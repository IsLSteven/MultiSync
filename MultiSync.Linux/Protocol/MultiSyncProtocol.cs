using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiSync.Linux.Protocol;

public enum DeviceRole { Master, Slave }
public enum ScreenSide { Left, Right, Up, Down }
public enum MouseBtn : byte { Left = 1, Right = 2, Middle = 3, XButton1 = 4, XButton2 = 5 }

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

public sealed class DirectionalLink
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
    public List<DirectionalLink> Links { get; set; } = [];
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
    public string DeviceName { get; set; } = Environment.MachineName;
    public DeviceRole Role { get; set; } = DeviceRole.Slave;
    public int KvmPort { get; set; } = 5050;
    public int DiscoveryPort { get; set; } = 5051;
    public int ControlPort { get; set; } = 5052;
    public string PairingPin { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public string MasterIpAddress { get; set; } = "";
    public bool BlockFullscreenEdge { get; set; } = true;
    public int DeadCornerPixels { get; set; } = 0;
    public List<DeviceNode> LayoutNodes { get; set; } = [];
    public Dictionary<string, PairedDeviceRecord> PairedDevices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record FileTransferManifest(string TransferId, List<FileTransferEntry> Entries, long TotalBytes);
public sealed record FileTransferEntry(string RelativePath, long SizeBytes, bool IsDirectory);

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
    string RequesterName, string RequesterIp, int RequesterWidth, int RequesterHeight,
    string Pin, string ProposedPairSecret, string ProposedSessionKey);

public sealed record PairResponseMessage(
    bool Success, string? Error, string DeviceName, string IpAddress,
    int Width, int Height, string AcceptedSessionKey);

public sealed record PushLayoutMessage(string SenderName, long UnixSeconds, string TargetConfigJson, string SignatureHex);
public sealed record AudioControlMessage(string SenderName, long UnixSeconds, string Action, string TargetIp, int AudioPort, string SignatureHex);
public sealed record FileTransferHeaderMessage(string SenderName, long UnixSeconds, FileTransferManifest Manifest, string SignatureHex);
public sealed record ScreenStreamRequest(string SenderName, long UnixSeconds, int MaxWidth, int TargetFps, int JpegQuality, string SignatureHex);
public sealed record ControlAckMessage(bool Success, string? Error = null);

