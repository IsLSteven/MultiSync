using System.Runtime.InteropServices;

namespace MultiSync.Core.Win32;

public enum MouseBtn : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    XButton1 = 4,
    XButton2 = 5,
}

public static class Win32Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_QUIT = 0x0012;

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x01000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLMHF_INJECTED = 0x01;

    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_L = 0x4C;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    public delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public POINT Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MOUSEINPUT Mi;
        [FieldOffset(8)] public KEYBDINPUT Ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern unsafe int GetClassNameW(nint hWnd, char* lpClassName, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, uint attr, out RECT rect, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(
        uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszBuff,
        int cchBuff, uint wFlags, nint dwhkl);

    /// <summary>
    /// Detects whether a fullscreen or borderless-fullscreen application (e.g. League of Legends)
    /// is currently focused, ignoring Windows Desktop, Taskbar, and standard maximized windows.
    /// </summary>
    public static unsafe bool IsFullscreenWindowFocused()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            return false;
        if (IsIconic(hwnd) || !IsWindowVisible(hwnd))
            return false;

        char* clsBuf = stackalloc char[64];
        var len = GetClassNameW(hwnd, clsBuf, 64);
        if (len > 0)
        {
            var cls = new ReadOnlySpan<char>(clsBuf, len);
            if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(hwnd, out rect))
                return false;
        }

        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == nint.Zero) return false;

        var mi = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi))
            return false;

        return rect.Left <= mi.Monitor.Left
            && rect.Top <= mi.Monitor.Top
            && rect.Right >= mi.Monitor.Right
            && rect.Bottom >= mi.Monitor.Bottom;
    }
}

public sealed class MasterInputCapture : IDisposable
{
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private nint _mouseHook;
    private nint _kbdHook;
    private Win32Native.LowLevelProc? _mouseDelegate;
    private Win32Native.LowLevelProc? _kbdDelegate;
    private Form? _cursorShieldForm;
    private readonly ManualResetEventSlim _ready = new();

    private volatile bool _isCapturingRemote;
    private volatile bool _awaitingParkArrival;
    private long _ignoreParkEchoUntilTick;
    private int _parkX = 960;
    private int _parkY = 540;

    public event Action<int, int>? LocalCursorMoved;
    public event Action<int, int>? RemoteMouseDelta;
    public event Action<MouseBtn, bool>? RemoteMouseButton;
    public event Action<int, bool>? RemoteMouseWheel;
    public event Action<ushort, ushort, bool, bool, char>? RemoteKey;
    public event Action? ToggleScreenLockHotkey;
    public event Action<int, int>? SwitchScreenDirectionHotkey;

    public bool IsCapturingRemote => _isCapturingRemote;

