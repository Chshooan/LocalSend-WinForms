using System.Drawing;
using System.Windows.Forms;

namespace LocalSendWinForms.Services;

/// <summary>
/// Owns the system-tray icon and its context menu. Exposes the actions as
/// events so the composition root can wire them to the appropriate windows:
/// open the main window, quick-send, settings, open the transfer folder,
/// show version info, or exit.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _openItem = null!;
    private ToolStripMenuItem _quickItem = null!;
    private ToolStripMenuItem _settingsItem = null!;
    private ToolStripMenuItem _folderItem = null!;
    private ToolStripMenuItem _aboutItem = null!;
    private ToolStripMenuItem _exitItem = null!;

    public event Action? OpenRequested;
    public event Action? QuickSendRequested;
    public event Action? SettingsRequested;
    public event Action? OpenFolderRequested;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public NotifyIcon NotifyIcon { get; }

    public TrayService()
    {
        _menu = new ContextMenuStrip();
        BuildMenu();

        NotifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Visible = true,
            ContextMenuStrip = _menu,
            Text = Truncate(Localizer.T("Tray.Tooltip"), 63),
        };
        NotifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        NotifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        _openItem = new ToolStripMenuItem(Localizer.T("Tray.Open"));
        _openItem.Font = new Font(_openItem.Font, System.Drawing.FontStyle.Bold);
        _openItem.Click += (_, _) => OpenRequested?.Invoke();

        _quickItem = new ToolStripMenuItem(Localizer.T("Tray.QuickSend"));
        _quickItem.Click += (_, _) => QuickSendRequested?.Invoke();

        _settingsItem = new ToolStripMenuItem(Localizer.T("Tray.Settings"));
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        _folderItem = new ToolStripMenuItem(Localizer.T("Tray.OpenFolder"));
        _folderItem.Click += (_, _) => OpenFolderRequested?.Invoke();

        _aboutItem = new ToolStripMenuItem(Localizer.T("Tray.About"));
        _aboutItem.Click += (_, _) => AboutRequested?.Invoke();

        _exitItem = new ToolStripMenuItem(Localizer.T("Tray.Exit"));
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(_openItem);
        _menu.Items.Add(_quickItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_folderItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_aboutItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
    }

    /// <summary>Rebuilds the menu text after a language change and updates the tooltip.</summary>
    public void ApplyLanguage()
    {
        BuildMenu();
        NotifyIcon.Text = Truncate(Localizer.T("Tray.Tooltip"), 63);
    }

    public void ShowBalloon(string title, string body) =>
        NotifyIcon.ShowBalloonTip(4000, title, body, ToolTipIcon.Info);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        NotifyIcon.Visible = false;
        NotifyIcon.Dispose();
        _menu.Dispose();
    }
}
