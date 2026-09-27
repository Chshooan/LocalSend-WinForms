using System.Drawing;
using System.Windows.Forms;
using LocalSendWinForms.Core;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>
/// A standalone progress window for one transfer (one device, N files). A new
/// instance is created per concurrent send/receive so multiple transfers can be
/// monitored independently.
/// </summary>
public sealed class ProgressForm : Form
{
    public enum Direction { Send, Receive }

    private sealed class Row
    {
        public required string FileName { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Label State { get; init; }
        public long Bytes { get; set; }
        public bool Assigned { get; set; }
    }

    private readonly Direction _direction;
    private readonly string _deviceName;
    private readonly Action? _onCancel;
    private readonly List<Row> _rowList = new();
    private readonly Dictionary<string, Row> _byFileId = new(StringComparer.Ordinal);

    private readonly Label _title = new();
    private readonly ProgressBar _overall = new();
    private readonly Label _overallText = new();
    private readonly Label _speed = new();
    private readonly Button _cancel = new();
    private readonly Button _close = new();
    private readonly TableLayoutPanel _fileTable = new();
    private readonly System.Windows.Forms.Timer _speedTimer = new();

    private long _totalBytes;
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;
    private bool _finished;

    public ProgressForm(Direction direction, string deviceName, IReadOnlyList<(string Name, long Size)> files, Action? onCancel)
    {
        _direction = direction;
        _deviceName = deviceName;
        _onCancel = onCancel;

        BuildUi();
        AddFiles(files);

        _speedTimer.Interval = 1000;
        _speedTimer.Tick += OnSpeedTick;
        _speedTimer.Start();
    }

    private void BuildUi()
    {
        Text = $"{Localizer.T("Progress.Title")} — {_deviceName}";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 430);
        MinimumSize = new Size(480, 320);
        Font = SystemFonts.MessageBoxFont;
        Padding = new Padding(12);

        _title.Dock = DockStyle.Top;
        _title.Height = 30;
        _title.Font = new Font(Font, FontStyle.Bold);
        _title.Text = _direction == Direction.Send
            ? Localizer.T("Progress.To", _deviceName)
            : Localizer.T("Progress.From", _deviceName);

        var overallPanel = new Panel { Dock = DockStyle.Top, Height = 58 };
        _overallText.Dock = DockStyle.Top;
        _overallText.Height = 20;
        _overallText.Text = "0%";
        _overall.Dock = DockStyle.Top;
        _overall.Height = 18;
        _overall.Maximum = 100;
        _speed.Dock = DockStyle.Top;
        _speed.Height = 16;
        _speed.ForeColor = SystemColors.GrayText;
        overallPanel.Controls.Add(_overall);
        overallPanel.Controls.Add(_overallText);
        overallPanel.Controls.Add(_speed);
        _overallText.BringToFront();
        _overall.BringToFront();
        _speed.BringToFront();

