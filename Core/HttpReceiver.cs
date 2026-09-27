using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Threading.Timer;

namespace LocalSendWinForms.Core;

/// <summary>Raised when a peer wants to send us files and a decision is required.</summary>
public sealed class ReceiveRequestEventArgs : EventArgs
{
    public required DeviceInfo Sender { get; init; }
    public required IReadOnlyList<FileMetadata> Files { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Completed by the UI once the user accepts or rejects (or on timeout).</summary>
    public TaskCompletionSource<ReceiveDecision> Decision { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>User's answer to a receive request.</summary>
public sealed class ReceiveDecision
{
    public bool Accepted { get; set; }
    public HashSet<string> FileIds { get; set; } = new();
    public string? Reason { get; set; }
}

/// <summary>Summary emitted when a receive session finishes, used for notifications.</summary>
public sealed class ReceiveSessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public string Directory { get; set; } = string.Empty;
    public bool AnyFailed { get; set; }
}

/// <summary>Emitted when a session is opened, so the UI can create a progress window.</summary>
public sealed class ReceiveSessionStarted
{
    public string SessionId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public IReadOnlyList<FileMetadata> Files { get; set; } = Array.Empty<FileMetadata>();
}

internal sealed class ReceiveFile
{
    public string FileId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Token { get; init; } = Guid.NewGuid().ToString("N");
    public long Size { get; init; }
    public string? Sha256 { get; init; }
    public long Received { get; set; }
    public bool Accepted { get; init; }
    public bool Done { get; set; }
    public bool Failed { get; set; }
}

internal sealed class ReceiveSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IPAddress Sender { get; init; } = IPAddress.None;
    public string SenderName { get; init; } = string.Empty;
    public ConcurrentDictionary<string, ReceiveFile> Files { get; } = new();
    public CancellationTokenSource Cts { get; } = new();
    public bool Cancelled { get; set; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
}

/// <summary>
/// A tiny, dependency-free HTTP/1.1 server (optionally wrapped in TLS) that
/// implements the LocalSend receiver endpoints. Using a raw TcpListener +
/// SslStream means no "netsh http add sslcert" / admin rights are needed.
/// </summary>
public sealed class HttpReceiver : IDisposable
{
    private const int HeaderLimit = 64 * 1024;
    private const int TransferBufferSize = 256 * 1024;
    /// <summary>Safety cap for a chunked upload body; a normal transfer stays far below this.</summary>
    private const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private readonly X509Certificate2 _certificate;
    private readonly bool _useHttps;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, ReceiveSession> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Timer? _cleanupTimer;

    public HttpReceiver(X509Certificate2 certificate, bool useHttps, int port)
    {
        _certificate = certificate;
        _useHttps = useHttps;
        _port = port;
    }

    // ---- configuration (set by the host / settings service) --------------------
    public DeviceInfo LocalInfo { get; set; } = new();
    public string DownloadDirectory { get; set; } = string.Empty;
    public string? Pin { get; set; }
    public bool AutoAccept { get; set; }
    /// <summary>"rename" (default) or "overwrite".</summary>
    public string ConflictMode { get; set; } = "rename";
    public bool VerifyChecksum { get; set; } = true;

    // ---- events -----------------------------------------------------------------
    public event Action<ReceiveRequestEventArgs>? ReceiveRequested;
    public event Action<ReceiveSessionStarted>? SessionStarted;
    public event Action<TransferProgress>? ProgressChanged;
    public event Action<ReceiveSessionInfo>? SessionCompleted;

    public bool IsRunning => _listener is not null;

    /// <summary>Port this receiver is (or will be) bound to.</summary>
    public int Port => _port;

    public void Start()
    {
        if (_listener is not null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _cleanupTimer = new Timer(_ => PurgeStaleSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        Logger.Info($"Receiver listening on {(_useHttps ? "https" : "http")}://0.0.0.0:{_port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        _sessions.Clear();
    }

    // --------------------------------------------------------------------------- accept loop

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            SslStream? tls = null;
            try
            {
                Stream stream = client.GetStream();
                if (_useHttps)
                {
                    // Keep the inner stream open so the socket can still be shut down
                    // gracefully once the TLS layer is finished with it.
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                    tls = ssl;
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.None, // OS-negotiated
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        },
                        ct).ConfigureAwait(false);
                    stream = ssl;
                }

                try
                {
                    await ProcessRequestAsync(stream, client.Client.RemoteEndPoint as IPEndPoint, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // A request must never die without an answer. Aborting the socket here
                    // is exactly what the peer experiences as
                    // "Connection reset by peer" and it hides the real cause, so try to
                    // reply first and only then close.
                    Logger.Warn($"Request aborted: {ex.Message}");
                    try
                    {
                        await WriteErrorAsync(stream, 500, "Internal Error", ct).ConfigureAwait(false);
                    }
                    catch (Exception write)
                    {
                        Logger.Warn($"Could not write the error response: {write.Message}");
                    }
                }

                await CloseGracefullyAsync(client, tls, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { /* peer hung up — normal during cancel */ }
            catch (AuthenticationException ex) { Logger.Warn($"TLS handshake failed: {ex.Message}"); }
            catch (Exception ex) { Logger.Error("Unhandled receiver error", ex); }
        }
    }

    /// <summary>
    /// Closes the connection so the peer sees a normal end-of-stream instead of an
    /// RST. Two things cause an RST here: closing while the TLS layer was not given a
    /// chance to send its <c>close_notify</c>, and closing while the peer's data is
    /// still sitting unread in the receive buffer.
    /// </summary>
    private static async Task CloseGracefullyAsync(TcpClient client, SslStream? tls, CancellationToken ct)
    {
        try
        {
            if (tls is not null)
            {
                await tls.FlushAsync(ct).ConfigureAwait(false);
                tls.Close();
            }
            else
            {
                await client.GetStream().FlushAsync(ct).ConfigureAwait(false);
            }

            // Half-close: gives the FIN a chance to go out before the socket disappears.
            client.Client.Shutdown(SocketShutdown.Send);

            if (client.Client.Available > 0)
            {
                client.Client.ReceiveTimeout = 1000;
                var buffer = new byte[1024];
                while (client.Client.Receive(buffer) > 0) { }
            }
        }
        catch { /* best effort; the peer is gone either way */ }
    }

    // --------------------------------------------------------------------------- request dispatch

    private async Task ProcessRequestAsync(Stream stream, IPEndPoint? remote, CancellationToken ct)
    {
        var requestLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 3) { await WriteErrorAsync(stream, 400, "Bad Request", ct); return; }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var (path, query) = SplitTarget(target);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (line is null) return;
            if (line.Length == 0) break;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        // -1 marks a chunked body: the length is only known once it has been parsed.
        // Replying before the body has been consumed leaves bytes unread in the socket
        // buffer, and closing a socket that way makes the peer see ECONNRESET instead
        // of a clean response.
        var chunked = headers.TryGetValue("Transfer-Encoding", out var te) &&
                      te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        long contentLength = 0;
        if (!chunked && headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var parsed))
            contentLength = parsed;

        // Honour "Expect: 100-continue" so large uploads from other clients proceed.
        if (headers.TryGetValue("Expect", out var expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n", ct).ConfigureAwait(false);
        }

        // Logged before the body is parsed so a rejected payload is still traceable.
        Logger.Info($"{method} {path} <- {remote?.Address} " +
                    $"({contentLength} bytes{(chunked ? ", chunked" : string.Empty)})");

        switch (path)
        {
            case LocalSendProtocol.InfoPath:
            case LocalSendProtocol.V1 + "/info":
                if (method != "GET") { await WriteErrorAsync(stream, 405, "Method Not Allowed", ct); return; }
                await WriteJsonAsync(stream, 200, "OK", LocalInfo, ct).ConfigureAwait(false);
                return;

            case LocalSendProtocol.RegisterPath:
            case LocalSendProtocol.V1 + "/register":
                if (method != "POST") { await WriteErrorAsync(stream, 405, "Method Not Allowed", ct); return; }
                await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, "OK", LocalInfo, ct).ConfigureAwait(false);
                return;

            case LocalSendProtocol.PrepareUploadPath:
                if (method != "POST") { await WriteErrorAsync(stream, 405, "Method Not Allowed", ct); return; }
                await HandlePrepareUploadAsync(stream, remote, query, contentLength, chunked, ct)
                    .ConfigureAwait(false);
                return;

            case LocalSendProtocol.UploadPath:
                if (method != "POST") { await WriteErrorAsync(stream, 405, "Method Not Allowed", ct); return; }
                await HandleUploadAsync(stream, remote, query, contentLength, chunked, ct).ConfigureAwait(false);
                return;

            case LocalSendProtocol.CancelPath:
                await DrainBodyAsync(stream, contentLength, chunked, ct).ConfigureAwait(false);
                await HandleCancelAsync(stream, query, ct).ConfigureAwait(false);
                return;

            default:
                await DrainBodyAsync(stream, contentLength, chunked, ct).ConfigureAwait(false);
                await WriteErrorAsync(stream, 404, "Not Found", ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Consumes the request body so the socket can be closed without an RST.
    /// </summary>
    private static async Task DrainBodyAsync(
        Stream stream, long contentLength, bool chunked, CancellationToken ct)
    {
        if (chunked) { await ReadChunkedAsync(stream, 16 * 1024 * 1024, ct).ConfigureAwait(false); return; }
        await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
    }

    // --------------------------------------------------------------------------- prepare-upload

    private async Task HandlePrepareUploadAsync(
        Stream stream, IPEndPoint? remote, string query, long contentLength, bool chunked, CancellationToken ct)
    {
        if (!chunked && (contentLength <= 0 || contentLength > HeaderLimit))
        {
            await WriteErrorAsync(stream, 400, "Bad Request", ct).ConfigureAwait(false);
            return;
        }

        byte[] body;
        if (chunked)
        {
            body = await ReadChunkedAsync(stream, HeaderLimit, ct).ConfigureAwait(false);
        }
        else
        {
            body = await ReadExactAsync(stream, (int)contentLength, ct).ConfigureAwait(false);
        }

        PrepareUploadRequest? request;
        try { request = LocalSendJson.Deserialize<PrepareUploadRequest>(body); }
        catch (JsonException ex)
        {
            // Rejecting the request is fine, but the peer must still see a complete
            // HTTP response — an aborted connection here is what reqwest surfaces as
            // "Connection reset by peer".
            Logger.Warn($"prepare-upload body rejected: {ex.Message}");
            request = null;
        }

        if (request is null)
        {
            await WriteErrorAsync(stream, 400, "Invalid body", ct).ConfigureAwait(false);
            return;
        }

        // PIN check (query string wins, then body).
        var queryParams = ParseQuery(query);
        var providedPin = queryParams.TryGetValue("pin", out var qp) ? qp : request.Pin;
        if (!string.IsNullOrEmpty(Pin) && !string.Equals(Pin, providedPin, StringComparison.Ordinal))
        {
            Logger.Warn("Rejected transfer: incorrect PIN");
            await WriteErrorAsync(stream, 401, "Invalid PIN", ct).ConfigureAwait(false);
            return;
        }

        var files = request.Files.Values
            .Select(f => new FileMetadata
            {
                Id = string.IsNullOrEmpty(f.Id) ? Guid.NewGuid().ToString("N") : f.Id,
                FileName = f.FileName,
                Size = f.Size,
                FileType = f.FileType,
                Sha256 = f.Sha256,
            })
            .ToList();

        if (files.Count == 0)
        {
            await WriteErrorAsync(stream, 400, "No files", ct).ConfigureAwait(false);
            return;
        }

        var decision = await GetDecisionAsync(request.Info, files, ct).ConfigureAwait(false);

        if (!decision.Accepted || decision.FileIds.Count == 0)
        {
            Logger.Info($"Transfer from {request.Info.Alias} rejected");
            await WriteErrorAsync(stream, 403, decision.Reason ?? "Declined", ct).ConfigureAwait(false);
            return;
        }

        var session = new ReceiveSession
        {
            Sender = remote?.Address ?? IPAddress.None,
            SenderName = request.Info.Alias,
        };

        foreach (var file in files)
        {
            session.Files[file.Id] = new ReceiveFile
            {
                FileId = file.Id,
                FileName = file.FileName,
                Size = file.Size,
                Sha256 = file.Sha256,
                Accepted = decision.FileIds.Contains(file.Id),
            };
        }

        _sessions[session.Id] = session;

        SessionStarted?.Invoke(new ReceiveSessionStarted
        {
            SessionId = session.Id,
            SenderName = session.SenderName,
            Files = files.Where(f => decision.FileIds.Contains(f.Id)).ToList(),
        });

        // Contract (protocol spec 4.1): files maps fileId -> token *string*, or null
        // when that file was declined. Anything else breaks the official clients.
        var response = new PrepareUploadResponse { SessionId = session.Id };
        foreach (var (id, file) in session.Files)
            response.Files[id] = file.Accepted ? file.Token : null;

        Logger.Info($"Session {session.Id} opened for {session.SenderName}: " +
                    $"{session.Files.Values.Count(f => f.Accepted)}/{session.Files.Count} file(s) accepted");

        await WriteJsonAsync(stream, 200, "OK", response, ct, WriteNulls).ConfigureAwait(false);
    }

    private async Task<ReceiveDecision> GetDecisionAsync(
        DeviceInfo sender, IReadOnlyList<FileMetadata> files, CancellationToken ct)
    {
        if (AutoAccept)
        {
            return new ReceiveDecision { Accepted = true, FileIds = files.Select(f => f.Id).ToHashSet() };
        }

        var args = new ReceiveRequestEventArgs { Sender = sender, Files = files };
        ReceiveRequested?.Invoke(args);

        var completed = await Task.WhenAny(
            args.Decision.Task,
            Task.Delay(args.Timeout, ct)).ConfigureAwait(false);

        if (completed == args.Decision.Task)
        {
            return await args.Decision.Task.ConfigureAwait(false);
        }

        Logger.Warn($"Receive request from {sender.Alias} timed out");
        return new ReceiveDecision { Accepted = false, Reason = "Timed out" };
    }

    // --------------------------------------------------------------------------- upload

    private async Task HandleUploadAsync(
        Stream stream, IPEndPoint? remote, string query, long contentLength, bool chunked, CancellationToken ct)
    {
        var q = ParseQuery(query);
        q.TryGetValue("sessionId", out var sessionId);
        q.TryGetValue("fileId", out var fileId);
        q.TryGetValue("token", out var token);

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId) ||
            !_sessions.TryGetValue(sessionId, out var session))
        {
            await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
            await WriteErrorAsync(stream, 404, "Unknown session", ct).ConfigureAwait(false);
            return;
        }

        if (session.Cancelled)
        {
            await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
            await WriteErrorAsync(stream, 403, "Session cancelled", ct).ConfigureAwait(false);
            return;
        }

        // Session is bound to the sender IP for security.
        if (remote is not null && !session.Sender.Equals(IPAddress.Any) && !session.Sender.Equals(remote.Address))
        {
            await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
            await WriteErrorAsync(stream, 403, "Session bound to another address", ct).ConfigureAwait(false);
            return;
        }

        if (!session.Files.TryGetValue(fileId, out var file) || !file.Accepted ||
            !string.Equals(file.Token, token, StringComparison.Ordinal))
        {
            await DrainAsync(stream, contentLength, ct).ConfigureAwait(false);
            await WriteErrorAsync(stream, 403, "Invalid token", ct).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);

        if (chunked)
        {
            // Official LocalSend clients stream the payload through reqwest's
            // Body::wrap_stream(ReceiverStream). That body has no known length, so reqwest
            // switches the request to "Transfer-Encoding: chunked" — which is exactly the
            // request shape this handler used to answer with HTTP 400. The framing is now
            // decoded on the fly straight into the destination file; buffering it first
            // would not scale to multi-gigabyte media files.
            using var body = new ChunkedInputStream(stream, MaxUploadBytes);
            if (!await SaveFileAsync(body, session, file, null, linked.Token).ConfigureAwait(false))
            {
                await WriteErrorAsync(stream, 500, "Write failed", ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            var length = contentLength > 0 ? contentLength : file.Size;
            if (!await SaveFileAsync(stream, session, file, length, linked.Token).ConfigureAwait(false))
            {
                await WriteErrorAsync(stream, 500, "Write failed", ct).ConfigureAwait(false);
                return;
            }
        }

        await WriteTextAsync(stream,
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the request body into the destination file.
    /// </summary>
    /// <param name="source">
    /// Body to read. For a chunked upload this is the chunk decoder, whose length is only
    /// known once the terminating chunk arrives, so <paramref name="declaredLength"/> is
    /// <c>null</c> and reading stops at the end of the payload.
    /// </param>
    /// <param name="declaredLength">
    /// Expected body length, or <c>null</c> when it is not known up front.
    /// </param>
    private async Task<bool> SaveFileAsync(
        Stream source, ReceiveSession session, ReceiveFile file, long? declaredLength, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var target = ResolveTargetPath(DownloadDirectory, file.FileName, ConflictMode);
        var partial = target + ".part";

        var index = session.Files.Values.ToList().IndexOf(file) + 1;
        var count = session.Files.Values.Count(f => f.Accepted);

        try
        {
            long received = 0;
            var buffer = new byte[TransferBufferSize];

            await using (var fs = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferSize, useAsync: true))
            {
                var lastReport = 0L;
                while (declaredLength is null || received < declaredLength.Value)
                {
                    var toRead = declaredLength is null
                        ? buffer.Length
                        : (int)Math.Min(buffer.Length, declaredLength.Value - received);
                    var read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        // End of a chunked payload is legitimate; an early end of a
                        // length-delimited one is not.
                        if (declaredLength is null) break;
                        throw new IOException("Unexpected end of stream");
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    if (received - lastReport >= 512 * 1024 || received == (declaredLength ?? received))
                    {
                        lastReport = received;
                        file.Received = received;
                        ProgressChanged?.Invoke(new TransferProgress
                        {
                            SessionId = session.Id,
                            DeviceName = session.SenderName,
                            FileName = file.FileName,
                            FileId = file.FileId,
                            Bytes = received,
                            // A chunked body carries no Content-Length, so the size the
                            // sender declared in prepare-upload is the only total available.
                            Total = declaredLength ?? file.Size,
                            FileIndex = index,
                            FileCount = count,
                            Sending = false,
                        });
                    }
                }
            }

            // A chunked payload simply ends when the peer stops, so a peer that went away
            // mid-transfer would otherwise leave a silently truncated file behind.
            if (declaredLength is null && file.Size > 0 && received != file.Size)
            {
                throw new IOException(
                    $"Truncated upload for {file.FileName} (expected {file.Size} bytes, got {received})");
            }

            if (VerifyChecksum && !string.IsNullOrEmpty(file.Sha256))
            {
                var actual = await LocalSendJson.Sha256HexAsync(partial, ct).ConfigureAwait(false);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"Checksum mismatch for {file.FileName} (expected {file.Sha256}, got {actual})");
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            file.Done = true;
            Logger.Info($"Saved {target} ({received} bytes)");

            TryCompleteSession(session);
            return true;
        }
        catch (OperationCanceledException)
        {
            file.Failed = true;
            TryDelete(partial);
            TryCompleteSession(session);
            throw;
        }
        catch (Exception ex)
        {
            file.Failed = true;
            Logger.Error($"Failed to save {file.FileName}", ex);
            TryDelete(partial);
            TryCompleteSession(session);
            return false;
        }
    }

    private void TryCompleteSession(ReceiveSession session)
    {
        var accepted = session.Files.Values.Where(f => f.Accepted).ToList();
        if (accepted.Count == 0 || accepted.Any(f => !f.Done && !f.Failed)) return;

        _sessions.TryRemove(session.Id, out _);
        SessionCompleted?.Invoke(new ReceiveSessionInfo
        {
            SessionId = session.Id,
            SenderName = session.SenderName,
            FileCount = accepted.Count(f => f.Done),
            Directory = DownloadDirectory,
            AnyFailed = accepted.Any(f => f.Failed),
        });
    }

    // --------------------------------------------------------------------------- cancel

    private async Task HandleCancelAsync(Stream stream, string query, CancellationToken ct)
    {
        var q = ParseQuery(query);
        if (q.TryGetValue("sessionId", out var sessionId) &&
            _sessions.TryRemove(sessionId, out var session))
        {
            session.Cancelled = true;
            Logger.Info($"Session {sessionId} cancelled by peer");
        }
        await WriteTextAsync(stream,
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
    }

    private void PurgeStaleSessions()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var (id, session) in _sessions)
        {
            if (session.CreatedUtc < cutoff && _sessions.TryRemove(id, out var removed))
            {
                AbortSession(removed);
            }
        }
    }

    /// <summary>Aborts an in-flight receive session (used by the progress window's Cancel button).</summary>
    public void CancelSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            Logger.Info($"Session {sessionId} cancelled locally");
            AbortSession(session);
        }
    }

    private static void AbortSession(ReceiveSession session)
    {
        session.Cancelled = true;
        try { session.Cts.Cancel(); } catch { }
    }

    // --------------------------------------------------------------------------- helpers

    private string ResolveTargetPath(string dir, string fileName, string mode)
    {
        var safe = SanitizeFileName(fileName);
        var target = Path.Combine(dir, safe);
        if (string.Equals(mode, "overwrite", StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            return target;

        var name = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) return "file";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static (string Path, string Query) SplitTarget(string target)
    {
        var idx = target.IndexOf('?');
        return idx < 0 ? (target, string.Empty) : (target[..idx], target[(idx + 1)..]);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(128);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (buffer[0] == (byte)'\n') return sb.ToString();
            if (buffer[0] != (byte)'\r') sb.Append((char)buffer[0]);
            if (sb.Length > HeaderLimit) throw new IOException("Header too large");
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Unexpected end of stream");
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// Parses a <c>Transfer-Encoding: chunked</c> body (RFC 7230 4.1) into memory,
    /// capped at <paramref name="maxBytes"/>.
    /// </summary>
    private static async Task<byte[]> ReadChunkedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var outcome = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (line is null) throw new IOException("Unexpected end of chunked body");

            // "chunk-size [chunk-ext]" — extensions are separated by ';' and ignored.
            var sizeText = line.Split(';', 2)[0].Trim();
            if (sizeText.Length == 0) continue;

            if (!long.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out var chunkSize) ||
                chunkSize < 0)
            {
                throw new IOException($"Invalid chunk size: '{line}'");
            }

            if (chunkSize == 0) break; // terminating chunk

            if (outcome.Length + chunkSize > maxBytes)
                throw new IOException("Chunked body exceeds the allowed size");

            var buffer = new byte[(int)Math.Min(chunkSize, 64 * 1024)];
            var taken = 0L;
            while (taken < chunkSize)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, chunkSize - taken)), ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Unexpected end of chunk data");
                await outcome.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                taken += read;
            }

            await ReadLineAsync(stream, ct).ConfigureAwait(false); // CRLF after the chunk data
        }

        // Trailer section: keep reading until a blank line.
        while (true)
        {
            var trailer = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (trailer is null || trailer.Length == 0) break;
        }

        return outcome.ToArray();
    }

