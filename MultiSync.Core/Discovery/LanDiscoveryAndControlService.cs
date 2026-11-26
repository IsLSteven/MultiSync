using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MultiSync.Core.Models;

namespace MultiSync.Core.Discovery;

public sealed record LanDeviceBeacon(
    string Kind,
    string InstanceId,
    string Name,
    List<string> Addresses,
    int KvmPort,
    int ControlPort,
    DeviceRole Role,
    int Width,
    int Height)
{
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PrimaryAddress => Addresses.FirstOrDefault() ?? "";
}

public sealed record PairRequestMessage(
    string RequesterName,
    string RequesterIp,
    int RequesterWidth,
    int RequesterHeight,
    string Pin,
    string ProposedPairSecret,
    string ProposedSessionKey);

public sealed record PairResponseMessage(
    bool Success,
    string? Error,
    string DeviceName,
    string IpAddress,
    int Width,
    int Height,
    string AcceptedSessionKey);

public sealed record PushLayoutMessage(
    string SenderName,
    long UnixSeconds,
    string TargetConfigJson,
    string SignatureHex);

public sealed record ControlAckMessage(bool Success, string? Error = null);

public sealed class LanDiscoveryAndControlService : IDisposable
{
    private readonly ConfigStore _store;
    private readonly Action<MultiSyncConfig> _onRemoteConfigApplied;
    private readonly ConcurrentDictionary<string, LanDeviceBeacon> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..12];
    private readonly CancellationTokenSource _cts = new();

    private Socket? _udpSocket;
    private TcpListener? _tcpControl;

    public event Action? DevicesUpdated;

    public LanDiscoveryAndControlService(ConfigStore store, Action<MultiSyncConfig> onRemoteConfigApplied)
    {
        _store = store;
        _onRemoteConfigApplied = onRemoteConfigApplied;
    }

    public void Start()
    {
        _ = Task.Run(() => RunUdpDiscoveryLoopAsync(_cts.Token));
        _ = Task.Run(() => RunTcpControlLoopAsync(_cts.Token));
    }

    public IReadOnlyList<LanDeviceBeacon> GetDiscoveredDevices()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-16);
        foreach (var kv in _discovered)
        {
            if (kv.Value.LastSeenUtc < cutoff)
                _discovered.TryRemove(kv.Key, out _);
        }
        return _discovered.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task BroadcastProbeAsync()
    {
        var probe = BuildLocalBeacon("probe");
        await SendUdpBroadcastAsync(probe, _cts.Token);
    }

    public async Task<PairResponseMessage> PairWithDeviceAsync(string targetIp, int controlPort, string pin, CancellationToken cancel = default)
    {
        var cfg = _store.GetSnapshot();
        var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
        var pairSecret = ConfigStore.GenerateToken(32);

        var req = new PairRequestMessage(
            RequesterName: cfg.DeviceName,
            RequesterIp: myIp,
            RequesterWidth: bounds.Width,
            RequesterHeight: bounds.Height,
            Pin: pin.Trim(),
            ProposedPairSecret: pairSecret,
            ProposedSessionKey: cfg.SessionKey);

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
        _store.Save(cfg);
        return resp;
    }

    public static async Task PushConfigToPeerAsync(
        string targetIp,
        int controlPort,
        string senderName,
        string sharedPairSecret,
        MultiSyncConfig targetConfig,
        CancellationToken cancel = default)
    {
        var configJson = JsonSerializer.Serialize(targetConfig, ConfigStore.JsonOptions);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = ConfigStore.ComputeHmacHex(sharedPairSecret, $"{senderName.ToLowerInvariant()}|{ts}|{configJson}");

        var msg = new PushLayoutMessage(senderName, ts, configJson, sig);
        var ack = await SendControlRpcAsync<PushLayoutMessage, ControlAckMessage>(targetIp, controlPort, "push-layout", msg, cancel);
        if (!ack.Success)
            throw new InvalidOperationException(ack.Error ?? $"Falha ao aplicar configuração em {targetIp}.");
    }

    private LanDeviceBeacon BuildLocalBeacon(string kind)
    {
        var cfg = _store.GetSnapshot();
        var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new LanDeviceBeacon(
            Kind: kind,
            InstanceId: _instanceId,
            Name: cfg.DeviceName,
            Addresses: LanAddressHelper.GetAllLocalIPv4(),
            KvmPort: cfg.KvmPort,
            ControlPort: cfg.ControlPort,
            Role: cfg.Role,
            Width: bounds.Width,
            Height: bounds.Height);
    }

    private async Task RunUdpDiscoveryLoopAsync(CancellationToken cancel)
    {
        var cfg = _store.GetSnapshot();
        try
        {
            _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _udpSocket.Bind(new IPEndPoint(IPAddress.Any, cfg.DiscoveryPort));
        }
        catch
        {
            return;
        }

        var broadcastTask = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                await SendUdpBroadcastAsync(BuildLocalBeacon("beacon"), cancel);
                try { await Task.Delay(TimeSpan.FromSeconds(3), cancel); }
                catch (OperationCanceledException) { break; }
            }
        }, cancel);

        var buffer = new byte[4096];
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var res = await _udpSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyEp, cancel);
                if (res.ReceivedBytes <= 0 || res.RemoteEndPoint is not IPEndPoint remoteIp) continue;

                var json = Encoding.UTF8.GetString(buffer, 0, res.ReceivedBytes);
                var packet = JsonSerializer.Deserialize<LanDeviceBeacon>(json, ConfigStore.JsonOptions);
                if (packet == null || string.IsNullOrWhiteSpace(packet.Name)) continue;

                var localCfg = _store.GetSnapshot();
                if (packet.InstanceId == _instanceId ||
                    string.Equals(packet.Name, localCfg.DeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var srcAddr = remoteIp.Address.ToString();
                var addrs = new List<string> { srcAddr };
                foreach (var a in packet.Addresses)
                    if (!addrs.Contains(a, StringComparer.Ordinal)) addrs.Add(a);

                _discovered[packet.Name] = packet with
                {
                    Addresses = addrs,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
                DevicesUpdated?.Invoke();

                if (packet.Kind == "probe")
                {
                    var reply = JsonSerializer.SerializeToUtf8Bytes(BuildLocalBeacon("beacon"), ConfigStore.JsonOptions);
                    await _udpSocket.SendToAsync(reply, SocketFlags.None, new IPEndPoint(remoteIp.Address, localCfg.DiscoveryPort), cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore malformed datagrams
            }
        }

        await broadcastTask;
    }

    private async Task SendUdpBroadcastAsync(LanDeviceBeacon beacon, CancellationToken cancel)
    {
        if (_udpSocket == null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(beacon, ConfigStore.JsonOptions);
        var port = _store.GetSnapshot().DiscoveryPort;

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var s in LanAddressHelper.GetActiveSubnets())
            targets.Add(s.Broadcast);

        foreach (var target in targets)
        {
            try { await _udpSocket.SendToAsync(payload, SocketFlags.None, new IPEndPoint(target, port), cancel); }
            catch { /* ignore unreachable NIC */ }
        }
    }

    private async Task RunTcpControlLoopAsync(CancellationToken cancel)
    {
        var port = _store.GetSnapshot().ControlPort;
        try
        {
            _tcpControl = new TcpListener(IPAddress.Any, port);
            _tcpControl.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcpControl.Start();
        }
        catch
        {
            return;
        }

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpControl.AcceptTcpClientAsync(cancel);
                _ = HandleControlClientAsync(client, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // continue accepting
            }
        }
    }

    private async Task HandleControlClientAsync(TcpClient client, CancellationToken serverCancel)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

            var env = await ReadFrameAsync<RpcEnvelope>(stream, timeout.Token);
            if (env.Method == "pair")
            {
                var req = JsonSerializer.Deserialize<PairRequestMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                var localCfg = _store.GetSnapshot();

                if (!ConfigStore.FixedTimeEquals(localCfg.PairingPin, req.Pin.Trim()))
                {
                    var fail = new PairResponseMessage(false, "PIN incorreto.", localCfg.DeviceName, "", 0, 0, "");
                    await WriteFrameAsync(stream, fail, timeout.Token);
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
                _store.Save(localCfg);

                var bounds = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
                var myIp = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
                var ok = new PairResponseMessage(true, null, localCfg.DeviceName, myIp, bounds.Width, bounds.Height, sessionKey);
                await WriteFrameAsync(stream, ok, timeout.Token);
            }
            else if (env.Method == "push-layout")
            {
                var req = JsonSerializer.Deserialize<PushLayoutMessage>(env.BodyJson, ConfigStore.JsonOptions)!;
                var localCfg = _store.GetSnapshot();

                if (!localCfg.PairedDevices.TryGetValue(req.SenderName, out var paired))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Dispositivo remetente não está pareado."), timeout.Token);
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (Math.Abs(now - req.UnixSeconds) > 120)
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Timestamp expirado."), timeout.Token);
                    return;
                }

                var expectedSig = ConfigStore.ComputeHmacHex(paired.SharedSecret, $"{req.SenderName.ToLowerInvariant()}|{req.UnixSeconds}|{req.TargetConfigJson}");
                if (!ConfigStore.FixedTimeEquals(expectedSig, req.SignatureHex))
                {
                    await WriteFrameAsync(stream, new ControlAckMessage(false, "Assinatura HMAC inválida."), timeout.Token);
                    return;
                }

                var incoming = JsonSerializer.Deserialize<MultiSyncConfig>(req.TargetConfigJson, ConfigStore.JsonOptions)
                    ?? throw new InvalidOperationException("Config JSON inválido.");

                // Preserve our local PIN and paired-devices table
                incoming.PairingPin = localCfg.PairingPin;
                incoming.PairedDevices = localCfg.PairedDevices;
                if (!string.IsNullOrWhiteSpace(remoteIp))
                    paired.IpAddress = remoteIp;

                _store.Save(incoming);
                await WriteFrameAsync(stream, new ControlAckMessage(true), timeout.Token);

                _ = Task.Run(() => _onRemoteConfigApplied(incoming));
            }
        }
    }

    private sealed record RpcEnvelope(string Method, string BodyJson);

    private static async Task<TResponse> SendControlRpcAsync<TRequest, TResponse>(
        string ip, int port, string method, TRequest payload, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, port, timeout.Token);
        await using var stream = tcp.GetStream();

        var env = new RpcEnvelope(method, JsonSerializer.Serialize(payload, ConfigStore.JsonOptions));
        await WriteFrameAsync(stream, env, timeout.Token);
        return await ReadFrameAsync<TResponse>(stream, timeout.Token);
    }

    private static async Task WriteFrameAsync<T>(Stream stream, T obj, CancellationToken cancel)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, ConfigStore.JsonOptions);
        var hdr = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(hdr, bytes.Length);
        await stream.WriteAsync(hdr, cancel);
        await stream.WriteAsync(bytes, cancel);
        await stream.FlushAsync(cancel);
    }

    private static async Task<T> ReadFrameAsync<T>(Stream stream, CancellationToken cancel)
    {
        var hdr = new byte[4];
        await stream.ReadExactlyAsync(hdr, cancel);
        var len = BinaryPrimitives.ReadInt32BigEndian(hdr);
        if (len <= 0 || len > 1024 * 1024)
            throw new InvalidDataException("Invalid control frame size.");
        var buf = new byte[len];
        await stream.ReadExactlyAsync(buf, cancel);
        return JsonSerializer.Deserialize<T>(buf, ConfigStore.JsonOptions)!;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udpSocket?.Dispose(); } catch { }
        try { _tcpControl?.Stop(); } catch { }
        _cts.Dispose();
    }
}
