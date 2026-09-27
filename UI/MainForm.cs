using System.Drawing;
using System.Windows.Forms;
using LocalSendWinForms.Core;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>
/// Primary window: choose a peer from the discovered device list, queue files
/// (via the buttons or drag-and-drop) and start the transfer.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppHost _host;
    private readonly TransferCoordinator _coordinator;

    private readonly ListView _deviceList = new();
    private readonly ListBox _fileList = new();
    private readonly Label _deviceHeader = new();
    private readonly Label _fileHeader = new();
    private readonly Label _status = new();
    private readonly Label _dropHint = new();
    private readonly Button _refreshButton = new();
    private readonly Button _addFilesButton = new();
    private readonly Button _addFolderButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _sendButton = new();
    private readonly Button _settingsButton = new();

    private readonly List<string> _files = new();
    private readonly HashSet<string> _fileSet = new(StringComparer.OrdinalIgnoreCase);

    public MainForm(AppHost host, TransferCoordinator coordinator)
    {
        _host = host;
        _coordinator = coordinator;

        BuildUi();
        ApplyLanguage();

        _host.DeviceAdded += OnDeviceAdded;
        _host.DeviceRemoved += OnDeviceRemoved;
        _host.SettingsChanged += ApplyLanguage;

        Load += (_, _) =>
        {
            ReloadDevices();
            UpdateStatus();
        };
    }

    private void BuildUi()
    {
        Text = Localizer.T("Main.Title");
        Icon = AppIcon.Load();
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AllowDrop = true;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        // ---- device list (left) ----
        _deviceList.Dock = DockStyle.Fill;
        _deviceList.View = View.Details;
        _deviceList.FullRowSelect = true;
        _deviceList.MultiSelect = false;
        _deviceList.HideSelection = false;
        _deviceList.Columns.Add("Name", 190);
        _deviceList.Columns.Add("Address", 130);
        _deviceList.Columns.Add("Type", 70);

        _deviceHeader.Dock = DockStyle.Top;
        _deviceHeader.Height = 30;
        _deviceHeader.Font = new Font(Font, FontStyle.Bold);
        _deviceHeader.TextAlign = ContentAlignment.MiddleLeft;
        _deviceHeader.Padding = new Padding(4, 0, 4, 0);

        _refreshButton.Dock = DockStyle.Top;
        _refreshButton.Height = 30;
        _refreshButton.Click += async (_, _) =>
        {
            _status.Text = Localizer.T("Main.Searching");
            await _host.RefreshDevicesAsync();
            ReloadDevices();
        };

        var devicePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        devicePanel.Controls.Add(_deviceList);
        devicePanel.Controls.Add(_refreshButton);
        devicePanel.Controls.Add(_deviceHeader);

        // ---- file list (right) ----
        _fileHeader.Dock = DockStyle.Top;
        _fileHeader.Height = 30;
        _fileHeader.Font = new Font(Font, FontStyle.Bold);
        _fileHeader.TextAlign = ContentAlignment.MiddleLeft;
        _fileHeader.Padding = new Padding(4, 0, 4, 0);

        _fileList.Dock = DockStyle.Fill;
        _fileList.SelectionMode = SelectionMode.One;
        _fileList.AllowDrop = true;
        _fileList.DragEnter += OnDragEnter;
        _fileList.DragDrop += OnDragDrop;

        _dropHint.Dock = DockStyle.Fill;
        _dropHint.TextAlign = ContentAlignment.MiddleCenter;
        _dropHint.ForeColor = SystemColors.GrayText;
        _dropHint.BackColor = Color.Transparent;
        _dropHint.Visible = false;

        var fileButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(2),
        };
        _addFilesButton.Text = Localizer.T("Main.AddFiles");
        _addFilesButton.Width = 100;
        _addFilesButton.Click += (_, _) => PickFiles();
        _addFolderButton.Text = Localizer.T("Main.AddFolder");
        _addFolderButton.Width = 100;
        _addFolderButton.Click += (_, _) => PickFolder();
        _removeButton.Text = Localizer.T("Main.Remove");
        _removeButton.Width = 80;
        _removeButton.Click += (_, _) => RemoveSelected();
        _clearButton.Text = Localizer.T("Main.Clear");
        _clearButton.Width = 80;
        _clearButton.Click += (_, _) => ClearFiles();
        fileButtons.Controls.AddRange(new Control[]
        {
            _addFilesButton, _addFolderButton, _removeButton, _clearButton,
        });

        var filePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        filePanel.Controls.Add(_fileList);
        filePanel.Controls.Add(_dropHint);
        filePanel.Controls.Add(fileButtons);
        filePanel.Controls.Add(_fileHeader);

        // ---- split container ----
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 320,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(devicePanel);
        split.Panel2.Controls.Add(filePanel);

        // ---- bottom bar ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8, 6, 8, 6) };
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = SystemColors.GrayText;

        _settingsButton.Text = Localizer.T("Main.Settings");
        _settingsButton.Width = 96;
        _settingsButton.Height = 32;
        _settingsButton.Dock = DockStyle.Right;
        _settingsButton.Click += (_, _) => _coordinator.ShowSettings(this);

        _sendButton.Text = Localizer.T("Main.Send");
        _sendButton.Width = 110;
        _sendButton.Height = 32;
        _sendButton.Dock = DockStyle.Right;
        _sendButton.Font = new Font(Font, FontStyle.Bold);
        _sendButton.Click += (_, _) => SendSelected();

        bottom.Controls.Add(_status);
        bottom.Controls.Add(_settingsButton);
        bottom.Controls.Add(_sendButton);

        Controls.Add(split);
        Controls.Add(bottom);
    }

    public void ApplyLanguage()
    {
        Text = Localizer.T("Main.Title");
        _deviceHeader.Text = Localizer.T("Main.Devices");
        _fileHeader.Text = Localizer.T("Main.Files");
        _refreshButton.Text = Localizer.T("Main.Refresh");
        _addFilesButton.Text = Localizer.T("Main.AddFiles");
        _addFolderButton.Text = Localizer.T("Main.AddFolder");
        _removeButton.Text = Localizer.T("Main.Remove");
        _clearButton.Text = Localizer.T("Main.Clear");
        _sendButton.Text = Localizer.T("Main.Send");
        _settingsButton.Text = Localizer.T("Main.Settings");
        _dropHint.Text = Localizer.T("Main.DropHint");
        _deviceList.Columns[0].Text = "Name";
        _deviceList.Columns[1].Text = "Address";
        _deviceList.Columns[2].Text = "Type";
        UpdateStatus();
    }

    // ---- devices -------------------------------------------------------------

    private void ReloadDevices()
    {
        _deviceList.BeginUpdate();
        _deviceList.Items.Clear();
        foreach (var device in _host.Devices) AddDeviceItem(device);
        _deviceList.EndUpdate();
        UpdateStatus();
    }

    private void OnDeviceAdded(DiscoveredDevice device)
    {
        if (IsDisposed) return;
        if (_deviceList.Items.ContainsKey(device.Id)) return;
        AddDeviceItem(device);
        UpdateStatus();
    }

    private void OnDeviceRemoved(DiscoveredDevice device)
    {
        if (IsDisposed) return;
        if (_deviceList.Items.ContainsKey(device.Id))
            _deviceList.Items.RemoveByKey(device.Id);
        UpdateStatus();
    }

    private void AddDeviceItem(DiscoveredDevice device)
    {
        var item = new ListViewItem(new[]
        {
            device.DisplayName,
            device.Address.ToString(),
            device.Info.DeviceType,
        })
        {
            Name = device.Id,
            Tag = device,
        };
        _deviceList.Items.Add(item);
    }

    private DiscoveredDevice? SelectedDevice => _deviceList.SelectedItems.Count > 0
        ? _deviceList.SelectedItems[0].Tag as DiscoveredDevice
        : null;

    // ---- files ---------------------------------------------------------------

    /// <summary>Adds paths coming from the command line / Explorer context menu.</summary>
    public void QueueFiles(IEnumerable<string> paths)
    {
        AddPaths(paths, expandDirectories: true);
        // Bring the window forward for shell-initiated sends.
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void PickFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = Localizer.T("Main.AddFiles"),
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames, false);
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = Localizer.T("Main.AddFolder") };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dialog.SelectedPath }, true);
    }

    private void AddPaths(IEnumerable<string> paths, bool expandDirectories)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (Directory.Exists(path))
            {
                if (!expandDirectories) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        if (AddFile(file)) added++;
                }
                catch (Exception ex)
                {
                    Core.Logger.Warn($"Failed to enumerate {path}: {ex.Message}");
                }
            }
            else if (File.Exists(path))
            {
                if (AddFile(path)) added++;
            }
        }

        if (added > 0) UpdateStatus();
        UpdateDropHint();
    }

    private bool AddFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!_fileSet.Add(full)) return false;
        _files.Add(full);
        _fileList.Items.Add($"{Path.GetFileName(full)}   ({UiHelpers.FormatSize(new FileInfo(full).Length)})");
        return true;
    }

    private void RemoveSelected()
    {
        var index = _fileList.SelectedIndex;
        if (index < 0) return;
        _fileSet.Remove(_files[index]);
        _files.RemoveAt(index);
        _fileList.Items.RemoveAt(index);
        UpdateStatus();
        UpdateDropHint();
    }

    private void ClearFiles()
    {
        _files.Clear();
        _fileSet.Clear();
        _fileList.Items.Clear();
        UpdateStatus();
        UpdateDropHint();
    }

    private void UpdateDropHint() => _dropHint.Visible = _fileList.Items.Count == 0;

    // ---- drag & drop ---------------------------------------------------------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths, expandDirectories: true);
    }

    // ---- send ----------------------------------------------------------------

    private void SendSelected()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            MessageBox.Show(this, Localizer.T("Main.SelectDevice"), Localizer.T("App.Name"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_files.Count == 0)
        {
            MessageBox.Show(this, Localizer.T("Main.NoFiles"), Localizer.T("App.Name"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _coordinator.RequestSend(device, _files.ToArray());
        _status.Text = Localizer.T("Main.Sending");
    }

    private void UpdateStatus()
    {
        _status.Text = string.Format(Localizer.T("Main.DevicesFound"), _host.Devices.Count) +
                       (_files.Count > 0 ? $"   |   {_files.Count} file(s)" : string.Empty);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_host.IsExiting && _host.Settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _host.Notifier.Info(Localizer.T("App.Name"), Localizer.T("Tray.Tooltip"));
            return;
        }
        base.OnFormClosing(e);
    }
}
