using System.Drawing;
using System.Windows.Forms;
using LocalSendWinForms.Core;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>
/// Independent confirmation popup shown when a peer offers us files. The
/// receiving HTTP handler waits on <see cref="ReceiveRequestEventArgs.Decision"/>,
/// so this window must always complete that task exactly once (accept, decline,
/// closing the window, or the countdown expiring all resolve it).
/// </summary>
public sealed class ReceiveConfirmForm : Form
{
    private readonly ReceiveRequestEventArgs _args;
    private readonly CheckedListBox _fileList = new();
    private readonly Label _countdownLabel = new();
    private readonly System.Windows.Forms.Timer _countdown = new();
    private int _remainingSeconds;
    private bool _resolved;

    public ReceiveConfirmForm(ReceiveRequestEventArgs args)
    {
        _args = args;
        BuildUi();

        _remainingSeconds = Math.Max(5, (int)args.Timeout.TotalSeconds);
        _countdown.Interval = 1000;
        _countdown.Tick += OnCountdownTick;
        _countdown.Start();
        UpdateCountdownLabel();
    }

    private void BuildUi()
    {
        Text = Localizer.T("Receive.Title");
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(520, 380);
        Font = SystemFonts.MessageBoxFont;
        Padding = new Padding(14);

        var header = new Label
        {
            Text = $"{_args.Sender.Alias} {Localizer.T("Receive.Prompt")}",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 44,
            Font = new Font(Font, FontStyle.Bold),
        };

        var fromLabel = new Label
        {
            Text = $"{Localizer.T("Receive.From")}: {_args.Sender.Alias} " +
                   $"({_args.Sender.DeviceModel}, {_args.Sender.Protocol}://…:{_args.Sender.Port})",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = SystemColors.GrayText,
        };

        _fileList.Dock = DockStyle.Fill;
        _fileList.CheckOnClick = true;
        _fileList.IntegralHeight = false;
        foreach (var file in _args.Files)
            _fileList.Items.Add($"{file.FileName}   ({UiHelpers.FormatSize(file.Size)})", true);

        _countdownLabel.Dock = DockStyle.Bottom;
        _countdownLabel.Height = 24;
        _countdownLabel.ForeColor = SystemColors.GrayText;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0),
        };

        var accept = new Button { Text = Localizer.T("Receive.Accept"), Width = 96, Height = 30 };
        accept.Click += (_, _) => Accept();
        var decline = new Button { Text = Localizer.T("Receive.Reject"), Width = 96, Height = 30 };
        decline.Click += (_, _) => Resolve(false);
        var selectAll = new Button { Text = Localizer.T("Receive.SelectAll"), Width = 90, Height = 30 };
        selectAll.Click += (_, _) => SetAllChecked(true);
        var selectNone = new Button { Text = Localizer.T("Receive.DeselectAll"), Width = 90, Height = 30 };
        selectNone.Click += (_, _) => SetAllChecked(false);

        buttons.Controls.Add(accept);
        buttons.Controls.Add(decline);
        buttons.Controls.Add(selectNone);
        buttons.Controls.Add(selectAll);

        Controls.Add(_fileList);
        Controls.Add(_countdownLabel);
        Controls.Add(buttons);
        Controls.Add(fromLabel);
        Controls.Add(header);

        AcceptButton = accept;
        CancelButton = decline;

        Shown += (_, _) =>
        {
            Activate();
            BringToFront();
        };
    }

    private void SetAllChecked(bool value)
    {
        for (var i = 0; i < _fileList.Items.Count; i++) _fileList.SetItemChecked(i, value);
    }

    private void Accept()
    {
        var ids = new HashSet<string>();
        for (var i = 0; i < _fileList.Items.Count; i++)
        {
            if (_fileList.GetItemChecked(i) && i < _args.Files.Count)
                ids.Add(_args.Files[i].Id);
        }

        if (ids.Count == 0)
        {
            MessageBox.Show(this, Localizer.T("Msg.NothingReceived"), Localizer.T("App.Name"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Resolve(true, ids);
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _remainingSeconds--;
        UpdateCountdownLabel();
        if (_remainingSeconds <= 0) Resolve(false);
    }

    private void UpdateCountdownLabel() =>
        _countdownLabel.Text = Localizer.T("Receive.AutoClose", Math.Max(0, _remainingSeconds));

    private void Resolve(bool accepted, HashSet<string>? fileIds = null)
    {
        if (_resolved) return;
        _resolved = true;

        _countdown.Stop();
        _args.Decision.TrySetResult(new ReceiveDecision
        {
            Accepted = accepted,
            FileIds = fileIds ?? new HashSet<string>(),
            Reason = accepted ? null : "Declined",
        });

        DialogResult = accepted ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Closing the window without choosing is treated as a decline.
        if (!_resolved)
        {
            _resolved = true;
            _countdown.Stop();
            _args.Decision.TrySetResult(new ReceiveDecision { Accepted = false, Reason = "Closed" });
        }
        _countdown.Dispose();
        base.OnFormClosed(e);
    }
}
