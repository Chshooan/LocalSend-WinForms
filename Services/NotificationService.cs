using System.Drawing;
using System.Windows.Forms;
using LocalSendWinForms.Core;

namespace LocalSendWinForms.Services;

/// <summary>
/// Surfaces Windows-standard notifications through the shell notification
/// surface (the same channel used by the Action Center). Notifications are
/// raised from the application's tray icon, so they appear as native Windows
/// notifications with the app's identity and land in the Action Center.
/// </summary>
public sealed class NotificationService
{
    /// <summary>The shared tray icon used as the notification source.</summary>
    public NotifyIcon? TrayIcon { get; set; }

    /// <summary>UI synchronization context, so balloon calls happen on the UI thread.</summary>
    public SynchronizationContext? Ui { get; set; }

    public bool Enabled { get; set; } = true;

    public void Info(string title, string body) => Show(title, body, ToolTipIcon.Info);

    public void Warn(string title, string body) => Show(title, body, ToolTipIcon.Warning);

    public void Error(string title, string body) => Show(title, body, ToolTipIcon.Error);

    private void Show(string title, string body, ToolTipIcon icon)
    {
        if (!Enabled) return;

#if WINRT_TOAST
        // Optional richer Action-Center toast. To enable, add the
        // CommunityToolkit.WinUI.Notifications package (see LocalSendWin.csproj) and
        // define the WINRT_TOAST compilation symbol. Falls back to the shell balloon.
        try
        {
            new ToastContentBuilder().AddText(title).AddText(body).Show();
            return;
        }
        catch (Exception ex) { Logger.Warn($"WinRT toast failed, using shell balloon: {ex.Message}"); }
#endif

        void Raise()
        {
            var tray = TrayIcon;
            if (tray is null) return;
            try
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = body;
                tray.BalloonTipIcon = icon;
                tray.ShowBalloonTip(6000);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Notification failed: {ex.Message}");
            }
        }

        var context = Ui;
        if (context is not null && SynchronizationContext.Current != context)
            context.Post(_ => Raise(), null);
        else
            Raise();
    }
}