        _fileTable.Dock = DockStyle.Fill;
        _fileTable.ColumnCount = 3;
        _fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        _fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        _fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        _fileTable.AutoScroll = true;
        _fileTable.GrowStyle = TableLayoutPanelGrowStyle.AddRows;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0),
        };
        _cancel.Text = Localizer.T("Progress.Cancel");
        _cancel.Width = 96;
        _cancel.Height = 30;
        _cancel.Click += (_, _) => OnCancelClicked();
        _close.Text = Localizer.T("Progress.Close");
        _close.Width = 96;
        _close.Height = 30;
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_close);

        Controls.Add(_fileTable);
        Controls.Add(overallPanel);
        Controls.Add(buttons);
        Controls.Add(_title);
    }

    private void AddFiles(IReadOnlyList<(string Name, long Size)> files)
    {
        foreach (var (name, size) in files)
        {
            var nameLabel = new Label
            {
                Text = name,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(3, 6, 3, 3),
            };
            var bar = new ProgressBar { Dock = DockStyle.Fill, Maximum = 100, Margin = new Padding(3, 8, 3, 3) };
            var state = new Label
            {
                Text = "0%",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(3, 6, 3, 3),
            };

            var rowIndex = _fileTable.RowCount;
            _fileTable.RowCount = rowIndex + 1;
            _fileTable.Controls.Add(nameLabel, 0, rowIndex);
            _fileTable.Controls.Add(bar, 1, rowIndex);
            _fileTable.Controls.Add(state, 2, rowIndex);

            _rowList.Add(new Row { FileName = name, Bar = bar, State = state });
            _totalBytes += size;
        }
    }

    /// <summary>Feeds a progress sample for one file. Safe to call from any thread.</summary>
    public void Report(TransferProgress progress)
    {
        if (_finished || IsDisposed) return;

        if (InvokeRequired)
        {
            BeginInvoke(new Action<TransferProgress>(Report), progress);
            return;
        }

        var row = ResolveRow(progress);
        if (row is null) return;

        if (progress.Bytes > row.Bytes) row.Bytes = progress.Bytes;

        var percent = progress.Total > 0
            ? (int)Math.Clamp(row.Bytes * 100L / progress.Total, 0, 100)
            : 0;
        row.Bar.Value = percent;
        row.State.Text = percent >= 100 ? "100%" : percent + "%";

        UpdateOverall();
    }

    /// <summary>
    /// Maps a progress sample to a UI row. Sender-generated file ids differ from
    /// the ids we display, so match by id first, then by file name, then fall back
    /// to the next unassigned row.
    /// </summary>
    private Row? ResolveRow(TransferProgress progress)
    {
        if (!string.IsNullOrEmpty(progress.FileId) && _byFileId.TryGetValue(progress.FileId, out var known))
            return known;

        var row = _rowList.FirstOrDefault(r => !r.Assigned &&
                        string.Equals(r.FileName, progress.FileName, StringComparison.OrdinalIgnoreCase))
                  ?? _rowList.FirstOrDefault(r => !r.Assigned);

        if (row is null) return null;

        row.Assigned = true;
        if (!string.IsNullOrEmpty(progress.FileId)) _byFileId[progress.FileId] = row;
        return row;
    }

    private void UpdateOverall()
    {
        var done = _rowList.Sum(r => r.Bytes);
        var percent = _totalBytes > 0 ? (int)Math.Clamp(done * 100L / _totalBytes, 0, 100) : 0;
        _overall.Value = percent;
        _overallText.Text = $"{Localizer.T("Progress.Overall")}: {percent}% " +
                            $"({UiHelpers.FormatSize(done)} / {UiHelpers.FormatSize(_totalBytes)})";
    }

    private void OnSpeedTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var done = _rowList.Sum(r => r.Bytes);
        var elapsed = (now - _lastSample).TotalSeconds;
        if (elapsed > 0)
        {
            var rate = (done - _lastBytes) / elapsed;
            _speed.Text = Localizer.T("Progress.Speed", UiHelpers.FormatSpeed(rate));
        }
        _lastBytes = done;
        _lastSample = now;
    }

    private void OnCancelClicked()
    {
        if (_finished) return;
        try { _onCancel?.Invoke(); }
        catch (Exception ex) { Core.Logger.Warn($"Cancel action failed: {ex.Message}"); }
        SetCompleted(false, Localizer.T("Progress.Cancelled"));
    }

    /// <summary>Marks the transfer finished and reflects the outcome in the UI.</summary>
    public void SetCompleted(bool success, string? message = null)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetCompleted(success, message)));
            return;
        }
        if (_finished) return;

        _finished = true;
        _speedTimer.Stop();
        _cancel.Enabled = false;
        _close.Enabled = true;

        if (success)
        {
            foreach (var row in _rowList)
            {
                row.Bar.Value = 100;
                row.State.Text = "100%";
            }
            UpdateOverall();
        }

        _overallText.Text = message ?? (success
            ? Localizer.T("Progress.Done")
            : Localizer.T("Progress.Failed"));

        if (success)
        {
            var autoClose = new System.Windows.Forms.Timer { Interval = 3500 };
            autoClose.Tick += (_, _) =>
            {
                autoClose.Stop();
                autoClose.Dispose();
                Close();
            };
            autoClose.Start();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _speedTimer.Stop();
        _speedTimer.Dispose();
        base.OnFormClosed(e);
    }
}