    public void Start()
    {
        if (_pumpThread != null) return;
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "MultiSync.InputPump",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    public void EnterRemoteCapture(int parkX, int parkY)
    {
        _parkX = parkX;
        _parkY = parkY;
        _awaitingParkArrival = true;
        _isCapturingRemote = true;
        Win32Native.SetCursorPos(parkX, parkY);
        ShowShield(true);
    }

    public void ExitRemoteCapture(int landingX, int landingY)
    {
        _isCapturingRemote = false;
        _awaitingParkArrival = false;
        _ignoreParkEchoUntilTick = Environment.TickCount64 + 80;
        ShowShield(false);
        Win32Native.SetCursorPos(landingX, landingY);
    }

    private void ShowShield(bool visible)
    {
        var form = _cursorShieldForm;
        if (form == null || form.IsDisposed) return;
        try
        {
            form.BeginInvoke(() =>
            {
                if (visible)
                {
                    form.Location = new System.Drawing.Point(_parkX - 60, _parkY - 60);
                    if (!form.Visible) form.Show();
                }
                else if (form.Visible)
                {
                    form.Hide();
                }
            });
        }
        catch
        {
            // ignore shutdown race
        }
    }

    private void PumpLoop()
    {
        _pumpThreadId = Win32Native.GetCurrentThreadId();
        _mouseDelegate = MouseCallback;
        _kbdDelegate = KeyboardCallback;

        // Invisible window under park point that hides cursor via Cursor.Hide() only over itself — never touches SetSystemCursor!
        _cursorShieldForm = new CursorBlankWindow();
        _ = _cursorShieldForm.Handle;

        _mouseHook = Win32Native.SetWindowsHookExW(Win32Native.WH_MOUSE_LL, _mouseDelegate, nint.Zero, 0);
        _kbdHook = Win32Native.SetWindowsHookExW(Win32Native.WH_KEYBOARD_LL, _kbdDelegate, nint.Zero, 0);
        _ready.Set();

        while (Win32Native.GetMessageW(out var msg, nint.Zero, 0, 0) > 0)
        {
            Win32Native.TranslateMessage(ref msg);
            Win32Native.DispatchMessageW(ref msg);
        }

        if (_mouseHook != nint.Zero) Win32Native.UnhookWindowsHookEx(_mouseHook);
        if (_kbdHook != nint.Zero) Win32Native.UnhookWindowsHookEx(_kbdHook);
        _cursorShieldForm?.Dispose();
    }

    private unsafe nint MouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
            return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        var info = *(Win32Native.MSLLHOOKSTRUCT*)lParam;
        var msg = (int)wParam;

        if (msg == Win32Native.WM_MOUSEMOVE)
        {
            if (_isCapturingRemote)
            {
                if (info.Pt.X == _parkX && info.Pt.Y == _parkY)
                {
                    _awaitingParkArrival = false;
                    return 1;
                }

                // Discard pre-warp mouse samples queued near the screen edge before SetCursorPos(_parkX, _parkY)
                // landed; otherwise (edgeX - parkX) adds ~960px and teleports the remote cursor to screen center!
                if (_awaitingParkArrival)
                {
                    Win32Native.SetCursorPos(_parkX, _parkY);
                    return 1;
                }

                var dx = info.Pt.X - _parkX;
                var dy = info.Pt.Y - _parkY;

                // Guard against any late OS warp-echo spike (> 350px in a single USB sample)
                if (Math.Abs(dx) > 350 || Math.Abs(dy) > 350)
                {
                    Win32Native.SetCursorPos(_parkX, _parkY);
                    return 1;
                }

                if (dx != 0 || dy != 0)
                {
                    RemoteMouseDelta?.Invoke(dx, dy);
                    // Only re-center if we are STILL on the remote screen!
                    // If RemoteMouseDelta returned to the local Master screen, ExitRemoteCapture already
                    // placed the cursor at the entry edge — re-centering here would teleport it to center!
                    if (_isCapturingRemote)
                        Win32Native.SetCursorPos(_parkX, _parkY);
                }
                return 1;
            }

            // Swallow any stale queued mouse move still stamped near the old park point right after returning
            if (Environment.TickCount64 <= _ignoreParkEchoUntilTick &&
                Math.Abs(info.Pt.X - _parkX) <= 4 && Math.Abs(info.Pt.Y - _parkY) <= 4)
            {
                return 1;
            }

            LocalCursorMoved?.Invoke(info.Pt.X, info.Pt.Y);
            return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
        }

        if (!_isCapturingRemote)
            return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        switch (msg)
        {
            case Win32Native.WM_LBUTTONDOWN: RemoteMouseButton?.Invoke(MouseBtn.Left, true); return 1;
            case Win32Native.WM_LBUTTONUP: RemoteMouseButton?.Invoke(MouseBtn.Left, false); return 1;
            case Win32Native.WM_RBUTTONDOWN: RemoteMouseButton?.Invoke(MouseBtn.Right, true); return 1;
            case Win32Native.WM_RBUTTONUP: RemoteMouseButton?.Invoke(MouseBtn.Right, false); return 1;
            case Win32Native.WM_MBUTTONDOWN: RemoteMouseButton?.Invoke(MouseBtn.Middle, true); return 1;
            case Win32Native.WM_MBUTTONUP: RemoteMouseButton?.Invoke(MouseBtn.Middle, false); return 1;
            case Win32Native.WM_XBUTTONDOWN:
                RemoteMouseButton?.Invoke(((info.MouseData >> 16) & 0xFFFF) == 1 ? MouseBtn.XButton1 : MouseBtn.XButton2, true);
                return 1;
            case Win32Native.WM_XBUTTONUP:
                RemoteMouseButton?.Invoke(((info.MouseData >> 16) & 0xFFFF) == 1 ? MouseBtn.XButton1 : MouseBtn.XButton2, false);
                return 1;
            case Win32Native.WM_MOUSEWHEEL:
                RemoteMouseWheel?.Invoke((short)(info.MouseData >> 16), false);
                return 1;
            case Win32Native.WM_MOUSEHWHEEL:
                RemoteMouseWheel?.Invoke((short)(info.MouseData >> 16), true);
                return 1;
        }

        return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private unsafe nint KeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
            return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        var info = *(Win32Native.KBDLLHOOKSTRUCT*)lParam;
        var msg = (int)wParam;
        var isDown = msg is Win32Native.WM_KEYDOWN or Win32Native.WM_SYSKEYDOWN;
        var isUp = msg is Win32Native.WM_KEYUP or Win32Native.WM_SYSKEYUP;

        // Global Hotkeys: Ctrl + Alt + Win + (L or Arrows)
        if (isDown && IsCtrlAltWinHeld())
        {
            if (info.VkCode == Win32Native.VK_L)
            {
                ToggleScreenLockHotkey?.Invoke();
                return 1;
            }
            if (info.VkCode == Win32Native.VK_RIGHT) { SwitchScreenDirectionHotkey?.Invoke(1, 0); return 1; }
            if (info.VkCode == Win32Native.VK_LEFT) { SwitchScreenDirectionHotkey?.Invoke(-1, 0); return 1; }
            if (info.VkCode == Win32Native.VK_UP) { SwitchScreenDirectionHotkey?.Invoke(0, -1); return 1; }
            if (info.VkCode == Win32Native.VK_DOWN) { SwitchScreenDirectionHotkey?.Invoke(0, 1); return 1; }
        }

        if (!_isCapturingRemote)
            return Win32Native.CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        if (isDown || isUp)
        {
            var extended = (info.Flags & Win32Native.LLKHF_EXTENDED) != 0;
            var ch = isDown ? ResolveUnicodeChar(info.VkCode, info.ScanCode) : '\0';
            RemoteKey?.Invoke((ushort)info.VkCode, (ushort)info.ScanCode, isDown, extended, ch);
        }
        return 1;
    }