public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _sync = new();
    private MultiSyncConfig _current;

    public string FilePath { get; }

    public ConfigStore(string? customPath = null)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        FilePath = customPath ?? Path.Combine(baseDir, "multisync", "multisync.json");
        _current = LoadOrCreate();
    }

    public MultiSyncConfig GetSnapshot()
    {
        lock (_sync)
        {
            var json = JsonSerializer.Serialize(_current, JsonOptions);
            return JsonSerializer.Deserialize<MultiSyncConfig>(json, JsonOptions)!;
        }
    }

    public void Save(MultiSyncConfig cfg)
    {
        lock (_sync)
        {
            _current = cfg;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOptions), Encoding.UTF8);
        }
    }

    public string RegeneratePin()
    {
        var cfg = GetSnapshot();
        cfg.PairingPin = GeneratePin();
        Save(cfg);
        return cfg.PairingPin;
    }

    private MultiSyncConfig LoadOrCreate()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<MultiSyncConfig>(File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions);
                if (loaded != null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.DeviceName)) loaded.DeviceName = Environment.MachineName;
                    if (string.IsNullOrWhiteSpace(loaded.PairingPin)) loaded.PairingPin = GeneratePin();
                    if (string.IsNullOrWhiteSpace(loaded.SessionKey)) loaded.SessionKey = GenerateToken(24);
                    loaded.PairedDevices = new Dictionary<string, PairedDeviceRecord>(loaded.PairedDevices, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
            catch { }
        }

        var myIp = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
        var (w, h) = SystemInfo.DetectPrimaryResolution();
        var fresh = new MultiSyncConfig
        {
            DeviceName = Environment.MachineName,
            Role = DeviceRole.Slave,
            PairingPin = GeneratePin(),
            SessionKey = GenerateToken(24),
            LayoutNodes =
            [
                new DeviceNode
                {
                    Name = Environment.MachineName,
                    IpAddress = myIp,
                    Width = w,
                    Height = h,
                    GridX = 0,
                    GridY = 0,
                    IsMaster = false,
                    IsLocal = true,
                }
            ],
        };
        Save(fresh);
        return fresh;
    }

    public static string GeneratePin() => RandomNumberGenerator.GetInt32(1000, 10000).ToString();

    public static string GenerateToken(int numBytes)
    {
        var bytes = RandomNumberGenerator.GetBytes(numBytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static string ComputeHmacHex(string secret, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    public static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

public static class TopologyCompiler
{
    public static Dictionary<string, MultiSyncConfig> CompileCluster(
        IReadOnlyList<DeviceNode> layoutNodes,
        string sessionKey,
        int kvmPort = 5050,
        int discoveryPort = 5051,
        int controlPort = 5052,
        bool blockFullscreenEdge = true,
        int deadCornerPixels = 0)
    {
        if (layoutNodes.Count == 0)
            throw new InvalidOperationException("Layout precisa de pelo menos 1 computador.");

        var master = layoutNodes.FirstOrDefault(n => n.IsMaster) ?? layoutNodes[0];
        var normalized = layoutNodes.Select(n => new DeviceNode
        {
            Name = n.Name.Trim(),
            IpAddress = n.IpAddress.Trim(),
            Width = n.Width > 0 ? n.Width : 1920,
            Height = n.Height > 0 ? n.Height : 1080,
            GridX = n.GridX - master.GridX,
            GridY = n.GridY - master.GridY,
            IsMaster = ReferenceEquals(n, master),
            IsLocal = false,
            Links = [],
        }).ToList();

        foreach (var node in normalized)
        {
            node.Links.Clear();
            foreach (var side in Enum.GetValues<ScreenSide>())
            {
                var neighbor = FindBestNeighbor(node, normalized, side);
                if (neighbor != null)
                    node.Links.Add(new DirectionalLink { Side = side, TargetDevice = neighbor.Name });
            }
        }

        var masterNode = normalized.First(n => n.IsMaster);
        var result = new Dictionary<string, MultiSyncConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in normalized)
        {
            var clonedNodes = normalized.Select(n => new DeviceNode
            {
                Name = n.Name,
                IpAddress = n.IpAddress,
                Width = n.Width,
                Height = n.Height,
                GridX = n.GridX,
                GridY = n.GridY,
                IsMaster = n.IsMaster,
                IsLocal = string.Equals(n.Name, target.Name, StringComparison.OrdinalIgnoreCase),
                Links = n.Links.Select(l => new DirectionalLink { Side = l.Side, TargetDevice = l.TargetDevice }).ToList(),
            }).ToList();

            result[target.Name] = new MultiSyncConfig
            {
                DeviceName = target.Name,
                Role = target.IsMaster ? DeviceRole.Master : DeviceRole.Slave,
                KvmPort = kvmPort,
                DiscoveryPort = discoveryPort,
                ControlPort = controlPort,
                SessionKey = sessionKey,
                MasterIpAddress = masterNode.IpAddress,
                BlockFullscreenEdge = blockFullscreenEdge,
                DeadCornerPixels = deadCornerPixels,
                LayoutNodes = clonedNodes,
            };
        }

        return result;
    }

    public static (int X, int Y) ComputeEntryPoint(
        ScreenSide exitSide, int srcX, int srcY,
        int srcLeft, int srcTop, int srcW, int srcH, int dstW, int dstH)
    {
        var ratioX = Math.Clamp((double)(srcX - srcLeft) / Math.Max(1, srcW - 1), 0.0, 1.0);
        var ratioY = Math.Clamp((double)(srcY - srcTop) / Math.Max(1, srcH - 1), 0.0, 1.0);
        return exitSide switch
        {
            ScreenSide.Right => (2, (int)Math.Round(ratioY * Math.Max(1, dstH - 1))),
            ScreenSide.Left => (Math.Max(0, dstW - 3), (int)Math.Round(ratioY * Math.Max(1, dstH - 1))),
            ScreenSide.Down => ((int)Math.Round(ratioX * Math.Max(1, dstW - 1)), 2),
            ScreenSide.Up => ((int)Math.Round(ratioX * Math.Max(1, dstW - 1)), Math.Max(0, dstH - 3)),
            _ => (dstW / 2, dstH / 2),
        };
    }

    private static DeviceNode? FindBestNeighbor(DeviceNode origin, List<DeviceNode> all, ScreenSide side)
    {
        DeviceNode? best = null;
        int bestScore = int.MaxValue;
        foreach (var cand in all)
        {
            if (ReferenceEquals(cand, origin)) continue;
            int dx = cand.GridX - origin.GridX;
            int dy = cand.GridY - origin.GridY;
            var matches = side switch
            {
                ScreenSide.Right => dx > 0 && Math.Abs(dy) <= dx,
                ScreenSide.Left => dx < 0 && Math.Abs(dy) <= -dx,
                ScreenSide.Down => dy > 0 && Math.Abs(dx) <= dy,
                ScreenSide.Up => dy < 0 && Math.Abs(dx) <= -dy,
                _ => false,
            };
            if (!matches) continue;
            var primary = side is ScreenSide.Left or ScreenSide.Right ? Math.Abs(dx) : Math.Abs(dy);
            var secondary = side is ScreenSide.Left or ScreenSide.Right ? Math.Abs(dy) : Math.Abs(dx);
            var score = primary * 10 + secondary;
            if (score < bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }
}

public static class LanAddressHelper
{
    public static List<(IPAddress Address, IPAddress Broadcast)> GetActiveSubnets()
    {
        var list = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var name = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (name.Contains("docker") || name.Contains("br-") || name.Contains("veth") || name.Contains("virbr") || name.Contains("tailscale"))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address))
                    continue;
                var addr = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask?.GetAddressBytes() ?? [255, 255, 255, 0];
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(addr[i] | ~mask[i]);
                list.Add((ua.Address, new IPAddress(bcast)));
            }
        }
        return list;
    }

    public static List<string> GetAllLocalIPv4() =>
        GetActiveSubnets().Select(s => s.Address.ToString()).Distinct(StringComparer.Ordinal).ToList();

    public static string? GetPreferredIPv4() => GetAllLocalIPv4().FirstOrDefault();
}

