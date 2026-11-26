using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;

namespace MultiSync.Core.Win32;

/// <summary>
/// Windows Service running as LocalSystem in Session 0 (--input-service).
/// Because Windows enforces Session 0 Isolation, this service duplicates its LocalSystem token into
/// the active interactive console session (e.g. Session 1) and launches "--input-session" on WinSta0,
/// giving the child process full SYSTEM privileges on BOTH WinSta0\Default and WinSta0\Winlogon (UAC / Lock Screen).
/// </summary>
public sealed class SystemInputWindowsService : ServiceBase
{
    public const string ServiceNameConst = "MultiSyncInputHelper";
    public const string PipeName = "MultiSync.SystemInput.v1";

    private CancellationTokenSource? _cts;
    private Task? _watchdogTask;
    private int _childPid;

    public SystemInputWindowsService()
    {
        ServiceName = ServiceNameConst;
        CanStop = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _watchdogTask = Task.Run(() => SessionChildWatchdogAsync(token), token);
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        KillChild();
        try { _watchdogTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
    }

    private async Task SessionChildWatchdogAsync(CancellationToken cancel)
    {
        uint activeSessionForChild = uint.MaxValue;

        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var consoleSession = WTSGetActiveConsoleSessionId();
                if (consoleSession != 0xFFFFFFFF && consoleSession != 0)
                {
                    var alive = IsChildAlive(_childPid);
                    if (!alive || activeSessionForChild != consoleSession)
                    {
                        KillChild();
                        if (LaunchSessionChild(consoleSession, out var newPid))
                        {
                            _childPid = newPid;
                            activeSessionForChild = consoleSession;
                        }
                    }
                }
            }
            catch
            {
                // retry on next tick
            }

            try { await Task.Delay(1500, cancel); }
            catch (OperationCanceledException) { break; }
        }

        KillChild();
    }

    private static bool IsChildAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void KillChild()
    {
        var pid = Interlocked.Exchange(ref _childPid, 0);
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static bool LaunchSessionChild(uint sessionId, out int pid)
    {
        pid = 0;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        nint myToken = nint.Zero;
        nint dupToken = nint.Zero;
        try
        {
            const uint TOKEN_ALL_ACCESS = 0xF01FF;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out myToken))
                return false;

            const uint MAXIMUM_ALLOWED = 0x02000000;
            if (!DuplicateTokenEx(myToken, MAXIMUM_ALLOWED, nint.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out dupToken))
                return false;

            // Retarget our LocalSystem primary token to the active interactive console session (e.g. Session 1)
            if (!SetTokenInformation(dupToken, 12 /* TokenSessionId */, ref sessionId, sizeof(uint)))
                return false;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };

            const uint CREATE_NO_WINDOW = 0x08000000;
            const uint HIGH_PRIORITY_CLASS = 0x00000080;
            var cmdLine = $"\"{exePath}\" --input-session";

            if (!CreateProcessAsUserW(
                    dupToken,
                    exePath,
                    cmdLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    CREATE_NO_WINDOW | HIGH_PRIORITY_CLASS,
                    nint.Zero,
                    Path.GetDirectoryName(exePath),
                    ref si,
                    out var pi))
            {
                return false;
            }

            pid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (dupToken != nint.Zero) CloseHandle(dupToken);
            if (myToken != nint.Zero) CloseHandle(myToken);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(nint tokenHandle, int tokenInformationClass, ref uint tokenInformation, uint tokenInformationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        nint token, string? applicationName, string commandLine, nint procAttrs, nint threadAttrs,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION procInfo);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// Runs as NT AUTHORITY\SYSTEM INSIDE the interactive user session (Session 1 / WinSta0) via "--input-session".
/// Switches its dedicated injector thread seamlessly between WinSta0\Default and WinSta0\Winlogon (UAC / Lock Screen)
/// so Slave mouse &amp; keyboard input NEVER freezes when permission prompts or admin dialogs appear.
/// </summary>
public static class SystemSessionInputWorker
{
    public static int Run()
    {
        using var cts = new CancellationTokenSource();
        using var queue = new BlockingCollection<byte[]>(boundedCapacity: 4096);

        var desktopWorker = new Thread(() => DesktopInjectionLoop(queue, cts.Token))
        {
            Name = "MultiSync.Session1SystemInjector",
            IsBackground = false,
            Priority = ThreadPriority.Highest,
        };
        desktopWorker.Start();

        PipeAcceptLoopAsync(queue, cts.Token).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task PipeAcceptLoopAsync(BlockingCollection<byte[]> queue, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipeServer();
                await pipe.WaitForConnectionAsync(cancel);
                _ = HandlePipeClientAsync(pipe, queue, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                try { await Task.Delay(300, cancel); } catch { break; }
            }
        }
    }