    private static bool IsCtrlAltWinHeld() =>
        (Win32Native.GetAsyncKeyState(Win32Native.VK_CONTROL) & 0x8000) != 0 &&
        (Win32Native.GetAsyncKeyState(Win32Native.VK_MENU) & 0x8000) != 0 &&
        ((Win32Native.GetAsyncKeyState(Win32Native.VK_LWIN) & 0x8000) != 0 ||
         (Win32Native.GetAsyncKeyState(Win32Native.VK_RWIN) & 0x8000) != 0);

    private static char ResolveUnicodeChar(uint vk, uint scan)
    {
        if ((Win32Native.GetAsyncKeyState(Win32Native.VK_CONTROL) & 0x8000) != 0 ||
            (Win32Native.GetAsyncKeyState(Win32Native.VK_LWIN) & 0x8000) != 0 ||
            (Win32Native.GetAsyncKeyState(Win32Native.VK_RWIN) & 0x8000) != 0)
            return '\0';

        var state = new byte[256];
        if (!Win32Native.GetKeyboardState(state)) return '\0';
        var fg = Win32Native.GetForegroundWindow();
        var tid = fg != nint.Zero ? Win32Native.GetWindowThreadProcessId(fg, out _) : 0;
        var layout = Win32Native.GetKeyboardLayout(tid);

        var sb = new System.Text.StringBuilder(8);
        // Flag 4 (Windows 10 1607+): do not alter dead-key state in kernel buffer
        var rc = Win32Native.ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 4, layout);
        return rc == 1 && sb.Length == 1 && !char.IsControl(sb[0]) ? sb[0] : '\0';
    }

    public void Dispose()
    {
        _isCapturingRemote = false;
        if (_pumpThreadId != 0)
            Win32Native.PostThreadMessageW(_pumpThreadId, Win32Native.WM_QUIT, nint.Zero, nint.Zero);
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _pumpThread = null;
        _ready.Dispose();
    }

    private sealed class CursorBlankWindow : Form
    {
        [DllImport("user32.dll")]
        private static extern nint SetCursor(nint hCursor);

        public CursorBlankWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new System.Drawing.Size(120, 120);
            Opacity = 0.01;
            BackColor = System.Drawing.Color.Black;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0020) // WM_SETCURSOR
            {
                SetCursor(nint.Zero);
                m.Result = 1;
                return;
            }
            base.WndProc(ref m);
        }
    }
}

