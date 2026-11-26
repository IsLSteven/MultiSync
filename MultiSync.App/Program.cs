using System.ServiceProcess;
using MultiSync.Core.Win32;

namespace MultiSync.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--input-service", StringComparer.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new SystemInputWindowsService());
            return 0;
        }

        if (args.Contains("--input-session", StringComparer.OrdinalIgnoreCase))
        {
            return SystemSessionInputWorker.Run();
        }

        // Best-effort install/start of LocalSystem helper (spawns Session-1 SYSTEM worker for UAC & Lock Screen)
        SystemInputServiceInstaller.EnsureInstalledAndRunning();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
