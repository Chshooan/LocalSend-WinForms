using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>Version / about dialog, reachable from the tray menu.</summary>
public sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = Localizer.T("About.Title");
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 340);
        Font = SystemFonts.MessageBoxFont;
        Padding = new Padding(16);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        var title = new Label
        {
            Text = Localizer.T("App.Name"),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 15f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 34,
        };
        var versionLabel = new Label
        {
            Text = Localizer.T("About.Version", version),
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = SystemColors.GrayText,
        };
        var description = new Label
        {
            Text = Localizer.T("About.Description"),
            Dock = DockStyle.Top,
            Height = 74,
        };
        var protocol = new Label
        {
            Text = Localizer.T("About.Protocol"),
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = SystemColors.GrayText,
        };
        var license = new Label
        {
            Text = Localizer.T("About.License"),
            Dock = DockStyle.Top,
            Height = 22,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0),
        };
        var openLog = new Button { Text = Localizer.T("About.OpenLog"), AutoSize = true, Height = 30 };
        openLog.Click += (_, _) => ShellIntegration.OpenInExplorer(SettingsStore.ConfigDirectory);
        var project = new Button { Text = Localizer.T("About.Project"), AutoSize = true, Height = 30 };
        project.Click += (_, _) => ShellIntegration.OpenUrl("https://github.com/localsend/protocol");
        var close = new Button { Text = Localizer.T("Progress.Close"), Width = 100, Height = 30 };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(openLog);
        buttons.Controls.Add(project);
        buttons.Controls.Add(close);

        Controls.Add(buttons);
        Controls.Add(license);
        Controls.Add(protocol);
        Controls.Add(description);
        Controls.Add(versionLabel);
        Controls.Add(title);

        AcceptButton = close;
        CancelButton = close;
    }
}
