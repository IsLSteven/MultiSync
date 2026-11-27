using MultiSync.Core.Audio;
using MultiSync.Core.ClipboardSync;
using MultiSync.Core.Discovery;
using MultiSync.Core.Models;
using MultiSync.Core.ScreenShare;
using MultiSync.Core.Topology;
using NAudio.Wave;
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

    [Test]
    public void SharedAudioEngine_ConvertTo48kStereo16_Resamples44kFloatTo48kPcm16()
    {
        // Generate 20 ms of 44.1 kHz 32-bit IEEE float stereo sine wave (882 stereo frames)
        const int srcRate = 44100;
        const int frames = 882;
        var srcFormat = WaveFormat.CreateIeeeFloatWaveFormat(srcRate, 2);
        var floatBytes = new byte[frames * 2 * sizeof(float)];
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(0.5 * Math.Sin(2.0 * Math.PI * 440.0 * i / srcRate));
            BitConverter.TryWriteBytes(floatBytes.AsSpan(i * 8, 4), sample);
            BitConverter.TryWriteBytes(floatBytes.AsSpan(i * 8 + 4, 4), sample);
        }

        var pcm16 = SharedAudioEngine.ConvertTo48kStereo16(floatBytes, floatBytes.Length, srcFormat);

        // 20 ms at 48 kHz 16-bit stereo = ~3840 bytes (resampler may vary slightly by a few frames)
        Assert.That(pcm16.Length, Is.InRange(3500, 4100));
        Assert.That(pcm16.Length % 4, Is.EqualTo(0));
        Assert.That(pcm16.Any(b => b != 0), Is.True);
    }

    [Test]
    public async Task DedicatedLanFileTransfer_StreamsFilesByteForByte()
    {
        var tempSrc = Path.Combine(Path.GetTempPath(), "MultiSyncFastSrc_" + Guid.NewGuid().ToString("N")[..8]);
        var tempCfg = Path.Combine(Path.GetTempPath(), "MultiSyncFastCfg_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Directory.CreateDirectory(tempSrc);
        var srcFile = Path.Combine(tempSrc, "payload.bin");
        var expected = new byte[600 * 1024]; // 600 KB across multiple 256 KB chunks
        Random.Shared.NextBytes(expected);
        await File.WriteAllBytesAsync(srcFile, expected);

        var receivedTcs = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ConfigStore(tempCfg);
        var cfg = store.GetSnapshot();
        cfg.ControlPort = 51529;
        cfg.DiscoveryPort = 51528;
        cfg.SessionKey = "test-session-key-123";
        store.Save(cfg);

        using var lan = new LanDiscoveryAndControlService(
            store,
            onRemoteConfigApplied: _ => { },
            onFilesReceived: paths => receivedTcs.TrySetResult(paths));
        lan.Start();
        await Task.Delay(150);

        try
        {
            var built = ClipboardSyncManager.BuildFileManifest([srcFile])!;
            await LanDiscoveryAndControlService.SendFilesToPeerAsync(
                "127.0.0.1",
                cfg.ControlPort,
                "PC-Sender",
                cfg.SessionKey,
                built.Value.Manifest,
                built.Value.FullPathByRelative);

            var topPaths = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(topPaths.Count, Is.EqualTo(1));
            var receivedBytes = await File.ReadAllBytesAsync(topPaths[0]);
            Assert.That(receivedBytes, Is.EqualTo(expected));
        }
        finally
        {
            try { Directory.Delete(tempSrc, recursive: true); } catch { }
            try { File.Delete(tempCfg); } catch { }
        }
    }

    [Test]
    public void ScreenStreamEngine_EncodesAndScalesValidJpegFrame()
    {
        using var bmp = new System.Drawing.Bitmap(1920, 1080, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.Clear(System.Drawing.Color.DarkSlateBlue);

        var frame = ScreenStreamEngine.EncodeBitmapToJpeg(bmp, maxWidth: 640, jpegQuality: 65);
        Assert.That(frame.SourceWidth, Is.EqualTo(1920));
        Assert.That(frame.SourceHeight, Is.EqualTo(1080));
        Assert.That(frame.JpegBytes.Length, Is.GreaterThan(100));
        // JPEG magic bytes FF D8
        Assert.That(frame.JpegBytes[0], Is.EqualTo(0xFF));
        Assert.That(frame.JpegBytes[1], Is.EqualTo(0xD8));
    }
}