public static class SystemInfo
{
    public static bool IsWayland => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static (int Width, int Height) DetectPrimaryResolution()
    {
        if (!OperatingSystem.IsLinux()) return (1920, 1080);

        // Try xrandr (works on X11 and XWayland across Debian, Mint, Ubuntu, Arch)
        var xrandr = ProcessRunner.RunCapture("xrandr", "--current", 1200);
        if (!string.IsNullOrWhiteSpace(xrandr))
        {
            foreach (var line in xrandr.Split('\n'))
            {
                if (!line.Contains('*')) continue;
                var trimmed = line.Trim();
                var resToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (resToken != null && resToken.Contains('x'))
                {
                    var parts = resToken.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 640 && h >= 480)
                        return (w, h);
                }
            }
        }

        // Try wlr-randr on Wayland compositors
        var wlr = ProcessRunner.RunCapture("wlr-randr", "", 1200);
        if (!string.IsNullOrWhiteSpace(wlr))
        {
            foreach (var line in wlr.Split('\n'))
            {
                if (!line.Contains("current")) continue;
                var token = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (token != null && token.Contains('x'))
                {
                    var parts = token.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                        return (w, h);
                }
            }
        }

        return (1920, 1080);
    }

    public static string DetectDistroSummary()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                foreach (var line in File.ReadAllLines("/etc/os-release"))
                {
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                        return line["PRETTY_NAME=".Length..].Trim().Trim('"');
                }
            }
        }
        catch { }
        return OperatingSystem.IsLinux() ? "Linux" : Environment.OSVersion.ToString();
    }
}

public static class ProcessRunner
{
    public static bool CommandExists(string cmd)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var outp = RunCapture("which", cmd, 800);
        return !string.IsNullOrWhiteSpace(outp);
    }

    public static string? RunCapture(string fileName, string arguments, int timeoutMs = 2000)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
