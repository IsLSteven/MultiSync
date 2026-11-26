using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MultiSync.Core.ClipboardSync;
using MultiSync.Core.Models;
using MultiSync.Core.Topology;
using MultiSync.Core.Win32;

namespace MultiSync.Core.Network;

public enum KvmPacketType : byte
{
    AuthChallenge = 1,
    AuthResponse = 2,
    AuthOk = 3,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    KeyEvent = 13,
    ClipboardText = 14,
    ClipboardImage = 15,
    FileTransferStart = 16,
    FileTransferChunk = 17,
    FileTransferComplete = 18,
    Ping = 20,
    Pong = 21,
}

public sealed class KvmSessionEngine : IDisposable
{
    private const int FileChunkBytes = 32 * 1024; // 32 KB chunks keep mouse input ultra-responsive during transfers

    private readonly Lock _lifecycleLock = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _fileSendCts;
    private Task? _runTask;
    private MasterInputCapture? _capture;
    private ClipboardSyncManager? _clipboard;
    private X509Certificate2? _tlsCert;

    private MultiSyncConfig _config;
    private readonly ConcurrentDictionary<string, ConnectedSlaveSession> _slaves = new(StringComparer.OrdinalIgnoreCase);

    // Slave-side active stream writer (for sending clipboard/files from Slave -> Master)
    private Func<KvmPacketType, byte[], Task>? _slaveOutboundSender;

    private volatile bool _lockedToCurrentScreen;
    private string? _activeRemoteDevice;
    private double _remoteCursorX;
    private double _remoteCursorY;
    private int _remoteWidth = 1920;
    private int _remoteHeight = 1080;

    public event Action? StatusChanged;
    public event Action<string>? TransferActivity;

    public KvmSessionEngine(MultiSyncConfig initialConfig)
    {
        _config = initialConfig;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };
    public DeviceRole CurrentRole => _config.Role;
    public bool IsLockedToScreen => _lockedToCurrentScreen;
    public string? ActiveRemoteDevice => _activeRemoteDevice;
    public double LatestPingMs { get; private set; }
    public bool SlaveConnectedToMaster { get; private set; }
    public IReadOnlyCollection<string> ConnectedPeers => _slaves.Keys.ToList();

    public void Start()
    {
        lock (_lifecycleLock)
        {
            StopInternal();
            ClipboardSyncManager.CleanupOldStagingFolders();

            _clipboard = new ClipboardSyncManager();
            _clipboard.LocalTextCopied += OnLocalTextCopied;
            _clipboard.LocalImageCopied += OnLocalImageCopied;
            _clipboard.LocalFilesCopied += OnLocalFilesCopied;
            _clipboard.Start();

            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = _config.Role == DeviceRole.Master
                ? Task.Run(() => RunMasterAsync(token), token)
                : Task.Run(() => RunSlaveAsync(token), token);
        }
        StatusChanged?.Invoke();
    }

    public void Reload(MultiSyncConfig updatedConfig)
    {
        lock (_lifecycleLock)
        {
            _config = updatedConfig;
        }
        Start();
    }

    public void ToggleCursorLock()
    {
        _lockedToCurrentScreen = !_lockedToCurrentScreen;
        StatusChanged?.Invoke();
    }

    // ==================== BIDIRECTIONAL CLIPBOARD & FILE TRANSFER ====================

    private void OnLocalTextCopied(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _ = BroadcastToPeersAsync(KvmPacketType.ClipboardText, bytes);
    }

    private void OnLocalImageCopied(byte[] pngBytes)
    {
        _ = BroadcastToPeersAsync(KvmPacketType.ClipboardImage, pngBytes);
    }

    private void OnLocalFilesCopied(List<string> paths)
    {
        if (_config.Role == DeviceRole.Master && _slaves.IsEmpty) return;
        if (_config.Role == DeviceRole.Slave && _slaveOutboundSender == null) return;

        var built = ClipboardSyncManager.BuildFileManifest(paths);
        if (built == null)
        {
            TransferActivity?.Invoke("Arquivos copiados excedem 512 MB (limite da transferência automática).");
            return;
        }

        _fileSendCts?.Cancel();
        _fileSendCts?.Dispose();
        _fileSendCts = CancellationTokenSource.CreateLinkedTokenSource(_runCts?.Token ?? CancellationToken.None);
        var cancel = _fileSendCts.Token;

        _ = Task.Run(() => StreamFilesToPeersAsync(built.Value.Manifest, built.Value.FullPathByRelative, cancel), cancel);
    }