    private static async Task HandlePipeClientAsync(NamedPipeServerStream pipe, BlockingCollection<byte[]> queue, CancellationToken cancel)
    {
        await using (pipe)
        {
            var buf = new byte[12];
            while (!cancel.IsCancellationRequested && pipe.IsConnected)
            {
                try
                {
                    await pipe.ReadExactlyAsync(buf, cancel);
                    var copy = new byte[12];
                    Buffer.BlockCopy(buf, 0, copy, 0, 12);
                    queue.TryAdd(copy);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            SystemInputWindowsService.PipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            0,
            security);
    }

    private static void DesktopInjectionLoop(BlockingCollection<byte[]> queue, CancellationToken cancel)
    {
        var inputSize = Marshal.SizeOf<Win32Native.INPUT>();
        long lastCheckTick = 0;
        nint currentDesktop = nint.Zero;

        try
        {
            foreach (var cmd in queue.GetConsumingEnumerable(cancel))
            {
                // Coalesce consecutive MouseMove commands already sitting in the queue so the cursor
                // jumps straight to the newest coordinate with zero backlog lag!
                var activeCmd = cmd;
                if (activeCmd[0] == 1)
                {
                    while (queue.Count > 0 && queue.TryTake(out var next))
                    {
                        if (next[0] == 1)
                        {
                            activeCmd = next;
                        }
                        else
                        {
                            // Flush the latest move before executing the non-move (click/key/wheel)
                            ExecuteWithDesktopRecovery(activeCmd, inputSize, ref currentDesktop, ref lastCheckTick);
                            activeCmd = next;
                            break;
                        }
                    }
                }

                ExecuteWithDesktopRecovery(activeCmd, inputSize, ref currentDesktop, ref lastCheckTick);
            }
        }
        catch (OperationCanceledException)
        {
            // stop
        }
        finally
        {
            if (currentDesktop != nint.Zero)
                CloseDesktop(currentDesktop);
        }
    }

    private static void ExecuteWithDesktopRecovery(byte[] cmd, int inputSize, ref nint currentDesktop, ref long lastCheckTick)
    {
        var now = Environment.TickCount64;
        if (currentDesktop == nint.Zero || now - lastCheckTick >= 500)
        {
            lastCheckTick = now;
            AttachToActiveInputDesktop(ref currentDesktop);
        }

        var sent = ExecuteCommand(cmd, inputSize);
        if (sent == 0)
        {
            // Desktop transitioned (Default <-> Winlogon UAC) — switch immediately and retry!
            AttachToActiveInputDesktop(ref currentDesktop);
            _ = ExecuteCommand(cmd, inputSize);
        }
    }

    private static void AttachToActiveInputDesktop(ref nint currentDesktop)
    {
        const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
        const uint GENERIC_ALL = 0x10000000;

        var desk = OpenInputDesktop(DF_ALLOWOTHERACCOUNTHOOK, false, GENERIC_ALL);
        if (desk == nint.Zero) return;

        if (SetThreadDesktop(desk))
        {
            if (currentDesktop != nint.Zero && currentDesktop != desk)
                CloseDesktop(currentDesktop);
            currentDesktop = desk;
        }
        else
        {
            CloseDesktop(desk);
        }
    }

    private static uint ExecuteCommand(byte[] cmd, int inputSize)
    {
        switch (cmd[0])
        {
            case 1:
            {
                var normX = BinaryPrimitives.ReadInt32BigEndian(cmd.AsSpan(1, 4));
                var normY = BinaryPrimitives.ReadInt32BigEndian(cmd.AsSpan(5, 4));
                var inp = new Win32Native.INPUT
                {
                    Type = Win32Native.INPUT_MOUSE,
                    Mi = new Win32Native.MOUSEINPUT
                    {
                        Dx = normX,
                        Dy = normY,
                        Flags = Win32Native.MOUSEEVENTF_MOVE | Win32Native.MOUSEEVENTF_ABSOLUTE | Win32Native.MOUSEEVENTF_VIRTUALDESK,
                    },
                };
                return Win32Native.SendInput(1, [inp], inputSize);
            }
            case 2:
            {
                var flags = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(1, 4));
                var data = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(5, 4));
                var inp = new Win32Native.INPUT
                {
                    Type = Win32Native.INPUT_MOUSE,
                    Mi = new Win32Native.MOUSEINPUT { Flags = flags, MouseData = data },
                };
                return Win32Native.SendInput(1, [inp], inputSize);
            }
            case 3:
            {
                var flags = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(1, 4));
                var delta = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(5, 4));
                var inp = new Win32Native.INPUT
                {
                    Type = Win32Native.INPUT_MOUSE,
                    Mi = new Win32Native.MOUSEINPUT { Flags = flags, MouseData = delta },
                };
                return Win32Native.SendInput(1, [inp], inputSize);
            }
            case 4:
            {
                var vk = BinaryPrimitives.ReadUInt16BigEndian(cmd.AsSpan(1, 2));
                var scan = BinaryPrimitives.ReadUInt16BigEndian(cmd.AsSpan(3, 2));
                var flags = BinaryPrimitives.ReadUInt32BigEndian(cmd.AsSpan(5, 4));
                var unicode = BinaryPrimitives.ReadUInt16BigEndian(cmd.AsSpan(9, 2));

                if (unicode != 0)
                {
                    var down = new Win32Native.INPUT
                    {
                        Type = Win32Native.INPUT_KEYBOARD,
                        Ki = new Win32Native.KEYBDINPUT { Scan = unicode, Flags = Win32Native.KEYEVENTF_UNICODE },
                    };
                    var up = new Win32Native.INPUT
                    {
                        Type = Win32Native.INPUT_KEYBOARD,
                        Ki = new Win32Native.KEYBDINPUT { Scan = unicode, Flags = Win32Native.KEYEVENTF_UNICODE | Win32Native.KEYEVENTF_KEYUP },
                    };
                    return Win32Native.SendInput(2, [down, up], inputSize);
                }

                var inp = new Win32Native.INPUT
                {
                    Type = Win32Native.INPUT_KEYBOARD,
                    Ki = new Win32Native.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
                };
                return Win32Native.SendInput(1, [inp], inputSize);
            }
            default:
                return 1;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint hDesktop);
}

