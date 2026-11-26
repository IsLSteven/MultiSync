using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiSync.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceRole
{
    Master,
    Slave,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenSide
{
    Left,
    Right,
    Up,
    Down,
}

public sealed class ScreenLink
{
    public ScreenSide Side { get; set; }
    public string TargetDevice { get; set; } = "";
}

public sealed class DeviceNode
{
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int GridX { get; set; }
    public int GridY { get; set; }
    public bool IsMaster { get; set; }
    public bool IsLocal { get; set; }
    public List<ScreenLink> Links { get; set; } = [];
}

public sealed class PairedDeviceRecord
{
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string SharedSecret { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MultiSyncConfig
{
    public const int DefaultKvmPort = 5050;
    public const int DefaultDiscoveryPort = 5051;
    public const int DefaultControlPort = 5052;

    public string DeviceName { get; set; } = Environment.MachineName.Split('.')[0];
    public DeviceRole Role { get; set; } = DeviceRole.Master;
    public int KvmPort { get; set; } = DefaultKvmPort;
    public int DiscoveryPort { get; set; } = DefaultDiscoveryPort;
    public int ControlPort { get; set; } = DefaultControlPort;
    public string PairingPin { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public string? MasterIpAddress { get; set; }
    public bool BlockFullscreenEdge { get; set; } = true;
    public int DeadCornerPixels { get; set; } = 30;
    public List<DeviceNode> LayoutNodes { get; set; } = [];
    public Dictionary<string, PairedDeviceRecord> PairedDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lock _sync = new();
    private readonly string _configPath;
    private MultiSyncConfig _current;

    public ConfigStore(string? explicitPath = null)
    {
        _configPath = explicitPath ?? GetDefaultConfigPath();
        _current = LoadOrCreate();
    }

    public string ConfigPath => _configPath;

    public static string GetDefaultConfigPath()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiSync");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "multisync.json");
    }

    public MultiSyncConfig GetSnapshot()
    {
        lock (_sync)
        {
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            return Normalize(JsonSerializer.Deserialize<MultiSyncConfig>(json, JsonOptions) ?? new MultiSyncConfig());
        }
    }

    public void Save(MultiSyncConfig updated)
    {
        lock (_sync)
        {
            _current = Normalize(updated);
            WriteAtomic(_current);
        }
    }

    public string RegeneratePin()
    {
        lock (_sync)
        {
            _current.PairingPin = GeneratePin();
            WriteAtomic(_current);
            return _current.PairingPin;
        }
    }

    public static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(1000, 10000).ToString("D4");

    public static string GenerateToken(int byteCount = 24)
    {
        Span<byte> buf = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string ComputeHmacHex(string secret, string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        return Convert.ToHexString(HMACSHA256.HashData(keyBytes, msgBytes));
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private MultiSyncConfig LoadOrCreate()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<MultiSyncConfig>(File.ReadAllText(_configPath), JsonOptions);
                if (loaded != null)
                {
                    var norm = Normalize(loaded);
                    WriteAtomic(norm);
                    return norm;
                }
            }
            catch
            {
                // recreate default if corrupted
            }
        }

        var fresh = Normalize(new MultiSyncConfig());
        WriteAtomic(fresh);
        return fresh;
    }

    private static MultiSyncConfig Normalize(MultiSyncConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DeviceName))
            cfg.DeviceName = Environment.MachineName.Split('.')[0];
        if (string.IsNullOrWhiteSpace(cfg.PairingPin))
            cfg.PairingPin = GeneratePin();
        if (string.IsNullOrWhiteSpace(cfg.SessionKey))
            cfg.SessionKey = GenerateToken(24);
        if (cfg.KvmPort <= 0) cfg.KvmPort = MultiSyncConfig.DefaultKvmPort;
        if (cfg.DiscoveryPort <= 0) cfg.DiscoveryPort = MultiSyncConfig.DefaultDiscoveryPort;
        if (cfg.ControlPort <= 0) cfg.ControlPort = MultiSyncConfig.DefaultControlPort;

        var caseInsensitive = new Dictionary<string, PairedDeviceRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.PairedDevices)
            caseInsensitive[kv.Key] = kv.Value;
        cfg.PairedDevices = caseInsensitive;

        if (cfg.LayoutNodes.Count == 0)
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            cfg.LayoutNodes.Add(new DeviceNode
            {
                Name = cfg.DeviceName,
                IpAddress = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1",
                Width = bounds.Width,
                Height = bounds.Height,
                GridX = 0,
                GridY = 0,
                IsMaster = cfg.Role == DeviceRole.Master,
                IsLocal = true,
            });
        }

        return cfg;
    }

    private void WriteAtomic(MultiSyncConfig cfg)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        File.Move(tmp, _configPath, overwrite: true);
    }
}

public static class LanAddressHelper
{
    public sealed record NicSubnet(IPAddress Address, IPAddress Broadcast, bool HasGateway, string Name);

    public static List<NicSubnet> GetActiveSubnets()
    {
        var list = new List<NicSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = nic.GetIPProperties();
                var hasGw = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ipBytes = uni.Address.GetAddressBytes();
                    if (ipBytes[0] == 127 || (ipBytes[0] == 169 && ipBytes[1] == 254)) continue;

                    var maskBytes = uni.IPv4Mask?.GetAddressBytes();
                    var bcast = IPAddress.Broadcast;
                    if (maskBytes is { Length: 4 })
                    {
                        var outBytes = new byte[4];
                        for (var i = 0; i < 4; i++)
                            outBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        bcast = new IPAddress(outBytes);
                    }

                    list.Add(new NicSubnet(uni.Address, bcast, hasGw, nic.Name));
                }
            }
        }
        catch
        {
            // ignore enumeration restrictions
        }

        return list
            .OrderByDescending(n => n.HasGateway)
            .ThenBy(n => n.Address.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public static List<string> GetAllLocalIPv4() =>
        GetActiveSubnets().Select(s => s.Address.ToString()).Distinct(StringComparer.Ordinal).ToList();

    public static string? GetPreferredIPv4() =>
        GetAllLocalIPv4().FirstOrDefault();
}
