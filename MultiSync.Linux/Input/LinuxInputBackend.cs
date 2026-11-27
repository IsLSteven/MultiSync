using System.Runtime.InteropServices;
using System.Text;
using MultiSync.Linux.Protocol;

namespace MultiSync.Linux.Input;

/// <summary>
/// Dual-backend Linux input injector for Slave mode and Remote Screen direct control:
/// 1) Primary: Native /dev/uinput virtual absolute mouse + keyboard (works on BOTH X11 and Wayland).
/// 2) Fallback: Native X11 + XTest (libX11.so.6 / libXtst.so.6), works out-of-the-box on Debian, Mint, Ubuntu X11, Arch X11 without root.
/// </summary>
public sealed class LinuxInputInjector : IDisposable
{
    private readonly Lock _sync = new();
    private int _uinputFd = -1;
    private nint _xDisplay = nint.Zero;
    private int _screenWidth = 1920;
    private int _screenHeight = 1080;

    public string ActiveBackend { get; private set; } = "Nenhum";

    public LinuxInputInjector()
    {
        var (w, h) = SystemInfo.DetectPrimaryResolution();
        _screenWidth = w;
        _screenHeight = h;
        InitializeBackend();
    }

    public void UpdateScreenSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
    }

    private void InitializeBackend()
    {
        if (!OperatingSystem.IsLinux())
        {
            ActiveBackend = "Simulado (não-Linux)";
            return;
        }

        if (TryInitUinput())
        {
            ActiveBackend = "/dev/uinput (X11 + Wayland)";
            return;
        }

        if (TryInitX11())
        {
            ActiveBackend = "X11 / XTest";
            return;
        }

        ActiveBackend = "Sem permissão (/dev/uinput e X11 indisponíveis — rode install-linux.sh)";
    }

    public void MoveCursorAbsolute(int x, int y)
    {
        lock (_sync)
        {
            if (_uinputFd >= 0)
            {
                var absX = (int)Math.Clamp(Math.Round((double)x / Math.Max(1, _screenWidth - 1) * 65535.0), 0, 65535);
                var absY = (int)Math.Clamp(Math.Round((double)y / Math.Max(1, _screenHeight - 1) * 65535.0), 0, 65535);
                WriteUinputEvent(EV_ABS, ABS_X, absX);
                WriteUinputEvent(EV_ABS, ABS_Y, absY);
                WriteUinputEvent(EV_SYN, SYN_REPORT, 0);
                return;
            }

            if (_xDisplay != nint.Zero)
            {
                var root = XDefaultRootWindow(_xDisplay);
                XWarpPointer(_xDisplay, nint.Zero, root, 0, 0, 0, 0, x, y);
                XFlush(_xDisplay);
            }
        }
    }

    public void InjectMouseButton(MouseBtn btn, bool isDown)
    {
        lock (_sync)
        {
            if (_uinputFd >= 0)
            {
                ushort code = btn switch
                {
                    MouseBtn.Left => BTN_LEFT,
                    MouseBtn.Right => BTN_RIGHT,
                    MouseBtn.Middle => BTN_MIDDLE,
                    MouseBtn.XButton1 => BTN_SIDE,
                    MouseBtn.XButton2 => BTN_EXTRA,
                    _ => BTN_LEFT,
                };
                WriteUinputEvent(EV_KEY, code, isDown ? 1 : 0);
                WriteUinputEvent(EV_SYN, SYN_REPORT, 0);
                return;
            }

            if (_xDisplay != nint.Zero)
            {
                uint xBtn = btn switch
                {
                    MouseBtn.Left => 1,
                    MouseBtn.Middle => 2,
                    MouseBtn.Right => 3,
                    MouseBtn.XButton1 => 8,
                    MouseBtn.XButton2 => 9,
                    _ => 1,
                };
                XTestFakeButtonEvent(_xDisplay, xBtn, isDown ? 1 : 0, 0);
                XFlush(_xDisplay);
            }
        }
    }

    public void InjectMouseWheel(int delta, bool horizontal)
    {
        var steps = Math.Clamp(delta / 120, -10, 10);
        if (steps == 0) steps = delta > 0 ? 1 : -1;

        lock (_sync)
        {
            if (_uinputFd >= 0)
            {
                WriteUinputEvent(EV_REL, horizontal ? REL_HWHEEL : REL_WHEEL, steps);
                WriteUinputEvent(EV_SYN, SYN_REPORT, 0);
                return;
            }

            if (_xDisplay != nint.Zero)
            {
                uint xBtn = horizontal
                    ? (steps > 0 ? 7u : 6u)
                    : (steps > 0 ? 4u : 5u);
                var count = Math.Abs(steps);
                for (var i = 0; i < count; i++)
                {
                    XTestFakeButtonEvent(_xDisplay, xBtn, 1, 0);
                    XTestFakeButtonEvent(_xDisplay, xBtn, 0, 0);
                }
                XFlush(_xDisplay);
            }
        }
    }

    public void InjectKey(ushort vk, bool isDown)
    {
        lock (_sync)
        {
            if (_uinputFd >= 0)
            {
                var ev = LinuxKeyMap.ToEvdevCode(vk);
                if (ev == 0) return;
                WriteUinputEvent(EV_KEY, ev, isDown ? 1 : 0);
                WriteUinputEvent(EV_SYN, SYN_REPORT, 0);
                return;
            }

            if (_xDisplay != nint.Zero)
            {
                var kc = LinuxKeyMap.ToX11KeyCode(vk);
                if (kc == 0) return;
                XTestFakeKeyEvent(_xDisplay, kc, isDown ? 1 : 0, 0);
                XFlush(_xDisplay);
            }
        }
    }

    private bool TryInitUinput()
    {
        try
        {
            var fd = libc_open("/dev/uinput", O_WRONLY | O_NONBLOCK);
            if (fd < 0) fd = libc_open("/dev/input/uinput", O_WRONLY | O_NONBLOCK);
            if (fd < 0) return false;

            libc_ioctl_int(fd, UI_SET_EVBIT, EV_KEY);
            libc_ioctl_int(fd, UI_SET_EVBIT, EV_ABS);
            libc_ioctl_int(fd, UI_SET_EVBIT, EV_REL);
            libc_ioctl_int(fd, UI_SET_EVBIT, EV_SYN);

            // Mouse buttons + scroll wheels
            foreach (var b in new[] { BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_SIDE, BTN_EXTRA })
                libc_ioctl_int(fd, UI_SET_KEYBIT, b);
            libc_ioctl_int(fd, UI_SET_RELBIT, REL_WHEEL);
            libc_ioctl_int(fd, UI_SET_RELBIT, REL_HWHEEL);

            // Absolute pointer axes 0..65535
            libc_ioctl_int(fd, UI_SET_ABSBIT, ABS_X);
            libc_ioctl_int(fd, UI_SET_ABSBIT, ABS_Y);

            // All mapped keyboard keys
            foreach (var k in LinuxKeyMap.AllEvdevKeyCodes)
                libc_ioctl_int(fd, UI_SET_KEYBIT, k);

            // Write legacy uinput_user_dev (works on all Linux kernels Debian/Mint/Ubuntu/Arch)
            var udev = new UinputUserDev();
            udev.SetName("MultiSync Virtual Input");
            udev.IdBusType = 0x03; // BUS_USB
            udev.IdVendor = 0x1D6B;
            udev.IdProduct = 0x554D;
            udev.IdVersion = 1;
            udev.SetAbsRange(ABS_X, 0, 65535);
            udev.SetAbsRange(ABS_Y, 0, 65535);

            var raw = udev.ToBytes();
            if (libc_write(fd, raw, (nuint)raw.Length) < 0 || libc_ioctl_void(fd, UI_DEV_CREATE) < 0)
            {
                libc_close(fd);
                return false;
            }

            _uinputFd = fd;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryInitX11()
    {
        try
        {
            var disp = XOpenDisplay(null);
            if (disp == nint.Zero) return false;
            _xDisplay = disp;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private unsafe void WriteUinputEvent(ushort type, ushort code, int value)
    {
        if (_uinputFd < 0) return;
        var ev = new InputEvent { Type = type, Code = code, Value = value };
        libc_write_ptr(_uinputFd, &ev, (nuint)Marshal.SizeOf<InputEvent>());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_uinputFd >= 0)
            {
                try { libc_ioctl_void(_uinputFd, UI_DEV_DESTROY); } catch { }
                try { libc_close(_uinputFd); } catch { }
                _uinputFd = -1;
            }
            if (_xDisplay != nint.Zero)
            {
                try { XCloseDisplay(_xDisplay); } catch { }
                _xDisplay = nint.Zero;
            }
        }
    }

    // Linux input/uinput constants (x86_64/arm64)
    private const int O_WRONLY = 0x0001;
    private const int O_NONBLOCK = 0x0800;
    private const ushort EV_SYN = 0x00;
    private const ushort EV_KEY = 0x01;
    private const ushort EV_REL = 0x02;
    private const ushort EV_ABS = 0x03;
    private const ushort SYN_REPORT = 0x00;
    private const ushort ABS_X = 0x00;
    private const ushort ABS_Y = 0x01;
    private const ushort REL_HWHEEL = 0x06;
    private const ushort REL_WHEEL = 0x08;
    private const ushort BTN_LEFT = 0x110;
    private const ushort BTN_RIGHT = 0x111;
    private const ushort BTN_MIDDLE = 0x112;
    private const ushort BTN_SIDE = 0x113;
    private const ushort BTN_EXTRA = 0x114;

    private const ulong UI_DEV_CREATE = 0x5501;
    private const ulong UI_DEV_DESTROY = 0x5502;
    private const ulong UI_SET_EVBIT = 0x40045564;
    private const ulong UI_SET_KEYBIT = 0x40045565;
    private const ulong UI_SET_RELBIT = 0x40045566;
    private const ulong UI_SET_ABSBIT = 0x40045567;

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public nint TvSec;
        public nint TvUsec;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    private sealed class UinputUserDev
    {
        private readonly byte[] _name = new byte[80];
        public ushort IdBusType, IdVendor, IdProduct, IdVersion;
        private readonly int[] _absMax = new int[64];
        private readonly int[] _absMin = new int[64];
        private readonly int[] _absFuzz = new int[64];
        private readonly int[] _absFlat = new int[64];

        public void SetName(string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(bytes, _name, Math.Min(bytes.Length, 79));
        }

        public void SetAbsRange(int axis, int min, int max)
        {
            _absMin[axis] = min;
            _absMax[axis] = max;
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream(1116);
            using var bw = new BinaryWriter(ms);
            bw.Write(_name);         // 80 bytes
            bw.Write(IdBusType);     // 2
            bw.Write(IdVendor);      // 2
            bw.Write(IdProduct);     // 2
            bw.Write(IdVersion);     // 2
            bw.Write(0);             // ff_effects_max (4)
            foreach (var v in _absMax) bw.Write(v);
            foreach (var v in _absMin) bw.Write(v);
            foreach (var v in _absFuzz) bw.Write(v);
            foreach (var v in _absFlat) bw.Write(v);
            return ms.ToArray();
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int libc_open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int libc_close(int fd);

    [DllImport("libc", EntryPoint = "write")]
    private static extern nint libc_write(int fd, byte[] buf, nuint count);

    [DllImport("libc", EntryPoint = "write")]
    private static extern unsafe nint libc_write_ptr(int fd, InputEvent* ev, nuint count);

    [DllImport("libc", EntryPoint = "ioctl")]
    private static extern int libc_ioctl_int(int fd, ulong request, int arg);

    [DllImport("libc", EntryPoint = "ioctl")]
    private static extern int libc_ioctl_void(int fd, ulong request);

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XWarpPointer(nint display, nint srcW, nint destW, int srcX, int srcY, uint srcW2, uint srcH2, int destX, int destY);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeButtonEvent(nint display, uint button, int isPress, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeKeyEvent(nint display, uint keycode, int isPress, ulong delay);
}

/// <summary>
/// Linux Master input capture over X11 (Debian, Mint, Ubuntu X11, Arch X11):
/// detects screen edge crossings and grabs pointer/keyboard while controlling a remote Windows or Linux Slave.
/// </summary>
public sealed class LinuxX11MasterCapture : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _capturingRemote;
    private int _parkX;
    private int _parkY;
    private nint _display;
    private nint _root;

    public event Action<int, int>? LocalCursorMoved;
    public event Action<int, int>? RemoteMouseDelta;
    public event Action<MouseBtn, bool>? RemoteMouseButton;
    public event Action<int, bool>? RemoteMouseWheel;
    public event Action<ushort, bool>? RemoteKey;

    public void Start()
    {
        if (!OperatingSystem.IsLinux() || _cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoop(token), token);
    }

    public void EnterRemoteCapture(int parkX, int parkY)
    {
        _parkX = parkX;
        _parkY = parkY;
        _capturingRemote = true;
    }

    public void ExitRemoteCapture(int returnX, int returnY)
    {
        _capturingRemote = false;
        if (_display != nint.Zero)
        {
            try
            {
                XUngrabPointer(_display, 0);
                XUngrabKeyboard(_display, 0);
                XWarpPointer(_display, nint.Zero, _root, 0, 0, 0, 0, returnX, returnY);
                XFlush(_display);
            }
            catch { }
        }
    }

    private void RunLoop(CancellationToken cancel)
    {
        try
        {
            _display = XOpenDisplay(null);
            if (_display == nint.Zero) return;
            _root = XDefaultRootWindow(_display);
            var (w, h) = SystemInfo.DetectPrimaryResolution();
            var centerX = w / 2;
            var centerY = h / 2;
            var grabbed = false;
            var evBuf = new byte[192]; // XEvent is 192 bytes on x86_64

            while (!cancel.IsCancellationRequested)
            {
                if (!_capturingRemote)
                {
                    if (grabbed)
                    {
                        XUngrabPointer(_display, 0);
                        XUngrabKeyboard(_display, 0);
                        XFlush(_display);
                        grabbed = false;
                    }

                    if (XQueryPointer(_display, _root, out _, out _, out var rx, out var ry, out _, out _, out _) != 0)
                        LocalCursorMoved?.Invoke(rx, ry);

                    Thread.Sleep(8);
                    continue;
                }

                if (!grabbed)
                {
                    const uint mask = (1u << 2) | (1u << 3) | (1u << 6); // ButtonPress | ButtonRelease | PointerMotion
                    XGrabPointer(_display, _root, 0, mask, 1, 1, nint.Zero, nint.Zero, 0);
                    XGrabKeyboard(_display, _root, 0, 1, 1, 0);
                    XWarpPointer(_display, nint.Zero, _root, 0, 0, 0, 0, centerX, centerY);
                    XFlush(_display);
                    grabbed = true;
                }

                while (XPending(_display) > 0 && _capturingRemote)
                {
                    XNextEvent(_display, evBuf);
                    var evType = BitConverter.ToInt32(evBuf, 0);
                    if (evType == 6) // MotionNotify
                    {
                        var mx = BitConverter.ToInt32(evBuf, 64);
                        var my = BitConverter.ToInt32(evBuf, 68);
                        var dx = mx - centerX;
                        var dy = my - centerY;
                        if (dx != 0 || dy != 0)
                        {
                            XWarpPointer(_display, nint.Zero, _root, 0, 0, 0, 0, centerX, centerY);
                            XFlush(_display);
                            RemoteMouseDelta?.Invoke(dx, dy);
                        }
                    }
                    else if (evType is 4 or 5) // ButtonPress / ButtonRelease
                    {
                        var isDown = evType == 4;
                        var btn = BitConverter.ToUInt32(evBuf, 84);
                        if (btn == 4 && isDown) RemoteMouseWheel?.Invoke(120, false);
                        else if (btn == 5 && isDown) RemoteMouseWheel?.Invoke(-120, false);
                        else if (btn == 6 && isDown) RemoteMouseWheel?.Invoke(-120, true);
                        else if (btn == 7 && isDown) RemoteMouseWheel?.Invoke(120, true);
                        else
                        {
                            var mapped = btn switch
                            {
                                1u => MouseBtn.Left,
                                2u => MouseBtn.Middle,
                                3u => MouseBtn.Right,
                                8u => MouseBtn.XButton1,
                                9u => MouseBtn.XButton2,
                                _ => (MouseBtn?)null,
                            };
                            if (mapped != null) RemoteMouseButton?.Invoke(mapped.Value, isDown);
                        }
                    }
                    else if (evType is 2 or 3) // KeyPress / KeyRelease
                    {
                        var isDown = evType == 2;
                        var kc = BitConverter.ToUInt32(evBuf, 84);
                        var vk = LinuxKeyMap.FromX11KeyCode(kc);
                        if (vk != 0) RemoteKey?.Invoke(vk, isDown);
                    }
                }

                Thread.Sleep(2);
            }
        }
        catch { }
        finally
        {
            if (_display != nint.Zero)
            {
                try
                {
                    XUngrabPointer(_display, 0);
                    XUngrabKeyboard(_display, 0);
                    XCloseDisplay(_display);
                }
                catch { }
                _display = nint.Zero;
            }
        }
    }

    public void Dispose()
    {
        _capturingRemote = false;
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern int XPending(nint display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(nint display, byte[] eventReturn);
    [DllImport("libX11.so.6")] private static extern int XWarpPointer(nint d, nint s, nint dst, int sx, int sy, uint sw, uint sh, int dx, int dy);
    [DllImport("libX11.so.6")] private static extern int XGrabPointer(nint d, nint w, int oe, uint em, int pm, int km, nint ct, nint cur, ulong t);
    [DllImport("libX11.so.6")] private static extern int XUngrabPointer(nint d, ulong t);
    [DllImport("libX11.so.6")] private static extern int XGrabKeyboard(nint d, nint w, int oe, int pm, int km, ulong t);
    [DllImport("libX11.so.6")] private static extern int XUngrabKeyboard(nint d, ulong t);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(nint d, nint w, out nint rr, out nint cr, out int rx, out int ry, out int wx, out int wy, out uint mask);
}
