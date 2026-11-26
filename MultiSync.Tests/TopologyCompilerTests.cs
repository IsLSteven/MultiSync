using MultiSync.Core.ClipboardSync;
using MultiSync.Core.Models;
using MultiSync.Core.Topology;
using NUnit.Framework;

namespace MultiSync.Tests;

[TestFixture]
public class TopologyCompilerTests
{
    [Test]
    public void CompileCluster_ComputesLeftRightUpDownLinksAndMasterSlaveRoles()
    {
        var nodes = new List<DeviceNode>
        {
            new() { Name = "PC-Principal", IpAddress = "192.168.2.5", GridX = 0, GridY = 0, IsMaster = true, IsLocal = true },
            new() { Name = "Notebook", IpAddress = "192.168.2.6", GridX = 1, GridY = 0, IsMaster = false, IsLocal = false },
            new() { Name = "Monitor-Cima", IpAddress = "192.168.2.7", GridX = 0, GridY = -1, IsMaster = false, IsLocal = false },
        };

        var compiled = TopologyCompiler.CompileCluster(nodes, "session-secret-xyz");

        Assert.That(compiled.Count, Is.EqualTo(3));
        Assert.That(compiled["PC-Principal"].Role, Is.EqualTo(DeviceRole.Master));
        Assert.That(compiled["Notebook"].Role, Is.EqualTo(DeviceRole.Slave));
        Assert.That(compiled["Notebook"].MasterIpAddress, Is.EqualTo("192.168.2.5"));

        var masterNode = compiled["PC-Principal"].LayoutNodes.First(n => n.Name == "PC-Principal");
        Assert.That(masterNode.Links.Any(l => l.Side == ScreenSide.Right && l.TargetDevice == "Notebook"), Is.True);
        Assert.That(masterNode.Links.Any(l => l.Side == ScreenSide.Up && l.TargetDevice == "Monitor-Cima"), Is.True);
    }

    [Test]
    public void CompileCluster_FlippingMasterToSecondMachine_UpdatesRolesAndDirections()
    {
        var nodes = new List<DeviceNode>
        {
            new() { Name = "PC-A", IpAddress = "192.168.2.5", GridX = 0, GridY = 0, IsMaster = false, IsLocal = true },
            new() { Name = "PC-B", IpAddress = "192.168.2.6", GridX = 1, GridY = 0, IsMaster = true, IsLocal = false },
        };

        var compiled = TopologyCompiler.CompileCluster(nodes, "flip-secret");

        Assert.That(compiled["PC-A"].Role, Is.EqualTo(DeviceRole.Slave));
        Assert.That(compiled["PC-A"].MasterIpAddress, Is.EqualTo("192.168.2.6"));
        Assert.That(compiled["PC-B"].Role, Is.EqualTo(DeviceRole.Master));

        var pcBNode = compiled["PC-B"].LayoutNodes.First(n => n.Name == "PC-B");
        Assert.That(pcBNode.Links.Any(l => l.Side == ScreenSide.Left && l.TargetDevice == "PC-A"), Is.True);
    }

    [Test]
    public void HmacAndPinVerification_WorksDeterministically()
    {
        var mac1 = ConfigStore.ComputeHmacHex("secret1", "payload");
        var mac2 = ConfigStore.ComputeHmacHex("secret1", "payload");
        var macWrong = ConfigStore.ComputeHmacHex("secret2", "payload");

        Assert.That(ConfigStore.FixedTimeEquals(mac1, mac2), Is.True);
        Assert.That(ConfigStore.FixedTimeEquals(mac1, macWrong), Is.False);
    }

    [Test]
    public void ComputeEntryPoint_EntersFromCorrespondingEdgeNotCenter()
    {
        // Crossing Right edge at top-right (1919, 120) -> lands at Left edge (x=2, y=120) on destination!
        var fromRight = TopologyCompiler.ComputeEntryPoint(ScreenSide.Right, 1919, 120, 0, 0, 1920, 1080, 1920, 1080);
        Assert.That(fromRight, Is.EqualTo((2, 120)));

        // Crossing Left edge at bottom-left (0, 950) -> lands at Right edge (x=1917, y=950) on destination!
        var fromLeft = TopologyCompiler.ComputeEntryPoint(ScreenSide.Left, 0, 950, 0, 0, 1920, 1080, 1920, 1080);
        Assert.That(fromLeft, Is.EqualTo((1917, 950)));

        // Crossing Up edge at (400, 0) -> lands at Bottom edge (x=400, y=1077) on destination!
        var fromUp = TopologyCompiler.ComputeEntryPoint(ScreenSide.Up, 400, 0, 0, 0, 1920, 1080, 1920, 1080);
        Assert.That(fromUp, Is.EqualTo((400, 1077)));

        // Crossing Down edge at (400, 1079) -> lands at Top edge (x=400, y=2) on destination!
        var fromDown = TopologyCompiler.ComputeEntryPoint(ScreenSide.Down, 400, 1079, 0, 0, 1920, 1080, 1920, 1080);
        Assert.That(fromDown, Is.EqualTo((400, 2)));
    }

    [Test]
    public void BuildFileManifest_CapturesFilesAndNestedFolders()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "MultiSyncTest_" + Guid.NewGuid().ToString("N")[..8]);
        var sub = Path.Combine(tempRoot, "PastaTeste", "Sub");
        Directory.CreateDirectory(sub);
        var f1 = Path.Combine(tempRoot, "PastaTeste", "a.txt");
        var f2 = Path.Combine(sub, "b.txt");
        File.WriteAllText(f1, "hello");
        File.WriteAllText(f2, "world!");

        try
        {
            var built = ClipboardSyncManager.BuildFileManifest([Path.Combine(tempRoot, "PastaTeste")]);
            Assert.That(built, Is.Not.Null);
            Assert.That(built!.Value.Manifest.TotalBytes, Is.EqualTo(11));
            Assert.That(built.Value.Manifest.Entries.Any(e => e.IsDirectory && e.RelativePath == "PastaTeste"), Is.True);
            Assert.That(built.Value.FullPathByRelative.Count, Is.EqualTo(2));

            var dropBuf = ClipboardSyncManager.BuildDropFilesBuffer([f1, f2]);
            Assert.That(BitConverter.ToInt32(dropBuf, 0), Is.EqualTo(20)); // DROPFILES.pFiles
            Assert.That(BitConverter.ToInt32(dropBuf, 16), Is.EqualTo(1)); // DROPFILES.fWide = TRUE
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
