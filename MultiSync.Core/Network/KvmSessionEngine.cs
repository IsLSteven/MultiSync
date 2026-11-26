using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
    Ping = 20,
    Pong = 21,
}

public sealed class KvmSessionEngine : IDisposable
{
    private readonly Lock _lifecycleLock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private MasterInputCapture? _capture;
    private X509Certificate2? _tlsCert;

    private MultiSyncConfig _config;
    private readonly ConcurrentDictionary<string, ConnectedSlaveSession> _slaves = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _lockedToCurrentScreen;
    private string? _activeRemoteDevice;
    private double _remoteCursorX;
    private double _remoteCursorY;
    private int _remoteWidth = 1920;
    private int _remoteHeight = 1080;
    private string _lastSyncedClipboard = "";

    public event Action? StatusChanged;

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
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch
        {
            // port bind failure
        }
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
                else if (pktType == KvmPacketType.ClipboardText)
                {
                    var txt = Encoding.UTF8.GetString(pktData);
                    SetLocalClipboardText(txt);
                }
            }
        }
        catch
        {
            // disconnected
        }
        finally
        {
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

                        case KvmPacketType.ClipboardText:
                            SetLocalClipboardText(Encoding.UTF8.GetString(data));
                            break;

                        case KvmPacketType.Ping:
                            await WritePacketAsync(ssl, KvmPacketType.Pong, data, cancel);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // retry reconnect
            }
            finally
            {
                SlaveConnectedToMaster = false;
                StatusChanged?.Invoke();
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

        var inCorner = (x < bounds.Left + corner || x >= bounds.Right - corner) &&
                       (y < bounds.Top + corner || y >= bounds.Bottom - corner);
        if (inCorner) return;

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

        var ratioX = Math.Clamp((double)(localX - localBounds.Left) / Math.Max(1, localBounds.Width), 0, 1);
        var ratioY = Math.Clamp((double)(localY - localBounds.Top) / Math.Max(1, localBounds.Height), 0, 1);

        (_remoteCursorX, _remoteCursorY) = exitSide switch
        {
            ScreenSide.Right => (2, ratioY * (_remoteHeight - 1)),
            ScreenSide.Left => (_remoteWidth - 3, ratioY * (_remoteHeight - 1)),
            ScreenSide.Down => (ratioX * (_remoteWidth - 1), 2),
            ScreenSide.Up => (ratioX * (_remoteWidth - 1), _remoteHeight - 3),
            _ => (_remoteWidth / 2.0, _remoteHeight / 2.0),
        };

        PushLocalClipboardIfChanged(slave);

        var parkX = localBounds.Left + localBounds.Width / 2;
        var parkY = localBounds.Top + localBounds.Height / 2;
        _capture?.EnterRemoteCapture(parkX, parkY);
        SendMouseMoveToSlave(slave, (int)_remoteCursorX, (int)_remoteCursorY);
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
        var ratioX = Math.Clamp(_remoteCursorX / Math.Max(1, _remoteWidth), 0, 1);
        var ratioY = Math.Clamp(_remoteCursorY / Math.Max(1, _remoteHeight), 0, 1);

        var (lx, ly) = fromRemoteSide switch
        {
            ScreenSide.Left => (bounds.Right - 4, bounds.Top + (int)(ratioY * (bounds.Height - 1))),
            ScreenSide.Right => (bounds.Left + 4, bounds.Top + (int)(ratioY * (bounds.Height - 1))),
            ScreenSide.Up => (bounds.Left + (int)(ratioX * (bounds.Width - 1)), bounds.Bottom - 4),
            ScreenSide.Down => (bounds.Left + (int)(ratioX * (bounds.Width - 1)), bounds.Top + 4),
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

    private void PushLocalClipboardIfChanged(ConnectedSlaveSession slave)
    {
        var txt = TryGetLocalClipboardText();
        if (!string.IsNullOrEmpty(txt) && txt != _lastSyncedClipboard && txt.Length <= 512_000)
        {
            _lastSyncedClipboard = txt;
            _ = slave.SendAsync(KvmPacketType.ClipboardText, Encoding.UTF8.GetBytes(txt));
        }
    }

    private static string? TryGetLocalClipboardText()
    {
        string? text = null;
        var t = new Thread(() =>
        {
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch { /* clipboard busy */ }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(150);
        return text;
    }

    private void SetLocalClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text) || text == _lastSyncedClipboard) return;
        _lastSyncedClipboard = text;
        var t = new Thread(() =>
        {
            try { Clipboard.SetText(text); }
            catch { /* clipboard busy */ }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
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
        if (len < 0 || len > 4 * 1024 * 1024)
            throw new InvalidDataException("KVM frame size out of bounds.");
        var buf = len == 0 ? [] : new byte[len];
        if (len > 0)
            await stream.ReadExactlyAsync(buf, cancel);
        return ((KvmPacketType)hdr[0], buf);
    }

    private void StopInternal()
    {
        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
    }

    public void Dispose()
    {
        lock (_lifecycleLock) StopInternal();
        _tlsCert?.Dispose();
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
            catch
            {
                // reader loop handles disconnect cleanup
            }
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
