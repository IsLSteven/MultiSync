using System.Diagnostics;
using MultiSync.Linux.Network;
using MultiSync.Linux.Protocol;
using MultiSync.Linux.WebUi;

var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
var uiPort = 5055;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--ui-port" && int.TryParse(args[i + 1], out var p) && p > 1024)
        uiPort = p;
}

using var coordinator = new LinuxMultiSyncCoordinator();
coordinator.Start();

LocalDashboardServer? webUi = null;
try
{
    webUi = new LocalDashboardServer(coordinator, uiPort);
    webUi.Start();
}
catch (Exception ex)
{
    Console.WriteLine($"[Aviso] Painel web local não pôde iniciar na porta {uiPort}: {ex.Message}");
}

var cfg = coordinator.Store.GetSnapshot();
var ip = LanAddressHelper.GetPreferredIPv4() ?? "127.0.0.1";
Console.WriteLine("============================================================");
Console.WriteLine("  ⇄ MultiSync Linux v0.6.0 (Debian / Mint / Ubuntu / Arch)");
Console.WriteLine("============================================================");
Console.WriteLine($"  Distro:          {SystemInfo.DetectDistroSummary()}");
Console.WriteLine($"  Sessão gráfica:  {(SystemInfo.IsWayland ? "Wayland" : "X11")}");
Console.WriteLine($"  Backend Input:   {coordinator.Injector.ActiveBackend}");
Console.WriteLine($"  Este PC:         {cfg.DeviceName} ({ip})");
Console.WriteLine($"  Modo atual:      {cfg.Role}");
Console.WriteLine($"  PIN de parear:   {cfg.PairingPin}");
Console.WriteLine($"  Portas LAN:      TCP {cfg.KvmPort} (KVM) | UDP {cfg.DiscoveryPort} (LAN) | TCP {cfg.ControlPort} (Ctrl/Arquivos/Tela) | UDP 5053 (Áudio)");
if (webUi != null)
    Console.WriteLine($"  Painel Visual:   {webUi.Url}");
Console.WriteLine("============================================================");
Console.WriteLine("Pressione Ctrl+C para encerrar.");

if (!headless && webUi != null && OperatingSystem.IsLinux())
{
    try
    {
        Process.Start(new ProcessStartInfo("xdg-open", webUi.Url)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
    }
    catch { }
}

var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.Set();

quit.Wait();
webUi?.Dispose();
