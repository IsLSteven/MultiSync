using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MultiSync.Core.Win32;

namespace MultiSync.Core.ScreenShare;

public enum ScreenStreamPreset
{
    Balanced720p30 = 0,
    Sharp1080p30 = 1,
    Smooth720p60 = 2,
}

public sealed record ScreenStreamRequest(
    string SenderName,
    long UnixSeconds,
    int MaxWidth,
    int TargetFps,
    int JpegQuality,
    string SignatureHex);

public sealed record ScreenFramePacket(
    int SourceWidth,
    int SourceHeight,
    byte[] JpegBytes);

/// <summary>
/// Low-latency LAN screen capture, cursor compositor, JPEG encoder, and bidirectional stream framing.
/// Uses Win32 BitBlt + GetCursorInfo/DrawIconEx for reliable desktop capture and reuses buffers to minimize GC.
/// </summary>
public static class ScreenStreamEngine
{
    public const byte MsgFrame = 1;
    public const byte MsgMouseMove = 10;
    public const byte MsgMouseButton = 11;
    public const byte MsgMouseWheel = 12;
    public const byte MsgKeyEvent = 13;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static (int MaxWidth, int TargetFps, int JpegQuality) GetPresetParams(ScreenStreamPreset preset) => preset switch
    {
        ScreenStreamPreset.Sharp1080p30 => (1920, 30, 76),
        ScreenStreamPreset.Smooth720p60 => (1280, 60, 62),
        _ => (1280, 30, 70),
    };

    /// <summary>
    /// Captures the primary screen with the live cursor, scales proportionally to maxWidth if larger,
    /// and encodes as a JPEG byte array.
    /// </summary>
    public static ScreenFramePacket CaptureScreenJpeg(int maxWidth = 1280, int jpegQuality = 70)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var srcW = Math.Max(1, bounds.Width);
        var srcH = Math.Max(1, bounds.Height);

        using var fullBmp = new Bitmap(srcW, srcH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(fullBmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(srcW, srcH), CopyPixelOperation.SourceCopy);
            DrawSystemCursor(g, bounds.Left, bounds.Top);
        }

