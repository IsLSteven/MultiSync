using System.Diagnostics;
using Microsoft.Win32;
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

    public event Action? StateUpdated;

    public AppCoordinator()
    {
        Store = new ConfigStore();
        var initial = Store.GetSnapshot();

        Engine = new KvmSessionEngine(initial);
        Engine.StatusChanged += () => StateUpdated?.Invoke();

        Lan = new LanDiscoveryAndControlService(Store, onRemoteConfigApplied: incoming =>
        {
            Engine.Reload(incoming);
            StateUpdated?.Invoke();
        });
        Lan.DevicesUpdated += () => StateUpdated?.Invoke();
    }

    public void StartAll()
    {
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

    public bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: false);
        return key?.GetValue(AutoStartValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, writable: true);
        if (key == null) return;
        if (enable)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
                key.SetValue(AutoStartValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
        }
    }

    public void OpenFirewallPorts()
    {
        var cfg = Store.GetSnapshot();
        var script =
            $"New-NetFirewallRule -DisplayName 'MultiSync KVM (TCP {cfg.KvmPort})' -Direction Inbound -Protocol TCP -LocalPort {cfg.KvmPort} -Action Allow -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'MultiSync Discovery (UDP {cfg.DiscoveryPort})' -Direction Inbound -Protocol UDP -LocalPort {cfg.DiscoveryPort} -Action Allow -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'MultiSync Control (TCP {cfg.ControlPort})' -Direction Inbound -Protocol TCP -LocalPort {cfg.ControlPort} -Action Allow -ErrorAction SilentlyContinue";

        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    public void Dispose()
    {
        Lan.Dispose();
        Engine.Dispose();
    }
}