public static class SlaveInputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<Win32Native.INPUT>();

    public static void MoveCursorAbsolute(int x, int y)
    {
        var vLeft = Win32Native.GetSystemMetrics(Win32Native.SM_XVIRTUALSCREEN);
        var vTop = Win32Native.GetSystemMetrics(Win32Native.SM_YVIRTUALSCREEN);
        var vWidth = Math.Max(2, Win32Native.GetSystemMetrics(Win32Native.SM_CXVIRTUALSCREEN));
        var vHeight = Math.Max(2, Win32Native.GetSystemMetrics(Win32Native.SM_CYVIRTUALSCREEN));

        var normX = ((x - vLeft) * 65535 + (vWidth - 1) / 2) / (vWidth - 1);
        var normY = ((y - vTop) * 65535 + (vHeight - 1) / 2) / (vHeight - 1);

        Span<byte> cmd = stackalloc byte[12];
        cmd[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(cmd.Slice(1, 4), normX);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(cmd.Slice(5, 4), normY);
        if (SystemInputBridgeClient.TrySendCommand(cmd))
            return;

        var input = new Win32Native.INPUT
        {
            Type = Win32Native.INPUT_MOUSE,
            Mi = new Win32Native.MOUSEINPUT
            {
                Dx = normX,
                Dy = normY,
                Flags = Win32Native.MOUSEEVENTF_MOVE | Win32Native.MOUSEEVENTF_ABSOLUTE | Win32Native.MOUSEEVENTF_VIRTUALDESK,
            },
        };
        Win32Native.SendInput(1, [input], InputSize);
    }

    public static void InjectMouseButton(MouseBtn button, bool isDown)
    {
        var (flag, data) = button switch
        {
            MouseBtn.Left => (isDown ? Win32Native.MOUSEEVENTF_LEFTDOWN : Win32Native.MOUSEEVENTF_LEFTUP, 0u),
            MouseBtn.Right => (isDown ? Win32Native.MOUSEEVENTF_RIGHTDOWN : Win32Native.MOUSEEVENTF_RIGHTUP, 0u),
            MouseBtn.Middle => (isDown ? Win32Native.MOUSEEVENTF_MIDDLEDOWN : Win32Native.MOUSEEVENTF_MIDDLEUP, 0u),
            MouseBtn.XButton1 => (isDown ? Win32Native.MOUSEEVENTF_XDOWN : Win32Native.MOUSEEVENTF_XUP, 1u),
            MouseBtn.XButton2 => (isDown ? Win32Native.MOUSEEVENTF_XDOWN : Win32Native.MOUSEEVENTF_XUP, 2u),
            _ => (0u, 0u),
        };
        if (flag == 0) return;

        Span<byte> cmd = stackalloc byte[12];
        cmd[0] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmd.Slice(1, 4), flag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmd.Slice(5, 4), data);
        if (SystemInputBridgeClient.TrySendCommand(cmd))
            return;

        var input = new Win32Native.INPUT
        {
            Type = Win32Native.INPUT_MOUSE,
            Mi = new Win32Native.MOUSEINPUT { Flags = flag, MouseData = data },
        };
        Win32Native.SendInput(1, [input], InputSize);
    }

    public static void InjectMouseWheel(int delta, bool horizontal)
    {
        var flags = horizontal ? Win32Native.MOUSEEVENTF_HWHEEL : Win32Native.MOUSEEVENTF_WHEEL;
        var rawDelta = unchecked((uint)delta);

        Span<byte> cmd = stackalloc byte[12];
        cmd[0] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmd.Slice(1, 4), flags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmd.Slice(5, 4), rawDelta);
        if (SystemInputBridgeClient.TrySendCommand(cmd))
            return;

        var input = new Win32Native.INPUT
        {
            Type = Win32Native.INPUT_MOUSE,
            Mi = new Win32Native.MOUSEINPUT { Flags = flags, MouseData = rawDelta },
        };
        Win32Native.SendInput(1, [input], InputSize);
    }

    public static void InjectKey(ushort vk, ushort scan, bool isDown, bool extended, char unicodeChar)
    {
        var flags = isDown ? 0u : Win32Native.KEYEVENTF_KEYUP;
        if (extended) flags |= Win32Native.KEYEVENTF_EXTENDEDKEY;
        var uni = (unicodeChar != '\0' && isDown) ? (ushort)unicodeChar : (ushort)0;

        Span<byte> cmd = stackalloc byte[12];
        cmd[0] = 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(1, 2), vk);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(3, 2), scan);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmd.Slice(5, 4), flags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cmd.Slice(9, 2), uni);
        if (SystemInputBridgeClient.TrySendCommand(cmd))
            return;

        if (uni != 0)
        {
            var down = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_KEYBOARD,
                Ki = new Win32Native.KEYBDINPUT { Scan = uni, Flags = Win32Native.KEYEVENTF_UNICODE },
            };
            var up = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_KEYBOARD,
                Ki = new Win32Native.KEYBDINPUT { Scan = uni, Flags = Win32Native.KEYEVENTF_UNICODE | Win32Native.KEYEVENTF_KEYUP },
            };
            Win32Native.SendInput(2, [down, up], InputSize);
            return;
        }

        var keyInput = new Win32Native.INPUT
        {
            Type = Win32Native.INPUT_KEYBOARD,
            Ki = new Win32Native.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
        };
        Win32Native.SendInput(1, [keyInput], InputSize);
    }
}
