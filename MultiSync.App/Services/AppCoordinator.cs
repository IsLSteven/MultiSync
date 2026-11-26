using System.Diagnostics;
using Microsoft.Win32;
using MultiSync.Core.Audio;
using MultiSync.Core.Discovery;
using MultiSync.Core.Models;
using MultiSync.Core.Network;
using MultiSync.Core.Topology;

namespace MultiSync.App.Services;

public sealed record LanPeerItem(
    string Name,
    string IpAddress,
    int Width,
    int Height,
    DeviceRole Role,
    int ControlPort,
    bool IsPaired,
    bool IsConnectedLive);

public sealed class AppCoordinator : IDisposable
{
    private const string AutoStartRegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "MultiSync";

    public ConfigStore Store { get; }
    public KvmSessionEngine Engine { get; }
    public LanDiscoveryAndControlService Lan { get; }
    public SharedAudioEngine Audio { get; }

    public event Action? StateUpdated;

    public AppCoordinator()
    {
        Store = new ConfigStore();
        var initial = Store.GetSnapshot();

        Engine = new KvmSessionEngine(initial);
        Engine.StatusChanged += () => StateUpdated?.Invoke();

        Audio = new SharedAudioEngine();
        Audio.StatusChanged += () => StateUpdated?.Invoke();

        Lan = new LanDiscoveryAndControlService(
            Store,
            onRemoteConfigApplied: incoming =>
            {
                Engine.Reload(incoming);
                StateUpdated?.Invoke();
            },
            onAudioControlRequested: (action, receiverIp, audioPort) =>
            {
                try
                {
                    var cfg = Store.GetSnapshot();
                    if (string.Equals(action, "start-send", StringComparison.OrdinalIgnoreCase))
                    {
                        Audio.StartSending(receiverIp, cfg.SessionKey, audioPort > 0 ? audioPort : SharedAudioEngine.DefaultAudioPort);
                    }
                    else if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        Audio.Stop();
                    }
                }
                catch
                {
                    // ignore transient audio capture/device errors
                }
                StateUpdated?.Invoke();
            });
        Lan.DevicesUpdated += () => StateUpdated?.Invoke();
    }

    public void StartAll()
    {
        _ = Task.Run(EnsureFirewallRulesSilent);
        Engine.Start();
        Lan.Start();
    }

    public List<LanPeerItem> GetLanPeersSnapshot()
    {
        var cfg = Store.GetSnapshot();
        var connected = Engine.ConnectedPeers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, LanDeviceBeacon>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in Lan.GetDiscoveredDevices())
            map[b.Name] = b;

        foreach (var paired in cfg.PairedDevices.Values)
        {
            if (!map.ContainsKey(paired.Name))
            {
                map[paired.Name] = new LanDeviceBeacon(
                    Kind: "paired",
                    InstanceId: "saved",
                    Name: paired.Name,
                    Addresses: string.IsNullOrWhiteSpace(paired.IpAddress) ? [] : [paired.IpAddress],
                    KvmPort: cfg.KvmPort,
                    ControlPort: cfg.ControlPort,
                    Role: DeviceRole.Slave,
                    Width: paired.Width,
                    Height: paired.Height);
            }
        }

        return map.Values
            .OrderByDescending(b => cfg.PairedDevices.ContainsKey(b.Name))
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new LanPeerItem(
                Name: b.Name,
                IpAddress: b.PrimaryAddress,
                Width: b.Width > 0 ? b.Width : 1920,
                Height: b.Height > 0 ? b.Height : 1080,
                Role: b.Role,
                ControlPort: b.ControlPort > 0 ? b.ControlPort : cfg.ControlPort,
                IsPaired: cfg.PairedDevices.ContainsKey(b.Name),
                IsConnectedLive: connected.Contains(b.Name) ||
                                 (cfg.Role == DeviceRole.Slave && Engine.SlaveConnectedToMaster && b.Role == DeviceRole.Master)))
            .ToList();
    }

    public async Task<(int RemoteSynced, List<string> Warnings)> ApplyVisualLayoutAsync(
        IReadOnlyList<DeviceNode> nodes,
        bool blockFullscreenEdge)
    {
        var current = Store.GetSnapshot();
        var compiled = TopologyCompiler.CompileCluster(
            nodes,
            sessionKey: current.SessionKey,
            kvmPort: current.KvmPort,
            discoveryPort: current.DiscoveryPort,
            controlPort: current.ControlPort,
            blockFullscreenEdge: blockFullscreenEdge,
            deadCornerPixels: current.DeadCornerPixels);

        var localNode = nodes.FirstOrDefault(n => n.IsLocal)
            ?? throw new InvalidOperationException("O computador local precisa estar no layout.");

        var warnings = new List<string>();
        var remoteCount = 0;

        foreach (var remoteNode in nodes.Where(n => !n.IsLocal))
        {
            if (!compiled.TryGetValue(remoteNode.Name, out var remoteCfg))
                continue;

            if (!current.PairedDevices.TryGetValue(remoteNode.Name, out var paired))
            {
                warnings.Add($"'{remoteNode.Name}' ainda não está pareado via PIN — faça o pareamento na aba lateral para sincronizar automaticamente.");
                continue;
            }

            var ip = !string.IsNullOrWhiteSpace(remoteNode.IpAddress) ? remoteNode.IpAddress : paired.IpAddress;
            if (string.IsNullOrWhiteSpace(ip))
            {
                warnings.Add($"Sem IP conhecido para '{remoteNode.Name}'.");
                continue;
            }

            try
            {
                await LanDiscoveryAndControlService.PushConfigToPeerAsync(
                    targetIp: ip,
                    controlPort: current.ControlPort,
                    senderName: localNode.Name,
                    sharedPairSecret: paired.SharedSecret,
                    targetConfig: remoteCfg);
                remoteCount++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Falha ao sincronizar '{remoteNode.Name}' ({ip}): {ex.Message}");
            }
        }

        var localCompiled = compiled[localNode.Name];
        localCompiled.PairingPin = current.PairingPin;
        localCompiled.PairedDevices = current.PairedDevices;
        Store.Save(localCompiled);
        Engine.Reload(localCompiled);

        return (remoteCount, warnings);
    }

    public async Task<(int StartedPeers, List<string> Warnings)> EnableReceiveAudioHereAsync()
    {
        var cfg = Store.GetSnapshot();
        var warnings = new List<string>();
        if (cfg.PairedDevices.Count == 0)
        {
            Audio.Stop();
            warnings.Add("Nenhum computador pareado encontrado. Pareie o segundo PC na aba lateral primeiro.");
            return (0, warnings);
        }

        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "";
        var audioPort = SharedAudioEngine.DefaultAudioPort;
        Audio.StartReceiving(cfg.SessionKey, audioPort);

        var discoveredByName = Lan.GetDiscoveredDevices()
            .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        var started = 0;
        foreach (var paired in cfg.PairedDevices.Values)
        {
            var ip = discoveredByName.TryGetValue(paired.Name, out var live) && !string.IsNullOrWhiteSpace(live.PrimaryAddress)
                ? live.PrimaryAddress
                : paired.IpAddress;

            if (string.IsNullOrWhiteSpace(ip))
            {
                warnings.Add($"Sem IP conhecido para '{paired.Name}'.");
                continue;
            }

            try
            {
                await LanDiscoveryAndControlService.SendAudioControlAsync(
                    targetIp: ip,
                    controlPort: cfg.ControlPort,
                    senderName: cfg.DeviceName,
                    sharedPairSecret: paired.SharedSecret,
                    action: "start-send",
                    audioReceiverIp: myIp,
                    audioPort: audioPort);
                started++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Falha ao iniciar áudio em '{paired.Name}' ({ip}): {ex.Message}");
            }
        }

        if (started == 0)
            Audio.Stop();

        StateUpdated?.Invoke();
        return (started, warnings);
    }

    public async Task DisableSharedAudioAsync()
    {
        Audio.Stop();
        var cfg = Store.GetSnapshot();
        var discoveredByName = Lan.GetDiscoveredDevices()
            .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var paired in cfg.PairedDevices.Values)
        {
            var ip = discoveredByName.TryGetValue(paired.Name, out var live) && !string.IsNullOrWhiteSpace(live.PrimaryAddress)
                ? live.PrimaryAddress
                : paired.IpAddress;
            if (string.IsNullOrWhiteSpace(ip)) continue;

            try
            {
                await LanDiscoveryAndControlService.SendAudioControlAsync(
                    targetIp: ip,
                    controlPort: cfg.ControlPort,
                    senderName: cfg.DeviceName,
                    sharedPairSecret: paired.SharedSecret,
                    action: "stop",
                    audioReceiverIp: "",
                    audioPort: SharedAudioEngine.DefaultAudioPort);
            }
            catch
            {
                // best effort stop on peer
            }
        }

        StateUpdated?.Invoke();
    }

    public bool IsAutoStartEnabled()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Query /TN \"MultiSyncAutoStart\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(2000);
            if (proc?.ExitCode == 0) return true;
        }
        catch { }

        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: false);
        return key?.GetValue(AutoStartValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public void SetAutoStart(bool enable)
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (enable && !string.IsNullOrWhiteSpace(exe))
        {
            using var proc = Process.Start(new ProcessStartInfo(
                "schtasks.exe",
                $"/Create /TN \"MultiSyncAutoStart\" /TR \"\\\"{exe}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(3000);
        }
        else
        {
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN \"MultiSyncAutoStart\" /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(3000);
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: true);
            key?.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
        }
    }

    private void EnsureFirewallRulesSilent()
    {
        try
        {
            var cfg = Store.GetSnapshot();
            RunNetsh($"advfirewall firewall show rule name=\"MultiSync KVM\" >nul 2>&1 || netsh advfirewall firewall add rule name=\"MultiSync KVM\" dir=in action=allow protocol=TCP localport={cfg.KvmPort}");
            RunNetsh($"advfirewall firewall show rule name=\"MultiSync Discovery\" >nul 2>&1 || netsh advfirewall firewall add rule name=\"MultiSync Discovery\" dir=in action=allow protocol=UDP localport={cfg.DiscoveryPort}");
            RunNetsh($"advfirewall firewall show rule name=\"MultiSync Control\" >nul 2>&1 || netsh advfirewall firewall add rule name=\"MultiSync Control\" dir=in action=allow protocol=TCP localport={cfg.ControlPort}");
            RunNetsh($"advfirewall firewall show rule name=\"MultiSync Audio\" >nul 2>&1 || netsh advfirewall firewall add rule name=\"MultiSync Audio\" dir=in action=allow protocol=UDP localport={SharedAudioEngine.DefaultAudioPort}");
        }
        catch
        {
            // ignore if non-elevated
        }
    }

    private static void RunNetsh(string cmdChain)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c netsh {cmdChain}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(4000);
    }

    public void OpenFirewallPorts()
    {
        var cfg = Store.GetSnapshot();
        var script =
            $"New-NetFirewallRule -DisplayName 'MultiSync KVM (TCP {cfg.KvmPort})' -Direction Inbound -Protocol TCP -LocalPort {cfg.KvmPort} -Action Allow -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'MultiSync Discovery (UDP {cfg.DiscoveryPort})' -Direction Inbound -Protocol UDP -LocalPort {cfg.DiscoveryPort} -Action Allow -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'MultiSync Control (TCP {cfg.ControlPort})' -Direction Inbound -Protocol TCP -LocalPort {cfg.ControlPort} -Action Allow -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'MultiSync Audio (UDP {SharedAudioEngine.DefaultAudioPort})' -Direction Inbound -Protocol UDP -LocalPort {SharedAudioEngine.DefaultAudioPort} -Action Allow -ErrorAction SilentlyContinue";

        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    public void Dispose()
    {
        Audio.Dispose();
        Lan.Dispose();
        Engine.Dispose();
    }
}
