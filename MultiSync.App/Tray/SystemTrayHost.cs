using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace MultiSync.App.Tray;

public sealed class SystemTrayHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public SystemTrayHost(
        Action onShowWindow,
        Func<Task> onQuickSwapRoles,
        Action onToggleLock,
        Action onExitApp)
    {
        _icon = BuildVectorIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir MultiSync", null, (_, _) => onShowWindow());
        menu.Items.Add("Inverter Master ↔ Slave", null, async (_, _) => await onQuickSwapRoles());
        menu.Items.Add("Travar / Destravar Cursor na Tela Atual (Ctrl+Alt+Win+L)", null, (_, _) => onToggleLock());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair do MultiSync", null, (_, _) => onExitApp());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "MultiSync — Controle Multi-PC",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => onShowWindow();
    }

    private static Icon BuildVectorIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var grad = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32),
                Color.FromArgb(255, 37, 99, 235),
                Color.FromArgb(255, 16, 185, 129),
                LinearGradientMode.ForwardDiagonal);
            using var pen = new Pen(Color.White, 2f);

            g.FillEllipse(grad, 1, 1, 30, 30);
            g.DrawRectangle(pen, 6, 9, 9, 11);
            g.DrawRectangle(pen, 17, 12, 9, 11);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