    private async Task BroadcastToPeersAsync(KvmPacketType type, byte[] payload)
    {
        if (_config.Role == DeviceRole.Master)
        {
            foreach (var slave in _slaves.Values)
                await slave.SendAsync(type, payload);
        }
        else if (_slaveOutboundSender is { } send)
        {
            await send(type, payload);
        }
    }

    private async Task StreamFilesToPeersAsync(
        FileTransferManifest manifest,
        Dictionary<string, string> fullPaths,
        CancellationToken cancel)
    {
        try
        {
            TransferActivity?.Invoke($"Enviando {manifest.Entries.Count(e => !e.IsDirectory)} arquivo(s) ({FormatSize(manifest.TotalBytes)})...");

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ConfigStore.JsonOptions);
            await BroadcastToPeersAsync(KvmPacketType.FileTransferStart, manifestBytes);

            long sentBytes = 0;
            int lastPct = -1;
            var readBuf = new byte[FileChunkBytes];

            for (var i = 0; i < manifest.Entries.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var entry = manifest.Entries[i];
                if (entry.IsDirectory) continue;
                if (!fullPaths.TryGetValue(entry.RelativePath, out var diskPath) || !File.Exists(diskPath))
                    continue;

                await using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileChunkBytes, useAsync: true);
                int read;
                while ((read = await fs.ReadAsync(readBuf, cancel)) > 0)
                {
                    var packet = new byte[4 + read];
                    BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), i);
                    Buffer.BlockCopy(readBuf, 0, packet, 4, read);
                    await BroadcastToPeersAsync(KvmPacketType.FileTransferChunk, packet);

                    sentBytes += read;
                    var pct = manifest.TotalBytes > 0 ? (int)(sentBytes * 100 / manifest.TotalBytes) : 100;
                    if (pct != lastPct && (pct % 10 == 0 || pct == 100))
                    {
                        lastPct = pct;
                        TransferActivity?.Invoke($"Enviando arquivos: {pct}% ({FormatSize(sentBytes)} / {FormatSize(manifest.TotalBytes)})");
                    }

