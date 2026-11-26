using System.Threading;
using System.Windows;
using MultiSync.App.Services;
using MultiSync.App.Tray;

namespace MultiSync.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private AppCoordinator? _coordinator;
    private SystemTrayHost? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\MultiSync.UnifiedApp.Instance", out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show(
                "O MultiSync já está em execução na bandeja do sistema (perto do relógio).",
                "MultiSync",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _coordinator = new AppCoordinator();
        _window = new MainWindow(_coordinator);

        _tray = new SystemTrayHost(
            onShowWindow: ShowMainWindow,
            onQuickSwapRoles: async () => await _window.QuickSwapMasterSlaveFromTrayAsync(),
            onToggleLock: () => _coordinator.Engine.ToggleCursorLock(),
            onExitApp: ShutdownAll);

        _coordinator.Engine.TransferActivity += msg =>
        {
            if (msg.Contains("Ctrl+V", StringComparison.OrdinalIgnoreCase))
                _tray?.ShowInfo("MultiSync", msg);
        };

        _coordinator.StartAll();

        if (!e.Args.Contains("--minimized"))
            ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShutdownAll()
    {
        _tray?.Dispose();
        _coordinator?.Dispose();
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _coordinator?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
