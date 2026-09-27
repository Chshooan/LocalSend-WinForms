using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using LocalSendWinForms.Core;

namespace LocalSendWinForms.Services;

/// <summary>
/// Composition root and transfer hub. Owns every long-lived service, applies
/// settings, and coordinates concurrent send operations plus incoming receive
/// sessions. UI dialogs subscribe to the events below and always receive them
/// on the UI thread.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly ConcurrentDictionary<string, SendOperation> _sends = new();
    private HttpReceiver _receiver = null!;
    private bool _disposed;

    public AppHost()
    {
        Current = this;
        Ui = SynchronizationContext.Current ?? new SynchronizationContext();

        Settings = SettingsStore.Load();
        Localizer.SetLanguage(Settings.Language);

        Certificate = CertificateManager.GetOrCreate(SettingsStore.CertificatePath);
        Fingerprint = CertificateManager.GetFingerprint(Certificate);

        Notifier = new NotificationService { Ui = Ui };
        Tray = new TrayService();
        Notifier.TrayIcon = Tray.NotifyIcon;

        _receiver = new HttpReceiver(Certificate, Settings.UseHttps, Settings.Port);
        WireReceiver(_receiver);
        CreateDiscovery();
    }

    public static AppHost Current { get; private set; } = null!;

    // ---- public state --------------------------------------------------------
    public AppSettings Settings { get; private set; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public SynchronizationContext Ui { get; private set; }
    public NotificationService Notifier { get; }

    /// <summary>
    /// Re-points UI marshaling at a context that is valid for the running application.
    /// Call this once the main window exists; before that, <see cref="Ui"/> is only a
    /// placeholder and must not be used to create or update controls.
    /// </summary>
    public void BindUi(SynchronizationContext context)
    {
        if (context is null) return;
        Ui = context;
        Notifier.Ui = Ui;
    }
    public TrayService Tray { get; }
    public HttpReceiver Receiver => _receiver;
    public DeviceDiscovery Discovery { get; private set; } = null!;
    public bool IsExiting { get; set; }

    public IReadOnlyList<DiscoveredDevice> Devices => Discovery.Devices;

    // ---- UI-facing events (raised on the UI thread) --------------------------
    public event Action<DiscoveredDevice>? DeviceAdded;
    public event Action<DiscoveredDevice>? DeviceRemoved;
    public event Action<ReceiveRequestEventArgs>? ReceiveRequested;
    public event Action<ReceiveSessionStarted>? ReceiveSessionStarted;
    public event Action<TransferProgress>? ReceiveProgress;
    public event Action<ReceiveSessionInfo>? ReceiveCompleted;
    public event Action<string, TransferProgress>? SendProgress;
    public event Action<string, SendResult>? SendCompleted;
    public event Action? SettingsChanged;

    // ---- lifecycle -----------------------------------------------------------

    public void Start()
    {
        ConfigureReceiver(_receiver);

        try { _receiver.Start(); }
        catch (Exception ex) { Logger.Error("Failed to start receiver", ex); }

        Discovery.Start();
        Logger.Info($"AppHost started: {Settings.DeviceName} " +
                    $"[{(Settings.UseHttps ? "https" : "http")}:{Settings.Port}]");
    }

    private void ConfigureReceiver(HttpReceiver receiver)
    {
        receiver.LocalInfo = BuildDeviceInfo();
        receiver.DownloadDirectory = Settings.DownloadDirectory;
        receiver.Pin = string.IsNullOrWhiteSpace(Settings.Pin) ? null : Settings.Pin;
        receiver.AutoAccept = Settings.AutoAccept;
        receiver.ConflictMode = Settings.ConflictMode;
        receiver.VerifyChecksum = Settings.ComputeSha256;
    }

    private void WireReceiver(HttpReceiver receiver)
    {
        receiver.ReceiveRequested += OnReceiveRequested;
        receiver.SessionStarted += OnSessionStarted;
        receiver.ProgressChanged += OnReceiveProgress;
        receiver.SessionCompleted += OnSessionCompleted;
    }

    public DeviceInfo BuildDeviceInfo() => new()
    {
        Alias = string.IsNullOrWhiteSpace(Settings.DeviceName) ? Environment.MachineName : Settings.DeviceName,
        Version = LocalSendProtocol.Version,
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = Fingerprint,
        Port = Settings.Port,
        Protocol = Settings.UseHttps ? "https" : "http",
        Download = false,
        Announcement = true,
    };

    private void CreateDiscovery()
    {
        Discovery = new DeviceDiscovery(BuildDeviceInfo, Settings.UseHttps, Settings.Port);
        Discovery.DeviceAdded += d => Post(() => DeviceAdded?.Invoke(d));
        Discovery.DeviceRemoved += d => Post(() => DeviceRemoved?.Invoke(d));
    }

    /// <summary>Applies new settings, restarting the network stack when required.</summary>
    public void ApplySettings(AppSettings updated)
    {
        var networkChanged = Settings.Port != updated.Port || Settings.UseHttps != updated.UseHttps;

        Settings = updated;
        SettingsStore.Save(updated);
        Localizer.SetLanguage(updated.Language);

        if (networkChanged)
        {
            RestartNetworking();
        }
        else
        {
            ConfigureReceiver(_receiver);
        }

        ShellIntegration.SetStartup(updated.LaunchAtStartup, ShellIntegration.ExecutablePath);
        Tray.ApplyLanguage();
        SettingsChanged?.Invoke();
        Logger.Info("Settings applied");
    }

    private void RestartNetworking()
    {
        try
        {
            _receiver.Dispose();
            Discovery.Dispose();

            var fresh = new HttpReceiver(Certificate, Settings.UseHttps, Settings.Port);
            WireReceiver(fresh);
            _receiver = fresh;

            ConfigureReceiver(_receiver);
            CreateDiscovery();

            _receiver.Start();
            Discovery.Start();
            Logger.Info($"Network stack restarted on port {Settings.Port}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to restart networking", ex);
        }
    }

    public Task RefreshDevicesAsync() => Discovery.ScanNowAsync();

    // ---- sending -------------------------------------------------------------

    /// <summary>
    /// Starts sending <paramref name="files"/> to <paramref name="device"/> and
    /// returns an operation id used to correlate progress events. Multiple sends
    /// may run concurrently.
    /// </summary>
    public string StartSend(DiscoveredDevice device, IReadOnlyList<string> files, string? pin)
    {
        var id = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p => SendProgress?.Invoke(id, p));
        var client = new TransferClient(BuildDeviceInfo) { ComputeSha256 = Settings.ComputeSha256 };

        var operation = new SendOperation(cts);
        _sends[id] = operation;

        _ = Task.Run(async () =>
        {
            var result = new SendResult { Total = files.Count };
            try
            {
                result = await client.SendAsync(device, files, pin, progress, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Errors.Add("cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error("Send operation failed", ex);
                result.Errors.Add(ex.Message);
            }
            finally
            {
                _sends.TryRemove(id, out _);

                // Deliberately NOT disposing the CancellationTokenSource here. The send
                // loop keeps using `cts.Token` for retry backoffs (Task.Delay registers a
                // callback with the source), so disposing it while a retry may still be
                // scheduled throws "ObjectDisposedException: The CancellationTokenSource
                // has been disposed" — which is exactly what tore down a transfer after a
                // cancel. Left alive until AppHost.Dispose(), which runs after every send
                // has finished.
                var captured = result;
                Post(() => SendCompleted?.Invoke(id, captured));
            }
        });

        return id;
    }

    public void CancelSend(string id)
    {
        if (_sends.TryGetValue(id, out var operation)) operation.Cancel();
    }

    // ---- receive event forwarding -------------------------------------------

    private void OnReceiveRequested(ReceiveRequestEventArgs args)
    {
        if (Settings.NotifyOnReceive)
        {
            Notifier.Info(
                Localizer.T("Notif.ReceiveTitle"),
                Localizer.T("Notif.ReceiveBody", args.Sender.Alias, args.Files.Count));
        }
        Post(() => ReceiveRequested?.Invoke(args));
    }

    private void OnSessionStarted(ReceiveSessionStarted info) =>
        Post(() => ReceiveSessionStarted?.Invoke(info));

    private void OnReceiveProgress(TransferProgress progress) =>
        Post(() => ReceiveProgress?.Invoke(progress));

    private void OnSessionCompleted(ReceiveSessionInfo info)
    {
        if (Settings.NotifyOnComplete)
        {
            if (info.AnyFailed)
                Notifier.Warn(Localizer.T("Notif.FailedTitle"),
                    Localizer.T("Notif.FailedBody", info.FileCount, info.SenderName));
            else
                Notifier.Info(Localizer.T("Notif.CompleteTitle"),
                    Localizer.T("Notif.CompleteBody", info.FileCount, info.Directory));
        }
        Post(() => ReceiveCompleted?.Invoke(info));
    }

    private void Post(Action action)
    {
        var context = Ui;
        if (context is null) { action(); return; }
        context.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsExiting = true;

        // Cancel every send still in flight, then release their token sources. Doing it
        // here (rather than when a send finishes) is what keeps a late cancel from
        // disposing a token source that a retry backoff is still holding on to.
        foreach (var operation in _sends.Values)
        {
            try { operation.Cancel(); operation.Dispose(); } catch { }
        }
        _sends.Clear();

        _receiver.Dispose();
        Discovery.Dispose();
        Tray.Dispose();
        Certificate.Dispose();
    }

    private sealed class SendOperation : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public SendOperation(CancellationTokenSource cts) => _cts = cts;
        public void Cancel() { try { _cts.Cancel(); } catch { } }
        public void Dispose() { try { _cts.Dispose(); } catch { } }
    }
}
