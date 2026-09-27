using System.IO.Pipes;
using System.Windows.Forms;
using LocalSendWinForms.Core;
using LocalSendWinForms.Services;
using LocalSendWinForms.UI;

namespace LocalSendWinForms;

internal static class Program
{
    private const string MutexName = @"Local\LocalSend-WinForms.SingleInstance";
    private const string PipeName = "LocalSend-WinForms.Pipe";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("Unhandled exception", (Exception)e.ExceptionObject);
        Application.ThreadException += (_, e) =>
        {
            Logger.Error("UI thread exception", e.Exception);
            MessageBox.Show(e.Exception.Message, Localizer.T("Msg.Error"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        var startMinimized = HasFlag(args, "--startup") || HasFlag(args, "--minimized");
        var pendingFiles = ExtractFiles(args);

        // Single-instance guard: a second launch forwards its file list to the
        // running instance (this is how multi-select from Explorer converges into
        // one send window) and then exits.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ForwardToRunningInstance(pendingFiles);
            return;
        }

        using var context = new TrayApplicationContext(startMinimized, pendingFiles);
        Application.Run(context);
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Collects file/folder paths from the command line (used by the shell verb).</summary>
    private static List<string> ExtractFiles(string[] args)
    {
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("--", StringComparison.Ordinal)) continue;
            if (File.Exists(arg) || Directory.Exists(arg)) files.Add(arg);
        }
        return files;
    }

    private static void ForwardToRunningInstance(IReadOnlyList<string> files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(string.Join('\n', files));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not reach the running instance: {ex.Message}");
        }
    }

    /// <summary>
    /// The WinForms application context: owns the host, the dialog coordinator,
    /// the main window and the pipe server that receives forwarded file lists.
    /// </summary>
    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly AppHost _host;
        private readonly TransferCoordinator _coordinator;
        private readonly MainForm _mainForm;
        private readonly CancellationTokenSource _pipeCts = new();

        public TrayApplicationContext(bool startMinimized, IReadOnlyList<string> pendingFiles)
        {
            _host = new AppHost();
            _coordinator = new TransferCoordinator(_host);
            _mainForm = new MainForm(_host, _coordinator);
            _coordinator.AttachMainForm(_mainForm);

            // From here on every event raised by a background thread (incoming transfer
            // requests, progress, completions) is marshalled onto the thread that owns the
            // main window, so dialogs are always created and pumped by the UI thread.
            // Without this the confirm/progress windows would be shown by a worker thread
            // and would freeze on arrival.
            _host.BindUi(new ControlSynchronizationContext(_mainForm));

            var tray = _host.Tray;
            tray.OpenRequested += () => _coordinator.ShowMain();
            tray.QuickSendRequested += () => _coordinator.QuickSend();
            tray.SettingsRequested += () => _coordinator.ShowSettings(null);
            tray.OpenFolderRequested += () => _coordinator.OpenTransferFolder();
            tray.AboutRequested += () => _coordinator.ShowAbout(null);
            tray.ExitRequested += ExitApplication;

            _host.Start();
            StartPipeServer();

            if (pendingFiles.Count > 0)
                _coordinator.QueueFilesOnMain(pendingFiles);
            else if (!startMinimized)
                _coordinator.ShowMain();
        }

        private void StartPipeServer()
        {
            _ = Task.Run(async () =>
            {
                var token = _pipeCts.Token;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                        using var reader = new StreamReader(server);
                        var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                        var files = text
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToList();

                        _host.Ui.Post(_ =>
                        {
                            if (files.Count > 0) _coordinator.QueueFilesOnMain(files);
                            else _coordinator.ShowMain();
                        }, null);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Logger.Warn($"Pipe server error: {ex.Message}"); }
                }
            });
        }

        private void ExitApplication()
        {
            _pipeCts.Cancel();
            _host.IsExiting = true;
            _host.Dispose();
            _coordinator.Dispose();
            _mainForm.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pipeCts.Cancel();
                _pipeCts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
