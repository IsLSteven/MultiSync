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
}
