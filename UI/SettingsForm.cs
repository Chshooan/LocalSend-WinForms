using System.Drawing;
using System.Windows.Forms;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>
/// Settings panel: identity, save location, transfer options, receiving rules,
/// notifications, and File Explorer integration.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppHost _host;

    private readonly TextBox _deviceName = new();
    private readonly TextBox _downloadDir = new();
    private readonly NumericUpDown _port = new();
    private readonly CheckBox _useHttps = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _autoAccept = new();
    private readonly ComboBox _conflict = new();
    private readonly TextBox _pin = new();
    private readonly CheckBox _notifyReceive = new();
    private readonly CheckBox _notifyComplete = new();
    private readonly CheckBox _minimizeToTray = new();
    private readonly CheckBox _launchStartup = new();
    private readonly CheckBox _computeSha256 = new();
    private readonly Label _menuStatus = new();

    public SettingsForm(AppHost host)
    {
        _host = host;
        BuildUi();
        LoadFromSettings();
    }

    private void BuildUi()
    {
        Text = Localizer.T("Settings.Title");
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 640);
        Font = SystemFonts.MessageBoxFont;
        Padding = new Padding(12);

        // ---- General ----
        var general = NewGroup(Localizer.T("Settings.General"));
        var generalLayout = NewInnerTable();
        AddRow(generalLayout, Localizer.T("Settings.DeviceName"), _deviceName);
        AddRow(generalLayout, Localizer.T("Settings.DownloadDir"), BuildPathRow());
        _port.Minimum = 1;
        _port.Maximum = 65535;
        _port.Width = 120;
        AddRow(generalLayout, Localizer.T("Settings.Port"), _port);
        _useHttps.Text = Localizer.T("Settings.UseHttps");
        _useHttps.AutoSize = true;
        AddRow(generalLayout, string.Empty, _useHttps);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Width = 160;
        _language.Items.Add("简体中文");
        _language.Items.Add("English");
        AddRow(generalLayout, Localizer.T("Settings.Language"), _language);
        general.Controls.Add(generalLayout);

        // ---- Receiving ----
        var receiving = NewGroup(Localizer.T("Settings.Receive"));
        var receivingLayout = NewInnerTable();
        _autoAccept.Text = Localizer.T("Settings.AutoAccept");
        _autoAccept.AutoSize = true;
        AddRow(receivingLayout, string.Empty, _autoAccept);
        _conflict.DropDownStyle = ComboBoxStyle.DropDownList;
        _conflict.Width = 240;
        _conflict.Items.Add(Localizer.T("Settings.ConflictRename"));
        _conflict.Items.Add(Localizer.T("Settings.ConflictOverwrite"));
        AddRow(receivingLayout, Localizer.T("Settings.Conflict"), _conflict);
        _pin.Width = 160;
        AddRow(receivingLayout, Localizer.T("Settings.Pin"), _pin);
        receiving.Controls.Add(receivingLayout);

        // ---- Notifications ----
        var notifications = NewGroup(Localizer.T("Settings.Notify"));
        var notificationLayout = NewInnerTable();
        _notifyReceive.Text = Localizer.T("Settings.NotifyReceive");
        _notifyReceive.AutoSize = true;
        AddRow(notificationLayout, string.Empty, _notifyReceive);
        _notifyComplete.Text = Localizer.T("Settings.NotifyComplete");
        _notifyComplete.AutoSize = true;
        AddRow(notificationLayout, string.Empty, _notifyComplete);
        notifications.Controls.Add(notificationLayout);

        // ---- Behaviour ----
        var behaviour = NewGroup(Localizer.T("Settings.General"));
        var behaviourLayout = NewInnerTable();
        _minimizeToTray.Text = Localizer.T("Settings.MinimizeTray");
        _minimizeToTray.AutoSize = true;
        AddRow(behaviourLayout, string.Empty, _minimizeToTray);
        _launchStartup.Text = Localizer.T("Settings.LaunchStartup");
        _launchStartup.AutoSize = true;
        AddRow(behaviourLayout, string.Empty, _launchStartup);
        _computeSha256.Text = Localizer.T("Settings.ComputeSha256");
        _computeSha256.AutoSize = true;
        AddRow(behaviourLayout, string.Empty, _computeSha256);
        behaviour.Controls.Add(behaviourLayout);

        // ---- Explorer integration ----
        var explorer = NewGroup(Localizer.T("Settings.Explorer"));
        var explorerLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(6),
        };
        explorerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        explorerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        explorerLayout.RowCount = 2;

        var explorerButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        var install = new Button { Text = Localizer.T("Settings.InstallMenu"), AutoSize = true };
        install.Click += (_, _) => OnInstallMenu();
        var remove = new Button { Text = Localizer.T("Settings.RemoveMenu"), AutoSize = true };
        remove.Click += (_, _) => OnRemoveMenu();
        explorerButtons.Controls.Add(install);
        explorerButtons.Controls.Add(remove);

        _menuStatus.AutoSize = true;
        _menuStatus.ForeColor = SystemColors.GrayText;
        _menuStatus.Margin = new Padding(3, 5, 3, 3);

        explorerLayout.Controls.Add(explorerButtons, 0, 0);
        explorerLayout.Controls.Add(_menuStatus, 0, 1);
        explorer.Controls.Add(explorerLayout);

        // ---- Assemble ----
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(4),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var groups = new Control[] { general, receiving, notifications, behaviour, explorer };
        layout.RowCount = groups.Length;
        foreach (var _ in groups) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < groups.Length; i++) layout.Controls.Add(groups[i], 0, i);

        // ---- Buttons ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(0, 8, 0, 0),
        };
        var save = new Button { Text = Localizer.T("Settings.Save"), Width = 100, Height = 32 };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = Localizer.T("Settings.Cancel"), Width = 100, Height = 32 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var openFolder = new Button { Text = Localizer.T("Settings.OpenFolder"), Width = 130, Height = 32 };
        openFolder.Click += (_, _) => ShellIntegration.OpenInExplorer(_downloadDir.Text);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(openFolder);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        UpdateMenuStatus();
    }

    private Control BuildPathRow()
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _downloadDir.Dock = DockStyle.Fill;
        var browse = new Button { Text = Localizer.T("Settings.Browse"), AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _downloadDir.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _downloadDir.Text = dialog.SelectedPath;
        };
        panel.Controls.Add(_downloadDir, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private static GroupBox NewGroup(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Padding = new Padding(8),
        Margin = new Padding(0, 0, 0, 8),
    };

    private static TableLayoutPanel NewInnerTable()
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 8, 3, 3),
        };
        control.Margin = new Padding(3, 5, 3, 3);
        table.Controls.Add(l, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void LoadFromSettings()
    {
        var s = _host.Settings;
        _deviceName.Text = s.DeviceName;
        _downloadDir.Text = s.DownloadDirectory;
        _port.Value = Math.Clamp(s.Port, 1, 65535);
        _useHttps.Checked = s.UseHttps;
        _language.SelectedIndex = Localizer.Normalize(s.Language) == Localizer.English ? 1 : 0;
        _autoAccept.Checked = s.AutoAccept;
        _conflict.SelectedIndex = string.Equals(s.ConflictMode, "overwrite", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _pin.Text = s.Pin ?? string.Empty;
        _notifyReceive.Checked = s.NotifyOnReceive;
        _notifyComplete.Checked = s.NotifyOnComplete;
        _minimizeToTray.Checked = s.MinimizeToTray;
        _launchStartup.Checked = s.LaunchAtStartup;
        _computeSha256.Checked = s.ComputeSha256;
    }

    private void OnInstallMenu()
    {
        ShellIntegration.InstallMenu(ShellIntegration.ExecutablePath, VerbText());
        UpdateMenuStatus();
        MessageBox.Show(this, Localizer.T("Msg.MenuInstalled"), Localizer.T("App.Name"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnRemoveMenu()
    {
        ShellIntegration.RemoveMenu();
        UpdateMenuStatus();
        MessageBox.Show(this, Localizer.T("Msg.MenuRemoved"), Localizer.T("App.Name"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateMenuStatus() =>
        _menuStatus.Text = (ShellIntegration.IsMenuInstalled() ? "● " : "○ ") + Localizer.T("Settings.InstallMenu");

    private static string VerbText() =>
        Localizer.Current == Localizer.English ? "Send with LocalSend" : "用 LocalSend 发送";

    private void Save()
    {
        var updated = _host.Settings.Clone();
        updated.DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text)
            ? Environment.MachineName
            : _deviceName.Text.Trim();
        updated.DownloadDirectory = string.IsNullOrWhiteSpace(_downloadDir.Text)
            ? AppSettings.DefaultDownloadDirectory()
            : _downloadDir.Text.Trim();
        updated.Port = (int)_port.Value;
        updated.UseHttps = _useHttps.Checked;
        updated.Language = _language.SelectedIndex == 1 ? Localizer.English : Localizer.Chinese;
        updated.AutoAccept = _autoAccept.Checked;
        updated.ConflictMode = _conflict.SelectedIndex == 1 ? "overwrite" : "rename";
        updated.Pin = string.IsNullOrWhiteSpace(_pin.Text) ? null : _pin.Text.Trim();
        updated.NotifyOnReceive = _notifyReceive.Checked;
        updated.NotifyOnComplete = _notifyComplete.Checked;
        updated.MinimizeToTray = _minimizeToTray.Checked;
        updated.LaunchAtStartup = _launchStartup.Checked;
        updated.ComputeSha256 = _computeSha256.Checked;

        try { Directory.CreateDirectory(updated.DownloadDirectory); }
        catch (Exception ex) { Core.Logger.Warn($"Cannot create download directory: {ex.Message}"); }

        _host.ApplySettings(updated);

        DialogResult = DialogResult.OK;
        Close();
    }
}