        return EncodeBitmapToJpeg(fullBmp, maxWidth, jpegQuality);
    }

    public static ScreenFramePacket EncodeBitmapToJpeg(Bitmap fullBmp, int maxWidth = 1280, int jpegQuality = 70)
    {
        var srcW = Math.Max(1, fullBmp.Width);
        var srcH = Math.Max(1, fullBmp.Height);
        Bitmap encodeTarget = fullBmp;
        Bitmap? scaledBmp = null;
        try
        {
            if (maxWidth > 0 && srcW > maxWidth)
            {
                var scale = (double)maxWidth / srcW;
                var dstW = maxWidth;
                var dstH = Math.Max(1, (int)Math.Round(srcH * scale));
                scaledBmp = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
                using var sg = Graphics.FromImage(scaledBmp);
                sg.CompositingQuality = CompositingQuality.HighSpeed;
                sg.InterpolationMode = InterpolationMode.Bilinear;
                sg.SmoothingMode = SmoothingMode.None;
                sg.DrawImage(fullBmp, 0, 0, dstW, dstH);
                encodeTarget = scaledBmp;
            }

            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(jpegQuality, 30, 95));
            using var ms = new MemoryStream(96 * 1024);
            encodeTarget.Save(ms, JpegCodec, encParams);
            return new ScreenFramePacket(srcW, srcH, ms.ToArray());
        }
        finally
        {
            scaledBmp?.Dispose();
        }
    }

    /// <summary>
    /// Server-side streaming loop: pushes captured frames while concurrently applying any remote control
    /// input packets sent back by the viewer window.
    /// </summary>
    public static async Task ServeStreamAsync(
        Stream stream,
        int maxWidth,
        int targetFps,
        int jpegQuality,
        CancellationToken cancel)
    {
        targetFps = Math.Clamp(targetFps, 5, 60);
        maxWidth = Math.Clamp(maxWidth, 640, 3840);
        jpegQuality = Math.Clamp(jpegQuality, 30, 95);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = linkedCts.Token;

        // Background task reading optional direct-control input from the viewer window
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

                    var payload = len == 0 ? [] : new byte[len];
                    if (len > 0)
                        await stream.ReadExactlyAsync(payload, token);

                    ApplyViewerInputPacket(msgType, payload);
                }
            }
            catch
            {
                // Viewer disconnected
            }
            finally
            {
                linkedCts.Cancel();
            }
        }, token);

        var frameIntervalMs = 1000.0 / targetFps;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var startMs = sw.Elapsed.TotalMilliseconds;
                ScreenFramePacket frame;
                try
                {
                    frame = CaptureScreenJpeg(maxWidth, jpegQuality);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Transient desktop switch (e.g., UAC or Lock Screen): keep stream alive and retry shortly
                    await Task.Delay(120, token);
                    continue;
                }

                var packet = new byte[1 + 4 + 4 + 4 + frame.JpegBytes.Length];
                packet[0] = MsgFrame;
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), 8 + frame.JpegBytes.Length);
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5, 4), frame.SourceWidth);
                BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(9, 4), frame.SourceHeight);
                Buffer.BlockCopy(frame.JpegBytes, 0, packet, 13, frame.JpegBytes.Length);

                await stream.WriteAsync(packet, token);

                var elapsed = sw.Elapsed.TotalMilliseconds - startMs;
                var sleepMs = (int)(frameIntervalMs - elapsed);
                if (sleepMs > 1)
                    await Task.Delay(sleepMs, token);
            }
        }
        catch
        {
            // Viewer closed or connection ended
        }
        finally
        {
            linkedCts.Cancel();
            try { await inputTask; } catch { }
        }
    }

    public static async Task<ScreenFramePacket?> ReadNextFrameAsync(Stream stream, CancellationToken cancel)
    {
        var hdr = new byte[5];
        await stream.ReadExactlyAsync(hdr, cancel);
        var msgType = hdr[0];
        var len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(1, 4));
        if (len < 8 || len > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid screen frame length.");

        var body = new byte[len];
        await stream.ReadExactlyAsync(body, cancel);
        if (msgType != MsgFrame) return null;

        var srcW = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        var srcH = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4, 4));
        var jpeg = new byte[len - 8];
        Buffer.BlockCopy(body, 8, jpeg, 0, jpeg.Length);
        return new ScreenFramePacket(srcW, srcH, jpeg);
    }

    public static byte[] BuildMouseMovePacket(int x, int y)
    {
        var pkt = new byte[5 + 8];
        pkt[0] = MsgMouseMove;
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(1, 4), 8);
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(5, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(9, 4), y);
        return pkt;
    }

    public static byte[] BuildMouseButtonPacket(MouseBtn btn, bool isDown)
    {
        var pkt = new byte[5 + 2];
        pkt[0] = MsgMouseButton;
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(1, 4), 2);
        pkt[5] = (byte)btn;
        pkt[6] = isDown ? (byte)1 : (byte)0;
        return pkt;
    }

    public static byte[] BuildMouseWheelPacket(int delta, bool horizontal = false)
    {
        var pkt = new byte[5 + 5];
        pkt[0] = MsgMouseWheel;
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(1, 4), 5);
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(5, 4), delta);
        pkt[9] = horizontal ? (byte)1 : (byte)0;
        return pkt;
    }

    public static byte[] BuildKeyPacket(ushort vk, bool isDown, bool extended = false)
    {
        var pkt = new byte[5 + 8];
        pkt[0] = MsgKeyEvent;
        BinaryPrimitives.WriteInt32BigEndian(pkt.AsSpan(1, 4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(5, 2), vk);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(7, 2), 0);
        pkt[9] = isDown ? (byte)1 : (byte)0;
        pkt[10] = extended ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(11, 2), 0);
        return pkt;
    }

    private static void ApplyViewerInputPacket(byte msgType, byte[] data)
    {
        switch (msgType)
        {
            case MsgMouseMove when data.Length == 8:
                SlaveInputInjector.MoveCursorAbsolute(
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4)));
                break;

            case MsgMouseButton when data.Length == 2:
                SlaveInputInjector.InjectMouseButton((MouseBtn)data[0], data[1] != 0);
                break;

            case MsgMouseWheel when data.Length == 5:
                SlaveInputInjector.InjectMouseWheel(
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4)),
                    data[4] != 0);
                break;

            case MsgKeyEvent when data.Length == 8:
                SlaveInputInjector.InjectKey(
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)),
                    data[4] != 0,
                    data[5] != 0,
                    (char)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2)));
                break;
        }
    }

    private static void DrawSystemCursor(Graphics g, int originX, int originY)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & 0x00000001) == 0 || ci.hCursor == nint.Zero)
                return;

            var x = ci.ptScreenPos.x - originX;
            var y = ci.ptScreenPos.y - originY;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                x -= ii.xHotspot;
                y -= ii.yHotspot;
                if (ii.hbmMask != nint.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != nint.Zero) DeleteObject(ii.hbmColor);
            }

            var hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, nint.Zero, 0x0003); // DI_NORMAL
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        catch
        {
            // ignore transient cursor draw errors on secure desktops
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, int istepIfAniCur, nint hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
