using MultiSync.Core.Models;

namespace MultiSync.Core.Topology;

public static class TopologyCompiler
{
    /// <summary>
    /// Compiles a visual grid of monitors (GridX, GridY, IsMaster) into complete MultiSyncConfig
    /// instances for every participating computer, computing orthogonal Left/Right/Up/Down links
    /// and Master/Slave roles automatically.
    /// </summary>
    public static Dictionary<string, MultiSyncConfig> CompileCluster(
        IReadOnlyList<DeviceNode> nodes,
        string sessionKey,
        int kvmPort = MultiSyncConfig.DefaultKvmPort,
        int discoveryPort = MultiSyncConfig.DefaultDiscoveryPort,
        int controlPort = MultiSyncConfig.DefaultControlPort,
        bool blockFullscreenEdge = true,
        int deadCornerPixels = 0)
    {
        if (nodes.Count == 0)
            throw new InvalidOperationException("O layout precisa ter pelo menos um computador.");

        var master = nodes.FirstOrDefault(n => n.IsMaster) ?? nodes[0];
        if (string.IsNullOrWhiteSpace(master.IpAddress))
            throw new InvalidOperationException($"O computador Master '{master.Name}' precisa ter um endereço IP definido.");

        // Build full node list with computed directional links
        var compiledNodes = nodes.Select(n => new DeviceNode
        {
            Name = n.Name,
            IpAddress = n.IpAddress,
            Width = n.Width > 0 ? n.Width : 1920,
            Height = n.Height > 0 ? n.Height : 1080,
            GridX = n.GridX,
            GridY = n.GridY,
            IsMaster = string.Equals(n.Name, master.Name, StringComparison.OrdinalIgnoreCase),
            IsLocal = false,
            Links = ComputeLinksForNode(n, nodes),
        }).ToList();

        var configs = new Dictionary<string, MultiSyncConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in compiledNodes)
        {
            var isTargetMaster = string.Equals(target.Name, master.Name, StringComparison.OrdinalIgnoreCase);
            var localCopyOfNodes = compiledNodes.Select(cn => new DeviceNode
            {
                Name = cn.Name,
                IpAddress = cn.IpAddress,
                Width = cn.Width,
                Height = cn.Height,
                GridX = cn.GridX,
                GridY = cn.GridY,
                IsMaster = cn.IsMaster,
                IsLocal = string.Equals(cn.Name, target.Name, StringComparison.OrdinalIgnoreCase),
                Links = cn.Links.Select(l => new ScreenLink { Side = l.Side, TargetDevice = l.TargetDevice }).ToList(),
            }).ToList();

            configs[target.Name] = new MultiSyncConfig
            {
                DeviceName = target.Name,
                Role = isTargetMaster ? DeviceRole.Master : DeviceRole.Slave,
                KvmPort = kvmPort,
                DiscoveryPort = discoveryPort,
                ControlPort = controlPort,
                SessionKey = sessionKey,
                MasterIpAddress = master.IpAddress,
                BlockFullscreenEdge = blockFullscreenEdge,
                DeadCornerPixels = deadCornerPixels,
                LayoutNodes = localCopyOfNodes,
            };
        }

        return configs;
    }

    public static ScreenSide Opposite(this ScreenSide side) => side switch
    {
        ScreenSide.Left => ScreenSide.Right,
        ScreenSide.Right => ScreenSide.Left,
        ScreenSide.Up => ScreenSide.Down,
        ScreenSide.Down => ScreenSide.Up,
        _ => ScreenSide.Right,
    };

    /// <summary>
    /// Computes the exact pixel coordinate on the destination screen corresponding to the edge
    /// and proportional position where the cursor exited the source screen.
    /// </summary>
    public static (int X, int Y) ComputeEntryPoint(
        ScreenSide exitSide,
        int srcX,
        int srcY,
        int srcLeft,
        int srcTop,
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight)
    {
        var ratioX = Math.Clamp((double)(srcX - srcLeft) / Math.Max(1, srcWidth - 1), 0.0, 1.0);
        var ratioY = Math.Clamp((double)(srcY - srcTop) / Math.Max(1, srcHeight - 1), 0.0, 1.0);

        var maxDstX = Math.Max(1, dstWidth - 1);
        var maxDstY = Math.Max(1, dstHeight - 1);

        return exitSide switch
        {
            ScreenSide.Right => (2, (int)Math.Round(ratioY * maxDstY)),
            ScreenSide.Left => (Math.Max(0, maxDstX - 2), (int)Math.Round(ratioY * maxDstY)),
            ScreenSide.Down => ((int)Math.Round(ratioX * maxDstX), 2),
            ScreenSide.Up => ((int)Math.Round(ratioX * maxDstX), Math.Max(0, maxDstY - 2)),
            _ => (dstWidth / 2, dstHeight / 2),
        };
    }

    private static List<ScreenLink> ComputeLinksForNode(DeviceNode from, IReadOnlyList<DeviceNode> all)
    {
        var links = new List<ScreenLink>();
        foreach (var to in all)
        {
            if (string.Equals(from.Name, to.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            var dx = to.GridX - from.GridX;
            var dy = to.GridY - from.GridY;

            if (dy == 0 && dx > 0 && !all.Any(m => m.GridY == from.GridY && m.GridX > from.GridX && m.GridX < to.GridX))
                links.Add(new ScreenLink { Side = ScreenSide.Right, TargetDevice = to.Name });
            else if (dy == 0 && dx < 0 && !all.Any(m => m.GridY == from.GridY && m.GridX < from.GridX && m.GridX > to.GridX))
                links.Add(new ScreenLink { Side = ScreenSide.Left, TargetDevice = to.Name });
            else if (dx == 0 && dy > 0 && !all.Any(m => m.GridX == from.GridX && m.GridY > from.GridY && m.GridY < to.GridY))
                links.Add(new ScreenLink { Side = ScreenSide.Down, TargetDevice = to.Name });
            else if (dx == 0 && dy < 0 && !all.Any(m => m.GridX == from.GridX && m.GridY < from.GridY && m.GridY > to.GridY))
                links.Add(new ScreenLink { Side = ScreenSide.Up, TargetDevice = to.Name });
        }
        return links;
    }
}