                    // Yield briefly so interactive mouse/keyboard packets stay silky-smooth
                    await Task.Yield();
                }
            }

            await BroadcastToPeersAsync(KvmPacketType.FileTransferComplete, Encoding.UTF8.GetBytes(manifest.TransferId));
            TransferActivity?.Invoke("Arquivos enviados! Pode apertar Ctrl+V no outro computador.");
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer copy
        }
        catch (Exception ex)
        {
            TransferActivity?.Invoke($"Erro ao transferir arquivos: {ex.Message}");
        }
    }

    private void HandleIncomingClipboardOrFilePacket(
        KvmPacketType pktType,
        byte[] data,
        ref IncomingFileReceiver? fileReceiver)
    {
        switch (pktType)
        {
            case KvmPacketType.ClipboardText:
                _clipboard?.ApplyRemoteText(Encoding.UTF8.GetString(data));
                break;

            case KvmPacketType.ClipboardImage:
                _clipboard?.ApplyRemoteImagePng(data);
                break;

            case KvmPacketType.FileTransferStart:
                fileReceiver?.Dispose();
                fileReceiver = null;
                try
                {
                    var manifest = JsonSerializer.Deserialize<FileTransferManifest>(data, ConfigStore.JsonOptions);
                    if (manifest != null)
                    {
                        fileReceiver = new IncomingFileReceiver(manifest, msg => TransferActivity?.Invoke(msg));
                        TransferActivity?.Invoke($"Recebendo {manifest.Entries.Count(e => !e.IsDirectory)} arquivo(s) ({FormatSize(manifest.TotalBytes)})...");
                    }
                }
                catch { }
                break;

            case KvmPacketType.FileTransferChunk when fileReceiver != null && data.Length >= 4:
                var entryIdx = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                fileReceiver.WriteChunk(entryIdx, data.AsSpan(4));
                break;

            case KvmPacketType.FileTransferComplete when fileReceiver != null:
                var topPaths = fileReceiver.CompleteAndGetTopLevelPaths();
                fileReceiver.Dispose();
                fileReceiver = null;
                if (topPaths.Count > 0)
                {
                    _clipboard?.ApplyRemoteFiles(topPaths);
                    TransferActivity?.Invoke($"Arquivos recebidos ({topPaths.Count} item(ns))! Aperte Ctrl+V para colar.");
                }
                break;
        }
    }

    // ==================== MASTER / SLAVE TLS TRANSPORT ====================

    private async Task RunMasterAsync(CancellationToken cancel)
    {
        _tlsCert ??= CreateSelfSignedTlsCert();

        _capture = new MasterInputCapture();
        _capture.LocalCursorMoved += OnMasterLocalMouseMove;
        _capture.RemoteMouseDelta += OnMasterRemoteMouseDelta;
        _capture.RemoteMouseButton += OnMasterRemoteMouseButton;
        _capture.RemoteMouseWheel += OnMasterRemoteMouseWheel;
        _capture.RemoteKey += OnMasterRemoteKey;
        _capture.ToggleScreenLockHotkey += ToggleCursorLock;
        _capture.SwitchScreenDirectionHotkey += OnMasterHotkeyDirectionJump;
        _capture.Start();

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, _config.KvmPort);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            var pingLoop = Task.Run(async () =>
            {
                while (await pingTimer.WaitForNextTickAsync(cancel))
                {
                    var nowTicks = StopwatchTicks();
                    var payload = new byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(payload, nowTicks);
                    foreach (var kv in _slaves)
                        _ = kv.Value.SendAsync(KvmPacketType.Ping, payload);
                }
            }, cancel);

            while (!cancel.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(cancel);
                tcp.NoDelay = true;
                _ = HandleIncomingSlaveAsync(tcp, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch { }
        finally
        {
            listener?.Stop();
            foreach (var s in _slaves.Values) s.Dispose();
            _slaves.Clear();
            _capture?.Dispose();
            _capture = null;
            _activeRemoteDevice = null;
            StatusChanged?.Invoke();
        }
    }

    private async Task HandleIncomingSlaveAsync(TcpClient tcp, CancellationToken cancel)
    {
        string? peerName = null;
        IncomingFileReceiver? fileReceiver = null;
        try
        {
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(_tlsCert!, clientCertificateRequired: false, checkCertificateRevocation: false);

            var nonce = ConfigStore.GenerateToken(16);
            await WritePacketAsync(ssl, KvmPacketType.AuthChallenge, Encoding.UTF8.GetBytes(nonce), cancel);

            var (type, body) = await ReadPacketAsync(ssl, cancel);
            if (type != KvmPacketType.AuthResponse) return;

            var parts = Encoding.UTF8.GetString(body).Split('|');
            if (parts.Length < 4) return;

            peerName = parts[0];
            var width = int.TryParse(parts[1], out var w) ? w : 1920;
            var height = int.TryParse(parts[2], out var h) ? h : 1080;
            var clientMac = parts[3];

            var expectedMac = ConfigStore.ComputeHmacHex(_config.SessionKey, $"{nonce}|{peerName}|{width}|{height}");
            if (!ConfigStore.FixedTimeEquals(expectedMac, clientMac))
                return;

            await WritePacketAsync(ssl, KvmPacketType.AuthOk, "OK"u8.ToArray(), cancel);

            var session = new ConnectedSlaveSession(peerName, width, height, tcp, ssl);
            if (_slaves.TryRemove(peerName, out var old)) old.Dispose();
            _slaves[peerName] = session;
            StatusChanged?.Invoke();

            while (!cancel.IsCancellationRequested)
            {
                var (pktType, pktData) = await ReadPacketAsync(ssl, cancel);
                if (pktType == KvmPacketType.Pong && pktData.Length == 8)
                {
                    var sentTicks = BinaryPrimitives.ReadInt64BigEndian(pktData);
                    var elapsedMs = (StopwatchTicks() - sentTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    LatestPingMs = Math.Round(elapsedMs, 1);
                    StatusChanged?.Invoke();
                }
                else
                {
                    HandleIncomingClipboardOrFilePacket(pktType, pktData, ref fileReceiver);
                }
            }
        }
        catch { }
        finally
        {
            fileReceiver?.Dispose();
            if (peerName != null && _slaves.TryRemove(peerName, out var removed))
            {
                removed.Dispose();
                if (string.Equals(_activeRemoteDevice, peerName, StringComparison.OrdinalIgnoreCase))
                    ReturnToLocalMasterScreen();
                StatusChanged?.Invoke();
            }
            else
            {
                tcp.Dispose();
            }
        }
    }

    private async Task RunSlaveAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var masterIp = _config.MasterIpAddress;
            if (string.IsNullOrWhiteSpace(masterIp))
            {
                await Task.Delay(1500, cancel);
                continue;
            }

            TcpClient? tcp = null;
            SslStream? ssl = null;
            IncomingFileReceiver? fileReceiver = null;
            SemaphoreSlim? writeLock = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    await tcp.ConnectAsync(masterIp, _config.KvmPort, connectTimeout.Token);
                }

                ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync("MultiSync-Master");

                var (chType, chBody) = await ReadPacketAsync(ssl, cancel);
                if (chType != KvmPacketType.AuthChallenge) continue;
                var nonce = Encoding.UTF8.GetString(chBody);

                var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
                var mac = ConfigStore.ComputeHmacHex(_config.SessionKey, $"{nonce}|{_config.DeviceName}|{bounds.Width}|{bounds.Height}");
                var resp = $"{_config.DeviceName}|{bounds.Width}|{bounds.Height}|{mac}";
                await WritePacketAsync(ssl, KvmPacketType.AuthResponse, Encoding.UTF8.GetBytes(resp), cancel);

                var (okType, _) = await ReadPacketAsync(ssl, cancel);
                if (okType != KvmPacketType.AuthOk) continue;

                writeLock = new SemaphoreSlim(1, 1);
                var activeSsl = ssl;
                var activeLock = writeLock;
                _slaveOutboundSender = async (t, p) =>
                {
                    await activeLock.WaitAsync(cancel);
                    try { await WritePacketAsync(activeSsl, t, p, cancel); }
                    catch { }
                    finally { activeLock.Release(); }
                };

                SlaveConnectedToMaster = true;
                StatusChanged?.Invoke();

                while (!cancel.IsCancellationRequested)
                {
                    var (pktType, data) = await ReadPacketAsync(ssl, cancel);
                    switch (pktType)
                    {
                        case KvmPacketType.MouseMove when data.Length == 8:
                            SlaveInputInjector.MoveCursorAbsolute(
                                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)));
                            break;

                        case KvmPacketType.MouseButton when data.Length == 2:
                            SlaveInputInjector.InjectMouseButton((MouseBtn)data[0], data[1] != 0);
                            break;

                        case KvmPacketType.MouseWheel when data.Length == 5:
                            SlaveInputInjector.InjectMouseWheel(
                                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                                data[4] != 0);
                            break;

                        case KvmPacketType.KeyEvent when data.Length == 8:
                            SlaveInputInjector.InjectKey(
                                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)),
                                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)),
                                data[4] != 0,
                                data[5] != 0,
                                (char)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2)));
                            break;

                        case KvmPacketType.Ping:
                            await _slaveOutboundSender(KvmPacketType.Pong, data);
                            break;

                        default:
                            HandleIncomingClipboardOrFilePacket(pktType, data, ref fileReceiver);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch { }
            finally
            {
                _slaveOutboundSender = null;
                SlaveConnectedToMaster = false;
                StatusChanged?.Invoke();
                fileReceiver?.Dispose();
                writeLock?.Dispose();
                ssl?.Dispose();
                tcp?.Dispose();
            }

            try { await Task.Delay(2000, cancel); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ==================== MASTER EDGE ROUTING ====================

    private void OnMasterLocalMouseMove(int x, int y)
    {
        if (_lockedToCurrentScreen) return;
        if (_config.BlockFullscreenEdge && Win32Native.IsFullscreenWindowFocused()) return;

        var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var corner = _config.DeadCornerPixels;

        if (corner > 0)
        {
            var inCorner = (x < bounds.Left + corner || x >= bounds.Right - corner) &&
                           (y < bounds.Top + corner || y >= bounds.Bottom - corner);
            if (inCorner) return;
        }

        ScreenSide? crossed = null;
        if (x >= bounds.Right - 1) crossed = ScreenSide.Right;
        else if (x <= bounds.Left) crossed = ScreenSide.Left;
        else if (y <= bounds.Top) crossed = ScreenSide.Up;
        else if (y >= bounds.Bottom - 1) crossed = ScreenSide.Down;

        if (crossed == null) return;

        var localNode = _config.LayoutNodes.FirstOrDefault(n => n.IsLocal || string.Equals(n.Name, _config.DeviceName, StringComparison.OrdinalIgnoreCase));
        var link = localNode?.Links.FirstOrDefault(l => l.Side == crossed.Value);
        if (link == null) return;

        if (!_slaves.TryGetValue(link.TargetDevice, out var targetSlave))
            return;

        EnterSlaveScreen(targetSlave, crossed.Value, x, y, bounds);
    }

    private void EnterSlaveScreen(ConnectedSlaveSession slave, ScreenSide exitSide, int localX, int localY, System.Drawing.Rectangle localBounds)
    {
        _activeRemoteDevice = slave.Name;
        _remoteWidth = slave.Width;
        _remoteHeight = slave.Height;

        var (entryX, entryY) = TopologyCompiler.ComputeEntryPoint(
            exitSide,
            localX,
            localY,
            localBounds.Left,
            localBounds.Top,
            localBounds.Width,
            localBounds.Height,
            _remoteWidth,
            _remoteHeight);

        _remoteCursorX = entryX;
        _remoteCursorY = entryY;

        // Keep Master's cursor parked right on the exact edge pixel where the user crossed!
        var edgeX = exitSide switch
        {
            ScreenSide.Right => localBounds.Right - 1,
            ScreenSide.Left => localBounds.Left,
            _ => Math.Clamp(localX, localBounds.Left, localBounds.Right - 1),
        };
        var edgeY = exitSide switch
        {
            ScreenSide.Down => localBounds.Bottom - 1,
            ScreenSide.Up => localBounds.Top,
            _ => Math.Clamp(localY, localBounds.Top, localBounds.Bottom - 1),
        };

        _capture?.EnterRemoteCapture(edgeX, edgeY);
        SendMouseMoveToSlave(slave, entryX, entryY);
        StatusChanged?.Invoke();
    }

    private void OnMasterRemoteMouseDelta(int dx, int dy)
    {
        var activeName = _activeRemoteDevice;
        if (activeName == null || !_slaves.TryGetValue(activeName, out var slave))
        {
            ReturnToLocalMasterScreen();
            return;
        }

        _remoteCursorX += dx;
        _remoteCursorY += dy;

        if (!_lockedToCurrentScreen)
        {
            ScreenSide? remoteExit = null;
            if (_remoteCursorX < 0) remoteExit = ScreenSide.Left;
            else if (_remoteCursorX >= _remoteWidth) remoteExit = ScreenSide.Right;
            else if (_remoteCursorY < 0) remoteExit = ScreenSide.Up;
            else if (_remoteCursorY >= _remoteHeight) remoteExit = ScreenSide.Down;

            if (remoteExit != null)
            {
                var remoteNode = _config.LayoutNodes.FirstOrDefault(n => string.Equals(n.Name, activeName, StringComparison.OrdinalIgnoreCase));
                var nextLink = remoteNode?.Links.FirstOrDefault(l => l.Side == remoteExit.Value);
                if (nextLink != null)
                {
                    if (string.Equals(nextLink.TargetDevice, _config.DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        ReturnToLocalMasterScreen(remoteExit.Value);
                        return;
                    }
                    if (_slaves.TryGetValue(nextLink.TargetDevice, out var anotherSlave))
                    {
                        var fakeBounds = new System.Drawing.Rectangle(0, 0, _remoteWidth, _remoteHeight);
                        EnterSlaveScreen(anotherSlave, remoteExit.Value, (int)Math.Clamp(_remoteCursorX, 0, _remoteWidth - 1), (int)Math.Clamp(_remoteCursorY, 0, _remoteHeight - 1), fakeBounds);
                        return;
                    }
                }
            }
        }

        _remoteCursorX = Math.Clamp(_remoteCursorX, 0, _remoteWidth - 1);
        _remoteCursorY = Math.Clamp(_remoteCursorY, 0, _remoteHeight - 1);
        SendMouseMoveToSlave(slave, (int)_remoteCursorX, (int)_remoteCursorY);
    }

    private void ReturnToLocalMasterScreen(ScreenSide? fromRemoteSide = null)
    {
        _activeRemoteDevice = null;
        var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var ratioX = Math.Clamp(_remoteCursorX / Math.Max(1, _remoteWidth - 1), 0, 1);
        var ratioY = Math.Clamp(_remoteCursorY / Math.Max(1, _remoteHeight - 1), 0, 1);

        var (lx, ly) = fromRemoteSide switch
        {
            ScreenSide.Left => (bounds.Right - 4, bounds.Top + (int)Math.Round(ratioY * (bounds.Height - 1))),
            ScreenSide.Right => (bounds.Left + 4, bounds.Top + (int)Math.Round(ratioY * (bounds.Height - 1))),
            ScreenSide.Up => (bounds.Left + (int)Math.Round(ratioX * (bounds.Width - 1)), bounds.Bottom - 4),
            ScreenSide.Down => (bounds.Left + (int)Math.Round(ratioX * (bounds.Width - 1)), bounds.Top + 4),
            _ => (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
        };

        _capture?.ExitRemoteCapture(lx, ly);
        StatusChanged?.Invoke();
    }

    private void OnMasterHotkeyDirectionJump(int stepX, int stepY)
    {
        var side = stepX > 0 ? ScreenSide.Right : stepX < 0 ? ScreenSide.Left : stepY > 0 ? ScreenSide.Down : ScreenSide.Up;
        var currentHost = _activeRemoteDevice ?? _config.DeviceName;
        var currentNode = _config.LayoutNodes.FirstOrDefault(n => string.Equals(n.Name, currentHost, StringComparison.OrdinalIgnoreCase));
        var link = currentNode?.Links.FirstOrDefault(l => l.Side == side);
        if (link == null) return;

        if (string.Equals(link.TargetDevice, _config.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            ReturnToLocalMasterScreen();
        }
        else if (_slaves.TryGetValue(link.TargetDevice, out var slave))
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            EnterSlaveScreen(slave, side, bounds.Width / 2, bounds.Height / 2, bounds);
        }
    }

    private void OnMasterRemoteMouseButton(MouseBtn btn, bool isDown)
    {
        if (_activeRemoteDevice != null && _slaves.TryGetValue(_activeRemoteDevice, out var slave))
            _ = slave.SendAsync(KvmPacketType.MouseButton, [(byte)btn, isDown ? (byte)1 : (byte)0]);
    }

    private void OnMasterRemoteMouseWheel(int delta, bool horizontal)
    {
        if (_activeRemoteDevice != null && _slaves.TryGetValue(_activeRemoteDevice, out var slave))
        {
            var buf = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), delta);
            buf[4] = horizontal ? (byte)1 : (byte)0;
            _ = slave.SendAsync(KvmPacketType.MouseWheel, buf);
        }
    }

    private void OnMasterRemoteKey(ushort vk, ushort scan, bool isDown, bool extended, char unicodeChar)
    {
        if (_activeRemoteDevice != null && _slaves.TryGetValue(_activeRemoteDevice, out var slave))
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), vk);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), scan);
            buf[4] = isDown ? (byte)1 : (byte)0;
            buf[5] = extended ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), unicodeChar);
            _ = slave.SendAsync(KvmPacketType.KeyEvent, buf);
        }
    }

    private static void SendMouseMoveToSlave(ConnectedSlaveSession slave, int x, int y)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(4, 4), y);
        _ = slave.SendAsync(KvmPacketType.MouseMove, buf);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    private static long StopwatchTicks() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static X509Certificate2 CreateSelfSignedTlsCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=MultiSync-Local", ecdsa, HashAlgorithmName.SHA256);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static async Task WritePacketAsync(Stream stream, KvmPacketType type, byte[] payload, CancellationToken cancel)
    {
        var hdr = new byte[5];
        hdr[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(1, 4), payload.Length);
        await stream.WriteAsync(hdr, cancel);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancel);
        await stream.FlushAsync(cancel);
    }

    private static async Task<(KvmPacketType Type, byte[] Payload)> ReadPacketAsync(Stream stream, CancellationToken cancel)
    {
        var hdr = new byte[5];
        await stream.ReadExactlyAsync(hdr, cancel);
        var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(1, 4));
        if (len < 0 || len > 24 * 1024 * 1024)
            throw new InvalidDataException("KVM frame size out of bounds.");
        var buf = len == 0 ? [] : new byte[len];
        if (len > 0)
            await stream.ReadExactlyAsync(buf, cancel);
        return ((KvmPacketType)hdr[0], buf);
    }

    private void StopInternal()
    {
        _fileSendCts?.Cancel();
        _fileSendCts?.Dispose();
        _fileSendCts = null;

        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;

        _clipboard?.Dispose();
        _clipboard = null;
    }

    public void Dispose()
    {
        lock (_lifecycleLock) StopInternal();
        _tlsCert?.Dispose();
    }

    private sealed class IncomingFileReceiver : IDisposable
    {
        private readonly FileTransferManifest _manifest;
        private readonly string _stagingDir;
        private readonly Action<string> _onProgress;
        private int _openEntryIndex = -1;
        private FileStream? _openStream;
        private long _receivedBytes;
        private int _lastPct = -1;

        public IncomingFileReceiver(FileTransferManifest manifest, Action<string> onProgress)
        {
            _manifest = manifest;
            _onProgress = onProgress;
            _stagingDir = Path.Combine(ClipboardSyncManager.GetStagingRoot(), manifest.TransferId);
            Directory.CreateDirectory(_stagingDir);

            foreach (var entry in manifest.Entries)
            {
                var safePath = SafeCombine(_stagingDir, entry.RelativePath);
                if (safePath == null) continue;
                if (entry.IsDirectory)
                    Directory.CreateDirectory(safePath);
                else
                {
                    var parent = Path.GetDirectoryName(safePath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    // Create empty file immediately so 0-byte files are preserved too
                    using (File.Create(safePath)) { }
                }
            }
        }

        public void WriteChunk(int entryIndex, ReadOnlySpan<byte> chunk)
        {
            if (entryIndex < 0 || entryIndex >= _manifest.Entries.Count) return;
            var entry = _manifest.Entries[entryIndex];
            if (entry.IsDirectory) return;

            if (_openEntryIndex != entryIndex)
            {
                _openStream?.Dispose();
                _openStream = null;
                _openEntryIndex = entryIndex;

                var safePath = SafeCombine(_stagingDir, entry.RelativePath);
                if (safePath == null) return;
                var parent = Path.GetDirectoryName(safePath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                _openStream = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 32768);
            }

            _openStream?.Write(chunk);
            _receivedBytes += chunk.Length;

            var pct = _manifest.TotalBytes > 0 ? (int)(_receivedBytes * 100 / _manifest.TotalBytes) : 100;
            if (pct != _lastPct && (pct % 10 == 0 || pct == 100))
            {
                _lastPct = pct;
                _onProgress($"Recebendo arquivos: {pct}% ({FormatSize(_receivedBytes)} / {FormatSize(_manifest.TotalBytes)})");
            }
        }

        public List<string> CompleteAndGetTopLevelPaths()
        {
            _openStream?.Dispose();
            _openStream = null;

            var top = new List<string>();
            if (Directory.Exists(_stagingDir))
            {
                top.AddRange(Directory.GetDirectories(_stagingDir));
                top.AddRange(Directory.GetFiles(_stagingDir));
            }
            return top;
        }

        private static string? SafeCombine(string root, string relative)
        {
            var fullRoot = Path.GetFullPath(root);
            var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? combined : null;
        }

        public void Dispose()
        {
            _openStream?.Dispose();
            _openStream = null;
        }
    }

    private sealed class ConnectedSlaveSession(string name, int width, int height, TcpClient tcp, SslStream ssl) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public string Name { get; } = name;
        public int Width { get; } = width;
        public int Height { get; } = height;

        public async Task SendAsync(KvmPacketType type, byte[] payload)
        {
            await _writeLock.WaitAsync();
            try
            {
                await WritePacketAsync(ssl, type, payload, CancellationToken.None);
            }
            catch { }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            try { ssl.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }
}
