using System.Buffers.Binary;
using System.Diagnostics;
using MultiSync.Linux.Input;
using MultiSync.Linux.Protocol;

namespace MultiSync.Linux.ScreenShare;

/// <summary>
/// Wire-compatible live screen streamer for Linux (X11 via ffmpeg x11grab up to 1080p 60 FPS,
/// Wayland via grim fallback) so Windows MultiSync's "Ver Tela (PiP)" works identically with Linux PCs.
/// </summary>
public static class LinuxScreenStreamer
{
    public const byte MsgFrame = 1;
    public const byte MsgMouseMove = 10;
    public const byte MsgMouseButton = 11;
    public const byte MsgMouseWheel = 12;
    public const byte MsgKeyEvent = 13;

    public static async Task ServeStreamAsync(
        Stream stream,
        LinuxInputInjector injector,
        int maxWidth,
        int targetFps,
        int jpegQuality,
        CancellationToken cancel)
    {
        targetFps = Math.Clamp(targetFps, 5, 60);
        maxWidth = Math.Clamp(maxWidth, 640, 3840);
        var (srcW, srcH) = SystemInfo.DetectPrimaryResolution();
        injector.UpdateScreenSize(srcW, srcH);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = linkedCts.Token;

        // Background task applying direct-control input from the Windows/Linux viewer window
        var inputTask = Task.Run(async () =>
        {
            var hdr = new byte[5];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(hdr, token);
                    var msgType = hdr[0];
                    var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(1, 4));
                    if (len < 0 || len > 1024) break;
                    var data = len == 0 ? [] : new byte[len];
                    if (len > 0) await stream.ReadExactlyAsync(data, token);
                    ApplyViewerInput(injector, msgType, data);
                }
            }
            catch { }
            finally { linkedCts.Cancel(); }
        }, token);

        try
        {
            if (!SystemInfo.IsWayland && ProcessRunner.CommandExists("ffmpeg"))
            {
                await StreamViaFfmpegX11Async(stream, srcW, srcH, maxWidth, targetFps, jpegQuality, token);
            }
            else if (SystemInfo.IsWayland && ProcessRunner.CommandExists("grim"))
            {
                await StreamViaGrimWaylandAsync(stream, srcW, srcH, targetFps, jpegQuality, token);
            }
            else if (ProcessRunner.CommandExists("ffmpeg"))
            {
                await StreamViaFfmpegX11Async(stream, srcW, srcH, maxWidth, targetFps, jpegQuality, token);
            }
        }
        catch { }
        finally
        {
            linkedCts.Cancel();
            try { await inputTask; } catch { }
        }
    }

    private static async Task StreamViaFfmpegX11Async(
        Stream netStream, int srcW, int srcH, int maxWidth, int fps, int jpegQuality, CancellationToken cancel)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display)) display = ":0.0";
        // Map JPEG quality (30..95) to ffmpeg mjpeg qscale (2..14, lower = sharper)
        var qScale = Math.Clamp((int)Math.Round(15 - (jpegQuality / 100.0 * 12)), 2, 14);
        var args = $"-loglevel error -f x11grab -framerate {fps} -video_size {srcW}x{srcH} -i {display} -vf \"scale='min({maxWidth},iw)':-2\" -f image2pipe -vcodec mjpeg -q:v {qScale} pipe:1";

        using var proc = Process.Start(new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc == null) return;

        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            var readBuf = new byte[64 * 1024];
            using var frameBuf = new MemoryStream(256 * 1024);
            var inFrame = false;
            byte prev = 0;

            while (!cancel.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(readBuf, cancel);
                if (read <= 0) break;

                for (var i = 0; i < read; i++)
                {
                    var b = readBuf[i];
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8)
                        {
                            inFrame = true;
                            frameBuf.SetLength(0);
                            frameBuf.WriteByte(0xFF);
                            frameBuf.WriteByte(0xD8);
                        }
                    }
                    else
                    {
                        frameBuf.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9)
                        {
                            inFrame = false;
                            await WriteScreenFrameAsync(netStream, srcW, srcH, frameBuf.GetBuffer(), (int)frameBuf.Length, cancel);
                        }
                    }
                    prev = b;
                }
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static async Task StreamViaGrimWaylandAsync(
        Stream netStream, int srcW, int srcH, int fps, int jpegQuality, CancellationToken cancel)
    {
        var intervalMs = Math.Max(33, 1000 / Math.Min(fps, 30));
        while (!cancel.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            using var proc = Process.Start(new ProcessStartInfo("grim", $"-t jpeg -q {jpegQuality} -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc != null)
            {
                using var ms = new MemoryStream(128 * 1024);
                await proc.StandardOutput.BaseStream.CopyToAsync(ms, cancel);
                await proc.WaitForExitAsync(cancel);
                if (ms.Length > 100)
                    await WriteScreenFrameAsync(netStream, srcW, srcH, ms.GetBuffer(), (int)ms.Length, cancel);
            }
            var wait = intervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 1) await Task.Delay(wait, cancel);
        }
    }

    private static async Task WriteScreenFrameAsync(Stream stream, int srcW, int srcH, byte[] jpegBuf, int jpegLen, CancellationToken cancel)
    {
        var packet = new byte[13 + jpegLen];
        packet[0] = MsgFrame;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), 8 + jpegLen);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5, 4), srcW);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(9, 4), srcH);
        Buffer.BlockCopy(jpegBuf, 0, packet, 13, jpegLen);
        await stream.WriteAsync(packet, cancel);
    }

    private static void ApplyViewerInput(LinuxInputInjector injector, byte msgType, byte[] data)
    {
        switch (msgType)
        {
            case MsgMouseMove when data.Length == 8:
                injector.MoveCursorAbsolute(
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)));
                break;
            case MsgMouseButton when data.Length == 2:
                injector.InjectMouseButton((MouseBtn)data[0], data[1] != 0);
                break;
            case MsgMouseWheel when data.Length == 5:
                injector.InjectMouseWheel(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)), data[4] != 0);
                break;
            case MsgKeyEvent when data.Length == 8:
                injector.InjectKey(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)), data[4] != 0);
                break;
        }
    }
}
