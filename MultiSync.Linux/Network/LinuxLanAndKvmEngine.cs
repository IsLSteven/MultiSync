using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MultiSync.Linux.Audio;
using MultiSync.Linux.Clipboard;
using MultiSync.Linux.Input;
using MultiSync.Linux.Protocol;
using MultiSync.Linux.ScreenShare;

namespace MultiSync.Linux.Network;

public sealed class LinuxMultiSyncCoordinator : IDisposable
{
    private const int FastFileBufferBytes = 256 * 1024;

    private readonly CancellationTokenSource _cts = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..12];
    private readonly ConcurrentDictionary<string, LanDeviceBeacon> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectedSlaveSession> _slaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _kvmLock = new();

    private Socket? _udpDiscovery;
    private TcpListener? _tcpControl;
    private CancellationTokenSource? _kvmCts;
    private Task? _kvmTask;
    private X509Certificate2? _tlsCert;
    private Func<KvmPacketType, byte[], Task>? _slaveOutbound;
    private LinuxX11MasterCapture? _masterCapture;
    private string? _activeRemote;
    private double _remoteX;
    private double _remoteY;
    private int _remoteW = 1920;
    private int _remoteH = 1080;

    public ConfigStore Store { get; }
    public LinuxInputInjector Injector { get; }
    public LinuxClipboardManager Clipboard { get; }
    public LinuxSharedAudioEngine Audio { get; }

    public bool SlaveConnectedToMaster { get; private set; }
    public double LatestPingMs { get; private set; }
    public string LastStatusMessage { get; private set; } = "Pronto.";
    public IReadOnlyCollection<string> ConnectedSlaves => _slaves.Keys.ToList();

    public LinuxMultiSyncCoordinator()
    {
        Store = new ConfigStore();
        Injector = new LinuxInputInjector();
        Clipboard = new LinuxClipboardManager();
        Audio = new LinuxSharedAudioEngine();

        Clipboard.LocalTextCopied += OnLocalTextCopied;
        Clipboard.LocalImageCopied += OnLocalImageCopied;
        Clipboard.LocalFilesCopied += OnLocalFilesCopied;
    }

    public void Start()
    {
        Clipboard.Start();
        _ = Task.Run(() => RunUdpDiscoveryAsync(_cts.Token));
        _ = Task.Run(() => RunTcpControlAsync(_cts.Token));
        RestartKvm();
    }

    public void RestartKvm()
    {
        lock (_kvmLock)
        {
            _kvmCts?.Cancel();
            try { _kvmTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _kvmCts?.Dispose();
            _kvmCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var cfg = Store.GetSnapshot();
            var token = _kvmCts.Token;
            _kvmTask = cfg.Role == DeviceRole.Master
                ? Task.Run(() => RunMasterKvmAsync(cfg, token), token)
                : Task.Run(() => RunSlaveKvmAsync(cfg, token), token);
        }
    }

    public IReadOnlyList<LanDeviceBeacon> GetDiscoveredDevices()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-16);
        foreach (var kv in _discovered)
            if (kv.Value.LastSeenUtc < cutoff) _discovered.TryRemove(kv.Key, out _);
        return _discovered.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PairResponseMessage> PairWithDeviceAsync(string targetIp, int controlPort, string pin, CancellationToken cancel = default)
    {
        var cfg = Store.GetSnapshot();
        var (w, h) = SystemInfo.DetectPrimaryResolution();
        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
        var pairSecret = ConfigStore.GenerateToken(32);

        var req = new PairRequestMessage(cfg.DeviceName, myIp, w, h, pin.Trim(), pairSecret, cfg.SessionKey);
        var resp = await SendControlRpcAsync<PairRequestMessage, PairResponseMessage>(targetIp, controlPort, "pair", req, cancel);
        if (!resp.Success)
            throw new InvalidOperationException(resp.Error ?? "Falha no pareamento LAN.");

        cfg.SessionKey = resp.AcceptedSessionKey;
        cfg.PairedDevices[resp.DeviceName] = new PairedDeviceRecord
        {
            Name = resp.DeviceName,
            IpAddress = targetIp,
            SharedSecret = pairSecret,
            Width = resp.Width,
            Height = resp.Height,
            PairedAt = DateTimeOffset.UtcNow,
        };
        if (!cfg.LayoutNodes.Any(n => string.Equals(n.Name, resp.DeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            var maxX = cfg.LayoutNodes.Select(n => n.GridX).DefaultIfEmpty(0).Max();
            cfg.LayoutNodes.Add(new DeviceNode
            {
                Name = resp.DeviceName,
                IpAddress = targetIp,
                Width = resp.Width > 0 ? resp.Width : 1920,
                Height = resp.Height > 0 ? resp.Height : 1080,
                GridX = maxX + 1,
                GridY = 0,
                IsMaster = false,
                IsLocal = false,
            });
        }
        Store.Save(cfg);
        LastStatusMessage = $"Pareado com {resp.DeviceName} ({targetIp})!";
        return resp;
    }

    public async Task<int> ApplyLayoutAsync(List<DeviceNode> nodes, CancellationToken cancel = default)
    {
        var current = Store.GetSnapshot();
        var compiled = TopologyCompiler.CompileCluster(
            nodes, current.SessionKey, current.KvmPort, current.DiscoveryPort, current.ControlPort, current.BlockFullscreenEdge, current.DeadCornerPixels);

        var localNode = nodes.FirstOrDefault(n => n.IsLocal || string.Equals(n.Name, current.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Computador local ausente no layout.");

        var synced = 0;
        foreach (var remote in nodes.Where(n => !ReferenceEquals(n, localNode)))
        {
            if (!compiled.TryGetValue(remote.Name, out var remoteCfg)) continue;
            if (!current.PairedDevices.TryGetValue(remote.Name, out var paired)) continue;
            var ip = !string.IsNullOrWhiteSpace(remote.IpAddress) ? remote.IpAddress : paired.IpAddress;
            if (string.IsNullOrWhiteSpace(ip)) continue;

            var configJson = JsonSerializer.Serialize(remoteCfg, ConfigStore.JsonOptions);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"{localNode.Name.ToLowerInvariant()}|{ts}|{configJson}");
            var msg = new PushLayoutMessage(localNode.Name, ts, configJson, sig);
            var ack = await SendControlRpcAsync<PushLayoutMessage, ControlAckMessage>(ip, current.ControlPort, "push-layout", msg, cancel);
            if (ack.Success) synced++;
        }

        var localCfg = compiled[localNode.Name];
        localCfg.PairingPin = current.PairingPin;
        localCfg.PairedDevices = current.PairedDevices;
        Store.Save(localCfg);
        RestartKvm();
        LastStatusMessage = $"Layout sincronizado ({synced + 1} PC(s))!";
        return synced;
    }

    public async Task<int> EnableReceiveAudioHereAsync(CancellationToken cancel = default)
    {
        var cfg = Store.GetSnapshot();
        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "";
        Audio.StartReceiving(cfg.SessionKey, LinuxSharedAudioEngine.DefaultAudioPort);

        var started = 0;
        foreach (var paired in cfg.PairedDevices.Values)
        {
            if (string.IsNullOrWhiteSpace(paired.IpAddress)) continue;
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sig = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"audio|{cfg.DeviceName.ToLowerInvariant()}|{ts}|start-send|{myIp}|{LinuxSharedAudioEngine.DefaultAudioPort}");
                var msg = new AudioControlMessage(cfg.DeviceName, ts, "start-send", myIp, LinuxSharedAudioEngine.DefaultAudioPort, sig);
                var ack = await SendControlRpcAsync<AudioControlMessage, ControlAckMessage>(paired.IpAddress, cfg.ControlPort, "audio-control", msg, cancel);
                if (ack.Success) started++;
            }
            catch { }
        }
        if (started == 0) Audio.Stop();
        LastStatusMessage = started > 0 ? "Áudio compartilhado ativado neste PC Linux!" : "Nenhum peer pareado respondeu ao áudio.";
        return started;
    }

    public async Task DisableSharedAudioAsync(CancellationToken cancel = default)
    {
        Audio.Stop();
        var cfg = Store.GetSnapshot();
        foreach (var paired in cfg.PairedDevices.Values)
        {
            if (string.IsNullOrWhiteSpace(paired.IpAddress)) continue;
            try
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sig = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"audio|{cfg.DeviceName.ToLowerInvariant()}|{ts}|stop||{LinuxSharedAudioEngine.DefaultAudioPort}");
                var msg = new AudioControlMessage(cfg.DeviceName, ts, "stop", "", LinuxSharedAudioEngine.DefaultAudioPort, sig);
                await SendControlRpcAsync<AudioControlMessage, ControlAckMessage>(paired.IpAddress, cfg.ControlPort, "audio-control", msg, cancel);
            }
            catch { }
        }
        LastStatusMessage = "Áudio compartilhado desligado.";
    }

    public async Task<(TcpClient Tcp, NetworkStream Stream)> ConnectPeerScreenStreamAsync(string targetIp, int maxWidth, int fps, int quality, CancellationToken cancel)
    {
        var cfg = Store.GetSnapshot();
        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1024 * 1024 };
        try
        {
            await tcp.ConnectAsync(targetIp, cfg.ControlPort, cancel);
            var stream = tcp.GetStream();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sig = ConfigStore.ComputeHmacHex(cfg.SessionKey, $"screen|{cfg.DeviceName.ToLowerInvariant()}|{ts}|{maxWidth}|{fps}|{quality}");
            var req = new ScreenStreamRequest(cfg.DeviceName, ts, maxWidth, fps, quality, sig);
            await WriteFrameAsync(stream, new RpcEnvelope("screen-stream", JsonSerializer.Serialize(req, ConfigStore.JsonOptions)), cancel);
            var ack = await ReadFrameAsync<ControlAckMessage>(stream, cancel);
            if (!ack.Success) throw new InvalidOperationException(ack.Error ?? "Peer recusou espelhamento.");
            return (tcp, stream);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // ==================== CLIPBOARD & 256KB FILE TRANSFER ====================

    private void OnLocalTextCopied(string text) => _ = BroadcastKvmAsync(KvmPacketType.ClipboardText, Encoding.UTF8.GetBytes(text));
    private void OnLocalImageCopied(byte[] png) => _ = BroadcastKvmAsync(KvmPacketType.ClipboardImage, png);

    private void OnLocalFilesCopied(List<string> paths)
    {
        var cfg = Store.GetSnapshot();
        var targets = cfg.Role == DeviceRole.Master
            ? _slaves.Values.Select(s => s.RemoteIp).Where(ip => !string.IsNullOrWhiteSpace(ip)).ToList()
            : (SlaveConnectedToMaster && !string.IsNullOrWhiteSpace(cfg.MasterIpAddress) ? [cfg.MasterIpAddress] : []);
        if (targets.Count == 0) return;

        var built = LinuxClipboardManager.BuildFileManifest(paths);
        if (built == null) return;

        _ = Task.Run(async () =>
        {
            foreach (var ip in targets)
            {
                try
                {
                    await SendFilesToPeerAsync(ip, cfg.ControlPort, cfg.DeviceName, cfg.SessionKey, built.Value.Manifest, built.Value.FullPathByRelative, _cts.Token);
                    LastStatusMessage = $"Arquivos enviados para {ip}! Aperte Ctrl+V lá.";
                }
                catch (Exception ex) { LastStatusMessage = $"Erro ao enviar arquivos: {ex.Message}"; }
            }
        });
    }

    private async Task BroadcastKvmAsync(KvmPacketType type, byte[] payload)
    {
        var cfg = Store.GetSnapshot();
        if (cfg.Role == DeviceRole.Master)
        {
            foreach (var s in _slaves.Values) await s.SendAsync(type, payload);
        }
        else if (_slaveOutbound is { } send)
        {
            await send(type, payload);
        }
    }

    public static async Task SendFilesToPeerAsync(
        string targetIp, int controlPort, string senderName, string sessionKey,
        FileTransferManifest manifest, Dictionary<string, string> fullPaths, CancellationToken cancel)
    {
        using var tcp = new TcpClient { NoDelay = true, SendBufferSize = 512 * 1024 };
        await tcp.ConnectAsync(targetIp, controlPort, cancel);
        await using var stream = tcp.GetStream();

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = ConfigStore.ComputeHmacHex(sessionKey, $"files|{senderName.ToLowerInvariant()}|{ts}|{manifest.TransferId}|{manifest.TotalBytes}");
        var header = new FileTransferHeaderMessage(senderName, ts, manifest, sig);
        await WriteFrameAsync(stream, new RpcEnvelope("file-transfer", JsonSerializer.Serialize(header, ConfigStore.JsonOptions)), cancel);
        var ack = await ReadFrameAsync<ControlAckMessage>(stream, cancel);
        if (!ack.Success) throw new InvalidOperationException(ack.Error ?? "Peer recusou transferência.");

        var buf = new byte[FastFileBufferBytes];
        foreach (var entry in manifest.Entries)
        {
            if (entry.IsDirectory) continue;
            await using var fs = new FileStream(fullPaths[entry.RelativePath], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FastFileBufferBytes, true);
            long rem = entry.SizeBytes;
            while (rem > 0)
            {
                var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, rem)), cancel);
                if (n <= 0) break;
                await stream.WriteAsync(buf.AsMemory(0, n), cancel);
                rem -= n;
            }
        }
        await stream.FlushAsync(cancel);
        await ReadFrameAsync<ControlAckMessage>(stream, cancel);
    }

    // ==================== LAN DISCOVERY (UDP 5051) & CONTROL RPC (TCP 5052) ====================

    private LanDeviceBeacon BuildLocalBeacon(string kind)
    {
        var cfg = Store.GetSnapshot();
        var (w, h) = SystemInfo.DetectPrimaryResolution();
        return new LanDeviceBeacon(kind, _instanceId, cfg.DeviceName, LanAddressHelper.GetAllLocalIPv4(), cfg.KvmPort, cfg.ControlPort, cfg.Role, w, h);
    }

    private async Task RunUdpDiscoveryAsync(CancellationToken cancel)
    {
        var port = Store.GetSnapshot().DiscoveryPort;
        try
        {
            _udpDiscovery = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpDiscovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpDiscovery.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _udpDiscovery.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch { return; }

        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                await BroadcastBeaconAsync(BuildLocalBeacon("beacon"), cancel);
                try { await Task.Delay(3000, cancel); } catch { break; }
            }
        }, cancel);

        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var res = await _udpDiscovery.ReceiveFromAsync(buf, SocketFlags.None, any, cancel);
                if (res.ReceivedBytes <= 0 || res.RemoteEndPoint is not IPEndPoint remoteIp) continue;
                var pkt = JsonSerializer.Deserialize<LanDeviceBeacon>(Encoding.UTF8.GetString(buf, 0, res.ReceivedBytes), ConfigStore.JsonOptions);
                var localCfg = Store.GetSnapshot();
                if (pkt == null || pkt.InstanceId == _instanceId || string.Equals(pkt.Name, localCfg.DeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var addrs = new List<string> { remoteIp.Address.ToString() };
                foreach (var a in pkt.Addresses) if (!addrs.Contains(a)) addrs.Add(a);
                _discovered[pkt.Name] = pkt with { Addresses = addrs, LastSeenUtc = DateTimeOffset.UtcNow };

                if (pkt.Kind == "probe")
                {
                    var reply = JsonSerializer.SerializeToUtf8Bytes(BuildLocalBeacon("beacon"), ConfigStore.JsonOptions);
                    await _udpDiscovery.SendToAsync(reply, SocketFlags.None, new IPEndPoint(remoteIp.Address, localCfg.DiscoveryPort), cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch { }
        }
    }

    private async Task BroadcastBeaconAsync(LanDeviceBeacon beacon, CancellationToken cancel)
    {
        if (_udpDiscovery == null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(beacon, ConfigStore.JsonOptions);
        var port = Store.GetSnapshot().DiscoveryPort;
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var s in LanAddressHelper.GetActiveSubnets()) targets.Add(s.Broadcast);
        foreach (var t in targets)
        {
            try { await _udpDiscovery.SendToAsync(payload, SocketFlags.None, new IPEndPoint(t, port), cancel); }
            catch { }
        }
    }

    private async Task RunTcpControlAsync(CancellationToken cancel)
    {
        var port = Store.GetSnapshot().ControlPort;
        try
        {
            _tcpControl = new TcpListener(IPAddress.Any, port);
            _tcpControl.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcpControl.Start();
        }
        catch { return; }

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpControl.AcceptTcpClientAsync(cancel);
                _ = HandleControlClientAsync(client, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch { }
        }
    }

    private async Task HandleControlClientAsync(TcpClient client, CancellationToken serverCancel)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            using var rpcTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancel);
            rpcTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

            var env = await ReadFrameAsync<RpcEnvelope>(stream, rpcTimeout.Token);
            var localCfg = Store.GetSnapshot();

            if (env.Method == "pair")
            {
                var req = JsonSerializer.Deserialize<PairRequestMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                if (!ConfigStore.FixedTimeEquals(localCfg.PairingPin, req.Pin.Trim()))
                {
                    await WriteFrameAsync(stream, new PairResponseMessage(false, "PIN incorreto.", localCfg.DeviceName, "", 0, 0, ""), rpcTimeout.Token);
                    return;
                }
                var sessionKey = string.IsNullOrWhiteSpace(req.ProposedSessionKey) ? localCfg.SessionKey : req.ProposedSessionKey;
                localCfg.SessionKey = sessionKey;
                localCfg.PairedDevices[req.RequesterName] = new PairedDeviceRecord
                {
                    Name = req.RequesterName,
                    IpAddress = string.IsNullOrWhiteSpace(req.RequesterIp) ? remoteIp : req.RequesterIp,
                    SharedSecret = req.ProposedPairSecret,
                    Width = req.RequesterWidth,
                    Height = req.RequesterHeight,
                    PairedAt = DateTimeOffset.UtcNow,
                };
                Store.Save(localCfg);
                var (w, h) = SystemInfo.DetectPrimaryResolution();
                var myIp = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
                await WriteFrameAsync(stream, new PairResponseMessage(true, null, localCfg.DeviceName, myIp, w, h, sessionKey), rpcTimeout.Token);
                LastStatusMessage = $"Pareado com {req.RequesterName} ({remoteIp})!";
            }
            else if (env.Method == "push-layout")
            {
                var req = JsonSerializer.Deserialize<PushLayoutMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                if (!localCfg.PairedDevices.TryGetValue(req.SenderName, out var paired) ||
                    Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - req.UnixSeconds) > 120)
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Remetente não pareado ou timestamp inválido."), rpcTimeout.Token);
                    return;
                }
                var expected = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"{req.SenderName.ToLowerInvariant()}|{req.UnixSeconds}|{req.TargetConfigJson}");
                if (!ConfigStore.FixedTimeEquals(expected, req.SignatureHex))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Assinatura HMAC inválida."), rpcTimeout.Token);
                    return;
                }

                var incoming = JsonSerializer.Deserialize<MultiSyncConfig>(req.TargetConfigJson, ConfigStore.JsonOptions)!;
                incoming.PairingPin = localCfg.PairingPin;
                incoming.PairedDevices = localCfg.PairedDevices;
                if (!string.IsNullOrWhiteSpace(remoteIp)) paired.IpAddress = remoteIp;
                Store.Save(incoming);
                await WriteFrameAsync(stream, new ControlAckMessage(true), rpcTimeout.Token);
                LastStatusMessage = $"Layout atualizado por {req.SenderName} (Modo: {incoming.Role}).";
                _ = Task.Run(RestartKvm);
            }
            else if (env.Method == "audio-control")
            {
                var req = JsonSerializer.Deserialize<AudioControlMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                if (!localCfg.PairedDevices.TryGetValue(req.SenderName, out var paired))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Não pareado."), rpcTimeout.Token);
                    return;
                }
                var expected = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"audio|{req.SenderName.ToLowerInvariant()}|{req.UnixSeconds}|{req.Action}|{req.TargetIp}|{req.AudioPort}");
                if (!ConfigStore.FixedTimeEquals(expected, req.SignatureHex))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "HMAC inválido."), rpcTimeout.Token);
                    return;
                }
                var receiverIp = string.IsNullOrWhiteSpace(req.TargetIp) ? remoteIp : req.TargetIp;
                await WriteFrameAsync(stream, new ControlAckMessage(true), rpcTimeout.Token);
                if (req.Action.Equals("start-send", StringComparison.OrdinalIgnoreCase))
                    Audio.StartSending(receiverIp, localCfg.SessionKey, req.AudioPort > 0 ? req.AudioPort : LinuxSharedAudioEngine.DefaultAudioPort);
                else
                    Audio.Stop();
            }
            else if (env.Method == "file-transfer")
            {
                var req = JsonSerializer.Deserialize<FileTransferHeaderMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                var expected = ConfigStore.ComputeHmacHex(localCfg.SessionKey, $"files|{req.SenderName.ToLowerInvariant()}|{req.UnixSeconds}|{req.Manifest.TransferId}|{req.Manifest.TotalBytes}");
                if (!ConfigStore.FixedTimeEquals(expected, req.SignatureHex))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Chave de sessão inválida."), rpcTimeout.Token);
                    return;
                }
                client.ReceiveBufferSize = 512 * 1024;
                await WriteFrameAsync(stream, new ControlAckMessage(true), rpcTimeout.Token);

                var stagingDir = Path.Combine(LinuxClipboardManager.GetStagingRoot(), req.Manifest.TransferId);
                Directory.CreateDirectory(stagingDir);
                var fullRoot = Path.GetFullPath(stagingDir);
                var buf = new byte[FastFileBufferBytes];

                foreach (var entry in req.Manifest.Entries)
                {
                    var safePath = Path.GetFullPath(Path.Combine(fullRoot, entry.RelativePath));
                    if (!safePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid path");
                    if (entry.IsDirectory) { Directory.CreateDirectory(safePath); continue; }
                    var parent = Path.GetDirectoryName(safePath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    await using var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, FastFileBufferBytes, true);
                    long rem = entry.SizeBytes;
                    while (rem > 0)
                    {
                        var read = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, rem)), serverCancel);
                        if (read <= 0) throw new EndOfStreamException();
                        await fs.WriteAsync(buf.AsMemory(0, read), serverCancel);
                        rem -= read;
                    }
                }

                var top = Directory.GetDirectories(stagingDir).Concat(Directory.GetFiles(stagingDir)).ToList();
                Clipboard.ApplyRemoteFiles(top);
                LastStatusMessage = $"Recebido(s) {top.Count} item(ns) em {stagingDir} (pronto para Ctrl+V)!";
                await WriteFrameAsync(stream, new ControlAckMessage(true), serverCancel);
            }
            else if (env.Method == "screen-stream")
            {
                var req = JsonSerializer.Deserialize<ScreenStreamRequest>(env.BodyJson, ConfigStore.JsonOptions)!;
                var expected = ConfigStore.ComputeHmacHex(localCfg.SessionKey, $"screen|{req.SenderName.ToLowerInvariant()}|{req.UnixSeconds}|{req.MaxWidth}|{req.TargetFps}|{req.JpegQuality}");
                if (!ConfigStore.FixedTimeEquals(expected, req.SignatureHex))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Chave de sessão inválida."), rpcTimeout.Token);
                    return;
                }
                client.NoDelay = true;
                client.SendBufferSize = 1024 * 1024;
                await WriteFrameAsync(stream, new ControlAckMessage(true), rpcTimeout.Token);
                await LinuxScreenStreamer.ServeStreamAsync(stream, Injector, req.MaxWidth, req.TargetFps, req.JpegQuality, serverCancel);
            }
        }
    }

    // ==================== TLS KVM ENGINE (TCP 5050) ====================

    private async Task RunSlaveKvmAsync(MultiSyncConfig cfg, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(cfg.MasterIpAddress))
            {
                try { await Task.Delay(1500, cancel); } catch { break; }
                continue;
            }

            TcpClient? tcp = null;
            SslStream? ssl = null;
            SemaphoreSlim? writeLock = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(cfg.MasterIpAddress, cfg.KvmPort, cancel);
                ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync("MultiSync-Master");

                var (chType, chBody) = await ReadKvmPacketAsync(ssl, cancel);
                if (chType != KvmPacketType.AuthChallenge) continue;
                var nonce = Encoding.UTF8.GetString(chBody);

                var (w, h) = SystemInfo.DetectPrimaryResolution();
                Injector.UpdateScreenSize(w, h);
                var mac = ConfigStore.ComputeHmacHex(cfg.SessionKey, $"{nonce}|{cfg.DeviceName}|{w}|{h}");
                await WriteKvmPacketAsync(ssl, KvmPacketType.AuthResponse, Encoding.UTF8.GetBytes($"{cfg.DeviceName}|{w}|{h}|{mac}"), cancel);

                var (okType, _) = await ReadKvmPacketAsync(ssl, cancel);
                if (okType != KvmPacketType.AuthOk) continue;

                writeLock = new SemaphoreSlim(1, 1);
                var activeSsl = ssl;
                var activeLock = writeLock;
                _slaveOutbound = async (t, p) =>
                {
                    await activeLock.WaitAsync(cancel);
                    try { await WriteKvmPacketAsync(activeSsl, t, p, cancel); }
                    catch { }
                    finally { activeLock.Release(); }
                };

                SlaveConnectedToMaster = true;
                LastStatusMessage = $"Conectado ao Master ({cfg.MasterIpAddress}) — Backend: {Injector.ActiveBackend}";

                while (!cancel.IsCancellationRequested)
                {
                    var (pktType, data) = await ReadKvmPacketAsync(ssl, cancel);
                    switch (pktType)
                    {
                        case KvmPacketType.MouseMove when data.Length == 8:
                            Injector.MoveCursorAbsolute(
                                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)));
                            break;
                        case KvmPacketType.MouseButton when data.Length == 2:
                            Injector.InjectMouseButton((MouseBtn)data[0], data[1] != 0);
                            break;
                        case KvmPacketType.MouseWheel when data.Length == 5:
                            Injector.InjectMouseWheel(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)), data[4] != 0);
                            break;
                        case KvmPacketType.KeyEvent when data.Length == 8:
                            Injector.InjectKey(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)), data[4] != 0);
                            break;
                        case KvmPacketType.Ping:
                            await _slaveOutbound(KvmPacketType.Pong, data);
                            break;
                        case KvmPacketType.ClipboardText:
                            Clipboard.ApplyRemoteText(Encoding.UTF8.GetString(data));
                            break;
                        case KvmPacketType.ClipboardImage:
                            Clipboard.ApplyRemoteImagePng(data);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
            catch { }
            finally
            {
                _slaveOutbound = null;
                SlaveConnectedToMaster = false;
                writeLock?.Dispose();
                ssl?.Dispose();
                tcp?.Dispose();
            }

            try { await Task.Delay(2000, cancel); } catch { break; }
        }
    }

    private async Task RunMasterKvmAsync(MultiSyncConfig cfg, CancellationToken cancel)
    {
        _tlsCert ??= CreateSelfSignedTlsCert();
        var (localW, localH) = SystemInfo.DetectPrimaryResolution();

        _masterCapture = new LinuxX11MasterCapture();
        _masterCapture.LocalCursorMoved += (x, y) => OnLinuxMasterLocalMove(cfg, x, y, localW, localH);
        _masterCapture.RemoteMouseDelta += (dx, dy) => OnLinuxMasterRemoteDelta(cfg, dx, dy, localW, localH);
        _masterCapture.RemoteMouseButton += (b, d) =>
        {
            if (_activeRemote != null && _slaves.TryGetValue(_activeRemote, out var s))
                _ = s.SendAsync(KvmPacketType.MouseButton, [(byte)b, d ? (byte)1 : (byte)0]);
        };
        _masterCapture.RemoteMouseWheel += (delta, horiz) =>
        {
            if (_activeRemote != null && _slaves.TryGetValue(_activeRemote, out var s))
            {
                var buf = new byte[5];
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), delta);
                buf[4] = horiz ? (byte)1 : (byte)0;
                _ = s.SendAsync(KvmPacketType.MouseWheel, buf);
            }
        };
        _masterCapture.RemoteKey += (vk, isDown) =>
        {
            if (_activeRemote != null && _slaves.TryGetValue(_activeRemote, out var s))
            {
                var buf = new byte[8];
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), vk);
                buf[4] = isDown ? (byte)1 : (byte)0;
                _ = s.SendAsync(KvmPacketType.KeyEvent, buf);
            }
        };
        _masterCapture.Start();

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, cfg.KvmPort);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            _ = Task.Run(async () =>
            {
                while (await pingTimer.WaitForNextTickAsync(cancel))
                {
                    var payload = new byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(payload, System.Diagnostics.Stopwatch.GetTimestamp());
                    foreach (var s in _slaves.Values) _ = s.SendAsync(KvmPacketType.Ping, payload);
                }
            }, cancel);

            while (!cancel.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(cancel);
                tcp.NoDelay = true;
                _ = HandleIncomingSlaveAsync(cfg, tcp, cancel);
            }
        }
        catch { }
        finally
        {
            listener?.Stop();
            foreach (var s in _slaves.Values) s.Dispose();
            _slaves.Clear();
            _masterCapture?.Dispose();
            _masterCapture = null;
        }
    }

    private async Task HandleIncomingSlaveAsync(MultiSyncConfig cfg, TcpClient tcp, CancellationToken cancel)
    {
        string? peerName = null;
        try
        {
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(_tlsCert!, false, false);

            var nonce = ConfigStore.GenerateToken(16);
            await WriteKvmPacketAsync(ssl, KvmPacketType.AuthChallenge, Encoding.UTF8.GetBytes(nonce), cancel);
            var (type, body) = await ReadKvmPacketAsync(ssl, cancel);
            if (type != KvmPacketType.AuthResponse) return;

            var parts = Encoding.UTF8.GetString(body).Split('|');
            if (parts.Length < 4) return;
            peerName = parts[0];
            var w = int.TryParse(parts[1], out var pw) ? pw : 1920;
            var h = int.TryParse(parts[2], out var ph) ? ph : 1080;
            var expected = ConfigStore.ComputeHmacHex(cfg.SessionKey, $"{nonce}|{peerName}|{w}|{h}");
            if (!ConfigStore.FixedTimeEquals(expected, parts[3])) return;

            await WriteKvmPacketAsync(ssl, KvmPacketType.AuthOk, "OK"u8.ToArray(), cancel);
            var remoteIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            var session = new ConnectedSlaveSession(peerName, remoteIp, w, h, tcp, ssl);
            if (_slaves.TryRemove(peerName, out var old)) old.Dispose();
            _slaves[peerName] = session;

            while (!cancel.IsCancellationRequested)
            {
                var (pktType, data) = await ReadKvmPacketAsync(ssl, cancel);
                if (pktType == KvmPacketType.Pong && data.Length == 8)
                {
                    var sent = BinaryPrimitives.ReadInt64BigEndian(data);
                    LatestPingMs = Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1);
                }
                else if (pktType == KvmPacketType.ClipboardText)
                    Clipboard.ApplyRemoteText(Encoding.UTF8.GetString(data));
                else if (pktType == KvmPacketType.ClipboardImage)
                    Clipboard.ApplyRemoteImagePng(data);
            }
        }
        catch { }
        finally
        {
            if (peerName != null && _slaves.TryRemove(peerName, out var rem)) rem.Dispose();
            else tcp.Dispose();
        }
    }

    private void OnLinuxMasterLocalMove(MultiSyncConfig cfg, int x, int y, int localW, int localH)
    {
        ScreenSide? crossed = null;
        if (x >= localW - 1) crossed = ScreenSide.Right;
        else if (x <= 0) crossed = ScreenSide.Left;
        else if (y <= 0) crossed = ScreenSide.Up;
        else if (y >= localH - 1) crossed = ScreenSide.Down;
        if (crossed == null) return;

        var localNode = cfg.LayoutNodes.FirstOrDefault(n => n.IsLocal || string.Equals(n.Name, cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
        var link = localNode?.Links.FirstOrDefault(l => l.Side == crossed.Value);
        if (link == null || !_slaves.TryGetValue(link.TargetDevice, out var slave)) return;

        _activeRemote = slave.Name;
        _remoteW = slave.Width;
        _remoteH = slave.Height;
        var (ex, ey) = TopologyCompiler.ComputeEntryPoint(crossed.Value, x, y, 0, 0, localW, localH, _remoteW, _remoteH);
        _remoteX = ex;
        _remoteY = ey;
        _masterCapture?.EnterRemoteCapture(x, y);
        slave.QueueMouseMove(ex, ey);
    }

    private void OnLinuxMasterRemoteDelta(MultiSyncConfig cfg, int dx, int dy, int localW, int localH)
    {
        if (_activeRemote == null || !_slaves.TryGetValue(_activeRemote, out var slave)) return;
        _remoteX += dx;
        _remoteY += dy;

        ScreenSide? exit = null;
        if (_remoteX < 0) exit = ScreenSide.Left;
        else if (_remoteX >= _remoteW) exit = ScreenSide.Right;
        else if (_remoteY < 0) exit = ScreenSide.Up;
        else if (_remoteY >= _remoteH) exit = ScreenSide.Down;

        if (exit != null)
        {
            var remoteNode = cfg.LayoutNodes.FirstOrDefault(n => string.Equals(n.Name, _activeRemote, StringComparison.OrdinalIgnoreCase));
            var link = remoteNode?.Links.FirstOrDefault(l => l.Side == exit.Value);
            if (link != null && string.Equals(link.TargetDevice, cfg.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                var rx = Math.Clamp(_remoteX / Math.Max(1, _remoteW - 1), 0, 1);
                var ry = Math.Clamp(_remoteY / Math.Max(1, _remoteH - 1), 0, 1);
                var (lx, ly) = exit.Value switch
                {
                    ScreenSide.Left => (localW - 4, (int)Math.Round(ry * (localH - 1))),
                    ScreenSide.Right => (4, (int)Math.Round(ry * (localH - 1))),
                    ScreenSide.Up => ((int)Math.Round(rx * (localW - 1)), localH - 4),
                    _ => ((int)Math.Round(rx * (localW - 1)), 4),
                };
                _activeRemote = null;
                _masterCapture?.ExitRemoteCapture(lx, ly);
                return;
            }
        }

        _remoteX = Math.Clamp(_remoteX, 0, _remoteW - 1);
        _remoteY = Math.Clamp(_remoteY, 0, _remoteH - 1);
        slave.QueueMouseMove((int)_remoteX, (int)_remoteY);
    }

    // ==================== WIRE FRAMING HELPERS ====================

    private sealed record RpcEnvelope(string Method, string BodyJson);

    private static async Task<TResp> SendControlRpcAsync<TReq, TResp>(string ip, int port, string method, TReq payload, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, port, timeout.Token);
        await using var stream = tcp.GetStream();
        await WriteFrameAsync(stream, new RpcEnvelope(method, JsonSerializer.Serialize(payload, ConfigStore.JsonOptions)), timeout.Token);
        return await ReadFrameAsync<TResp>(stream, timeout.Token);
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T obj, CancellationToken cancel)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, ConfigStore.JsonOptions);
        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), bytes.Length);
        Buffer.BlockCopy(bytes, 0, frame, 4, bytes.Length);
        await stream.WriteAsync(frame, cancel);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancel)
    {
        var hdr = new byte[4];
        await stream.ReadExactlyAsync(hdr, cancel);
        var len = BinaryPrimitives.ReadInt32BigEndian(hdr);
        if (len <= 0 || len > 1024 * 1024) throw new InvalidDataException("Invalid RPC frame.");
        var buf = new byte[len];
        await stream.ReadExactlyAsync(buf, cancel);
        return JsonSerializer.Deserialize<T>(buf, ConfigStore.JsonOptions)!;
    }

    private static async Task WriteKvmPacketAsync(Stream stream, KvmPacketType type, byte[] payload, CancellationToken cancel)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        if (payload.Length > 0) Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
        await stream.WriteAsync(frame, cancel);
    }

    private static async Task<(KvmPacketType Type, byte[] Payload)> ReadKvmPacketAsync(Stream stream, CancellationToken cancel)
    {
        var hdr = new byte[5];
        await stream.ReadExactlyAsync(hdr, cancel);
        var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(1, 4));
        if (len < 0 || len > 24 * 1024 * 1024) throw new InvalidDataException("KVM frame out of bounds.");
        var buf = len == 0 ? [] : new byte[len];
        if (len > 0) await stream.ReadExactlyAsync(buf, cancel);
        return ((KvmPacketType)hdr[0], buf);
    }

    private static X509Certificate2 CreateSelfSignedTlsCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=MultiSync-Linux", ecdsa, HashAlgorithmName.SHA256);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _kvmCts?.Cancel();
        try { _udpDiscovery?.Dispose(); } catch { }
        try { _tcpControl?.Stop(); } catch { }
        Audio.Dispose();
        Clipboard.Dispose();
        Injector.Dispose();
        _masterCapture?.Dispose();
        _tlsCert?.Dispose();
        _cts.Dispose();
    }

    private sealed class ConnectedSlaveSession(string name, string remoteIp, int width, int height, TcpClient tcp, SslStream ssl) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private long _packedMove;
        private int _hasPending;
        private int _draining;
        private long _lastMoveTick;
        private volatile bool _disposed;

        public string Name { get; } = name;
        public string RemoteIp { get; } = remoteIp;
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void QueueMouseMove(int x, int y)
        {
            if (_disposed) return;
            Volatile.Write(ref _packedMove, ((long)x << 32) | (uint)y);
            Volatile.Write(ref _hasPending, 1);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                _ = DrainAsync();
        }

        private async Task DrainAsync()
        {
            try
            {
                while (!_disposed)
                {
                    if (Interlocked.Exchange(ref _hasPending, 0) == 0)
                    {
                        Volatile.Write(ref _draining, 0);
                        if (Volatile.Read(ref _hasPending) == 1 && Interlocked.CompareExchange(ref _draining, 1, 0) == 0) continue;
                        return;
                    }
                    var wait = 4 - (int)(Environment.TickCount64 - Volatile.Read(ref _lastMoveTick));
                    if (wait > 0) await Task.Delay(wait);

                    var p = Volatile.Read(ref _packedMove);
                    var frame = new byte[13];
                    frame[0] = (byte)KvmPacketType.MouseMove;
                    BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), 8);
                    BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), (int)(p >> 32));
                    BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(9, 4), (int)(uint)p);

                    await _writeLock.WaitAsync();
                    try { if (!_disposed) { await ssl.WriteAsync(frame); Volatile.Write(ref _lastMoveTick, Environment.TickCount64); } }
                    catch { }
                    finally { _writeLock.Release(); }
                }
            }
            catch { Volatile.Write(ref _draining, 0); }
        }

        public async Task SendAsync(KvmPacketType type, byte[] payload)
        {
            if (_disposed) return;
            await _writeLock.WaitAsync();
            try { if (!_disposed) await WriteKvmPacketAsync(ssl, type, payload, CancellationToken.None); }
            catch { }
            finally { _writeLock.Release(); }
        }

        public void Dispose()
        {
            _disposed = true;
            try { ssl.Dispose(); } catch { }
            try { tcp.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }
}
