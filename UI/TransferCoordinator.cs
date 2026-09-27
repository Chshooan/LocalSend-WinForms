using System.Windows.Forms;
using LocalSendWinForms.Core;
using LocalSendWinForms.Services;

namespace LocalSendWinForms.UI;

/// <summary>
/// Owns the dialog lifecycle. Translates high-level <see cref="AppHost"/> events
/// into concrete windows (receive confirmation, progress) and routes progress
/// samples to the right window so concurrent transfers never cross-talk.
/// </summary>
public sealed class TransferCoordinator : IDisposable
{
    private readonly AppHost _host;
    private readonly Dictionary<string, (ProgressForm Form, string Device)> _sendForms = new();
    private readonly Dictionary<string, ProgressForm> _receiveForms = new();
    private MainForm? _mainForm;

    public TransferCoordinator(AppHost host)
    {
        _host = host;
        _host.ReceiveRequested += OnReceiveRequested;
        _host.ReceiveSessionStarted += OnReceiveSessionStarted;
        _host.ReceiveProgress += OnReceiveProgress;
        _host.ReceiveCompleted += OnReceiveCompleted;
        _host.SendProgress += OnSendProgress;
        _host.SendCompleted += OnSendCompleted;
    }

    public void AttachMainForm(MainForm form) => _mainForm = form;

    // ---- window helpers ------------------------------------------------------

    public void ShowMain()
    {
        if (_mainForm is null) return;
        if (!_mainForm.Visible) _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized) _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    public void QueueFilesOnMain(IEnumerable<string> files) => _mainForm?.QueueFiles(files);

    /// <summary>Tray "quick send": pick files, then open the main window to choose a target.</summary>
    public void QuickSend()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = Localizer.T("Main.AddFiles"),
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            QueueFilesOnMain(dialog.FileNames);
    }

    public void ShowSettings(IWin32Window? owner)
    {
        using var form = new SettingsForm(_host);
        if (owner is not null) form.ShowDialog(owner);
        else form.ShowDialog();
    }

    public void ShowAbout(IWin32Window? owner)
    {
        using var form = new AboutForm();
        if (owner is not null) form.ShowDialog(owner);
        else form.ShowDialog();
    }

    public void OpenTransferFolder() => ShellIntegration.OpenInExplorer(_host.Settings.DownloadDirectory);

    // ---- sending -------------------------------------------------------------

    /// <summary>Starts a transfer to <paramref name="device"/> and shows its progress window.</summary>
    public void RequestSend(DiscoveredDevice device, IReadOnlyList<string> files)
    {
        var rows = new List<(string Name, long Size)>(files.Count);
        foreach (var path in files)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { /* shown as 0 */ }
            rows.Add((Path.GetFileName(path), size));
        }

        string? operationId = null;
        var form = new ProgressForm(
            ProgressForm.Direction.Send,
            device.DisplayName,
            rows,
            onCancel: () => { if (operationId is not null) _host.CancelSend(operationId); });

        operationId = _host.StartSend(device, files, _host.Settings.Pin);
        _sendForms[operationId] = (form, device.DisplayName);
        form.Show();
    }

    private void OnSendProgress(string operationId, TransferProgress progress)
    {
        if (_sendForms.TryGetValue(operationId, out var entry))
            entry.Form.Report(progress);
    }

    private void OnSendCompleted(string operationId, SendResult result)
    {
        if (!_sendForms.TryGetValue(operationId, out var entry)) return;
        _sendForms.Remove(operationId);

        var cancelled = result.Errors.Count == 1 &&
                        string.Equals(result.Errors[0], "cancelled", StringComparison.Ordinal);

        if (cancelled)
            entry.Form.SetCompleted(false, Localizer.T("Progress.Cancelled"));
        else if (result.Success)
            entry.Form.SetCompleted(true);
        else
        {
            // Say why. A rejected TLS handshake means the request never left this machine,
            // so a generic message reads as though the peer ignored us.
            var tls = result.Errors.Any(e => e.Contains("TLS", StringComparison.OrdinalIgnoreCase));
            entry.Form.SetCompleted(false, Localizer.T(tls ? "Progress.HandshakeFailed" : "Progress.Failed"));
        }

        if (!_host.Settings.NotifyOnComplete || cancelled) return;

        if (result.Success)
            _host.Notifier.Info(Localizer.T("Notif.SentTitle"),
                Localizer.T("Notif.SentBody", result.Sent, entry.Device));
        else
            _host.Notifier.Warn(Localizer.T("Notif.FailedTitle"),
                Localizer.T("Notif.FailedBody", result.Total - result.Sent, entry.Device));
    }

    // ---- receiving -----------------------------------------------------------

    private void OnReceiveRequested(ReceiveRequestEventArgs args)
    {
        var form = new ReceiveConfirmForm(args);
        form.Show();
    }

    private void OnReceiveSessionStarted(ReceiveSessionStarted info)
    {
        var rows = info.Files.Select(f => (f.FileName, f.Size)).ToList();
        var sessionId = info.SessionId;
        var form = new ProgressForm(
            ProgressForm.Direction.Receive,
            info.SenderName,
            rows,
            onCancel: () => _host.Receiver.CancelSession(sessionId));

        _receiveForms[sessionId] = form;
        form.Show();
    }

    private void OnReceiveProgress(TransferProgress progress)
    {
        if (_receiveForms.TryGetValue(progress.SessionId, out var form))
            form.Report(progress);
    }

    private void OnReceiveCompleted(ReceiveSessionInfo info)
    {
        if (_receiveForms.TryGetValue(info.SessionId, out var form))
        {
            _receiveForms.Remove(info.SessionId);
            form.SetCompleted(!info.AnyFailed,
                info.AnyFailed ? Localizer.T("Progress.Failed") : Localizer.T("Progress.Done"));
        }
    }

    public void Dispose()
    {
        _host.ReceiveRequested -= OnReceiveRequested;
        _host.ReceiveSessionStarted -= OnReceiveSessionStarted;
        _host.ReceiveProgress -= OnReceiveProgress;
        _host.ReceiveCompleted -= OnReceiveCompleted;
        _host.SendProgress -= OnSendProgress;
        _host.SendCompleted -= OnSendCompleted;
    }
}
