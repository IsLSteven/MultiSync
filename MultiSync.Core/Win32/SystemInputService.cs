using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace MultiSync.Core.Win32;

/// <summary>
/// Runs as NT AUTHORITY\SYSTEM (--input-service) and attaches a dedicated worker thread to whichever
/// Windows desktop is currently receiving input (Default desktop, UAC Secure Desktop Winlogon, or Lock Screen),
/// allowing mouse &amp; keyboard control even over UAC permission dialogs and elevated system prompts.
/// </summary>
public sealed class SystemInputWindowsService : ServiceBase
{
    public const string ServiceNameConst = "MultiSyncInputHelper";
    public const string PipeName = "MultiSync.SystemInput.v1";

    private CancellationTokenSource? _cts;
    private Thread? _desktopWorker;
    private Task? _pipeServerTask;
    private readonly BlockingCollection<byte[]> _commandQueue = new(boundedCapacity: 4096);

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

        _desktopWorker = new Thread(() => DesktopInjectionLoop(token))
        {
            Name = "MultiSync.SystemDesktopInjector",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _desktopWorker.Start();

        _pipeServerTask = Task.Run(() => PipeAcceptLoopAsync(token), token);
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _commandQueue.CompleteAdding();
        try { _desktopWorker?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _pipeServerTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
    }

    private async Task PipeAcceptLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateSecurePipeServer();
                await pipe.WaitForConnectionAsync(cancel);
                _ = HandlePipeClientAsync(pipe, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                try { await Task.Delay(500, cancel); } catch { break; }
            }
        }
    }

    private async Task HandlePipeClientAsync(NamedPipeServerStream pipe, CancellationToken cancel)
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
                    _commandQueue.TryAdd(copy);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateSecurePipeServer()
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
            PipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            0,
            security);
    }

    private void DesktopInjectionLoop(CancellationToken cancel)
    {
        var inputSize = Marshal.SizeOf<Win32Native.INPUT>();
        long lastDesktopCheckTick = 0;
        nint currentDesktop = nint.Zero;

        try
        {
            foreach (var cmd in _commandQueue.GetConsumingEnumerable(cancel))
            {
                var now = Environment.TickCount64;
                if (now - lastDesktopCheckTick >= 100 || currentDesktop == nint.Zero)
                {
                    lastDesktopCheckTick = now;
                    AttachToActiveInputDesktop(ref currentDesktop);
                }

                var sent = ExecuteRawCommand(cmd, inputSize);
                if (sent == 0)
                {
                    // Active desktop just switched (e.g. UAC Winlogon just appeared) — reattach immediately and retry!
                    AttachToActiveInputDesktop(ref currentDesktop);
                    _ = ExecuteRawCommand(cmd, inputSize);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        finally
        {
            if (currentDesktop != nint.Zero)
                CloseDesktop(currentDesktop);
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

    private static uint ExecuteRawCommand(byte[] cmd, int inputSize)
    {
        // Fixed 12-byte wire command:
        // [0] = op (1 = mouseAbsNorm, 2 = mouseBtn, 3 = mouseWheel, 4 = key)
        var op = cmd[0];
        switch (op)
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
                if (now - _lastConnectAttemptTick < 2000)
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

            var needsBinaryCopy = !File.Exists(serviceExe) ||
                                  new FileInfo(serviceExe).Length != new FileInfo(currentExe).Length;

            using var existing = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, SystemInputWindowsService.ServiceNameConst, StringComparison.OrdinalIgnoreCase));

            if (existing != null && needsBinaryCopy)
            {
                if (existing.Status != ServiceControllerStatus.Stopped)
                {
                    existing.Stop();
                    existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
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