public static class SystemInputBridgeClient
{
    private static readonly Lock PipeLock = new();
    private static NamedPipeClientStream? _pipe;
    private static long _lastConnectAttemptTick;

    public static bool TrySendCommand(ReadOnlySpan<byte> cmd12)
    {
        lock (PipeLock)
        {
            if (_pipe is not { IsConnected: true })
            {
                var now = Environment.TickCount64;
                if (now - _lastConnectAttemptTick < 1500)
                    return false;
                _lastConnectAttemptTick = now;

                _pipe?.Dispose();
                _pipe = null;
                try
                {
                    var client = new NamedPipeClientStream(".", SystemInputWindowsService.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(80);
                    _pipe = client;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                _pipe.Write(cmd12);
                return true;
            }
            catch
            {
                _pipe.Dispose();
                _pipe = null;
                return false;
            }
        }
    }
}

public static class SystemInputServiceInstaller
{
    public static void EnsureInstalledAndRunning()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                return;

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return;

            var hostDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MultiSync");
            Directory.CreateDirectory(hostDir);
            var serviceExe = Path.Combine(hostDir, "MultiSyncServiceHost.exe");

            var needsBinaryCopy = !FilesMatchByHash(currentExe, serviceExe);

            using var existing = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, SystemInputWindowsService.ServiceNameConst, StringComparison.OrdinalIgnoreCase));

            if (existing != null && needsBinaryCopy)
            {
                if (existing.Status != ServiceControllerStatus.Stopped)
                {
                    existing.Stop();
                    existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }

                // Make sure any Session-1 child host process exits so MultiSyncServiceHost.exe can be overwritten
                foreach (var p in Process.GetProcessesByName("MultiSyncServiceHost"))
                {
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(1500); } catch { }
                    finally { p.Dispose(); }
                }
            }

            if (needsBinaryCopy)
                File.Copy(currentExe, serviceExe, overwrite: true);

            var binArg = $"\\\"{serviceExe}\\\" --input-service";
            if (existing == null)
            {
                RunSc($"create {SystemInputWindowsService.ServiceNameConst} binPath= \"{binArg}\" start= auto obj= LocalSystem");
                RunSc($"description {SystemInputWindowsService.ServiceNameConst} \"MultiSync System Input Helper (UAC & Lock Screen support)\"");
            }

            RunSc($"start {SystemInputWindowsService.ServiceNameConst}");
        }
        catch
        {
            // Non-fatal: falls back to direct elevated SendInput
        }
    }

    private static bool FilesMatchByHash(string pathA, string pathB)
    {
        if (!File.Exists(pathA) || !File.Exists(pathB)) return false;
        if (new FileInfo(pathA).Length != new FileInfo(pathB).Length) return false;
        using var fsA = File.OpenRead(pathA);
        using var fsB = File.OpenRead(pathB);
        return SHA256.HashData(fsA).AsSpan().SequenceEqual(SHA256.HashData(fsB));
    }

    private static void RunSc(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        proc?.WaitForExit(4000);
    }
}