    private static async Task DrainAsync(Stream stream, long length, CancellationToken ct)
    {
        if (length <= 0) return;
        var buffer = new byte[64 * 1024];
        var remaining = length;
        try
        {
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (Exception) { /* draining best-effort */ }
    }

    /// <summary>
    /// Options used for the prepare-upload reply. Declined files are signalled with a
    /// JSON <c>null</c> value, and <see cref="JsonIgnoreCondition.WhenWritingNull"/> would
    /// drop those entries entirely — which would hide the rejection from the sender, who
    /// could then not tell a declined file from a missing one.
    /// </summary>
    private static readonly JsonSerializerOptions WriteNulls = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private static Task WriteJsonAsync(Stream stream, int status, string reason, object body, CancellationToken ct)
    {
        return WriteJsonAsync(stream, status, reason, body, ct, LocalSendJson.Options);
    }

    private static Task WriteJsonAsync(
        Stream stream, int status, string reason, object body, CancellationToken ct, JsonSerializerOptions options)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, options));
        return WriteRawAsync(stream, status, reason, bytes, "application/json; charset=utf-8", ct);
    }

    private static Task WriteErrorAsync(Stream stream, int status, string reason, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(LocalSendJson.Serialize(new { error = reason }));
        return WriteRawAsync(stream, status, reason, body, "application/json; charset=utf-8", ct);
    }

    private static async Task WriteRawAsync(
        Stream stream, int status, string reason, byte[] body, string contentType, CancellationToken ct)
    {
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();

    // --------------------------------------------------------------------------- chunked body

    /// <summary>
    /// Decodes an HTTP/1.1 <c>Transfer-Encoding: chunked</c> request body on the fly
    /// (RFC 7230 4.1). Nothing is buffered: the caller receives the payload in the
    /// arbitrary pieces the peer happened to send it in, which is what makes it usable
    /// for multi-gigabyte uploads.
    /// </summary>
    private sealed class ChunkedInputStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _chunkRemaining;
        private bool _finished;
        private long _total;

        public ChunkedInputStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        /// <summary>Total payload bytes handed out so far.</summary>
        public long Total => _total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                if (_finished) return 0;

                if (_chunkRemaining <= 0)
                {
                    // Either a fresh chunk header or — right after a chunk's data — the
                    // empty line that terminates it.
                    if (!await BeginNextChunkAsync(ct).ConfigureAwait(false)) return 0;
                    continue;
                }

                if (buffer.Length == 0) return 0;

                var taken = (int)Math.Min(buffer.Length, _chunkRemaining);
                var read = await _inner.ReadAsync(buffer[..taken], ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("Unexpected end of chunked body");

                _chunkRemaining -= read;
                _total += read;
                return read;
            }
        }

        /// <summary>
        /// Reads the next chunk header. Returns <c>false</c> once the terminating chunk
        /// (and the trailer section) has been consumed.
        /// </summary>
        private async ValueTask<bool> BeginNextChunkAsync(CancellationToken ct)
        {
            var line = await ReadLineAsync(_inner, ct).ConfigureAwait(false);
            if (line is null) { _finished = true; return false; }

            // "chunk-size [chunk-ext]" — extensions are separated by ';' and ignored.
            var sizeText = line.Split(';', 2)[0].Trim();
            if (sizeText.Length == 0) return true; // the CRLF that closes the previous chunk data

            if (!long.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out var size) ||
                size < 0)
            {
                throw new IOException($"Invalid chunk size: '{line}'");
            }

            if (size == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(_inner, ct).ConfigureAwait(false);
                    if (trailer is null || trailer.Length == 0) break;
                }

                _finished = true;
                return false;
            }

            if (_total + size > _maxBytes)
                throw new IOException("Chunked body exceeds the allowed size");

            _chunkRemaining = size;
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
