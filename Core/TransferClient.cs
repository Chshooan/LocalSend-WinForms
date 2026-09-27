using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace LocalSendWinForms.Core;

/// <summary>Outcome of a send operation to one device.</summary>
public sealed class SendResult
{
    public int Total { get; set; }
    public int Sent { get; set; }
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0 && Sent > 0;
}

/// <summary>
/// Sends files to a peer using the LocalSend two-phase protocol:
/// prepare-upload (metadata handshake) followed by one streamed upload per file.
/// The peer's TLS certificate is pinned to the fingerprint learned during
/// discovery, and transient network failures are retried with backoff.
/// </summary>
public sealed class TransferClient
{
    private readonly Func<DeviceInfo> _localInfoProvider;

    public TransferClient(Func<DeviceInfo> localInfoProvider) => _localInfoProvider = localInfoProvider;

    /// <summary>When false, files are sent without a SHA-256 (faster handshake).</summary>
    public bool ComputeSha256 { get; set; } = true;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Cause of the most recent failed handshake, or <c>null</c> when healthy.</summary>
    private string? _prepareFailure;

    public async Task<SendResult> SendAsync(
        DiscoveredDevice device,
        IReadOnlyList<string> filePaths,
        string? pin,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        var result = new SendResult { Total = filePaths.Count };

        // 1. Build metadata (id, name, size, mime, optional checksum).
        var items = new List<(FileMetadata Meta, string Path)>(filePaths.Count);
        foreach (var path in filePaths)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { result.Errors.Add($"{Path.GetFileName(path)}: not found"); continue; }

                var meta = new FileMetadata
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FileName = fi.Name,
                    Size = fi.Length,
                    FileType = LocalSendJson.MimeFromExtension(path),
                    Sha256 = ComputeSha256
                        ? await LocalSendJson.Sha256HexAsync(path, ct).ConfigureAwait(false)
                        : null,
                };
                items.Add((meta, path));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (items.Count == 0) return result;

        using var http = CreateHttpClient(device);

        // 2. Handshake. `prepared` is refreshed automatically if a session expires.
        PrepareUploadResponse? prepared = await PrepareAsync(http, device, items, pin, ct)
            .ConfigureAwait(false);
        if (prepared is null)
        {
            // `prepare-upload` is the first request on the wire. If it fails during the TLS
            // handshake nothing is ever transmitted, which looks to the other side exactly
            // like "no request was received" — so name the cause explicitly.
            result.Errors.Add(_prepareFailure is null
                ? "Handshake failed"
                : $"Handshake failed: {_prepareFailure}");
            return result;
        }
        _prepareFailure = null;

        /// <summary>
        /// Reads the per-file upload token. The receiver maps <c>fileId -> token string</c>
        /// (or null when the file was declined), so the value is used directly — a nested
        /// object here fails on every official LocalSend build.
        /// </summary>
        bool TryToken(string fileId, out string token)
        {
            token = string.Empty;
            if (prepared is null) return false;
            if (!prepared.Files.TryGetValue(fileId, out var t)) return false;
            if (string.IsNullOrEmpty(t)) return false;
            token = t;
            return true;
        }

        // 3. One upload request per file, with retry / re-handshake.
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (meta, path) = items[i];
            progress?.Report(new TransferProgress
            {
                DeviceName = device.DisplayName,
                FileName = meta.FileName,
                FileId = meta.Id,
                Bytes = 0,
                Total = meta.Size,
                FileIndex = i + 1,
                FileCount = items.Count,
                Sending = true,
            });

            var ok = await UploadOneAsync(meta, path, i + 1, items.Count, progress).ConfigureAwait(false);
            if (ok)
            {
                result.Sent++;
            }
            else if (!TryToken(meta.Id, out _))
            {
                result.Errors.Add($"{meta.FileName}: declined by receiver");
            }
            else
            {
                result.Errors.Add($"{meta.FileName}: transfer failed");
            }
        }

        if (result.Errors.Count > 0)
        {
            await CancelAsync(http, device, prepared?.SessionId, ct).ConfigureAwait(false);
        }

        return result;

        async Task<bool> UploadOneAsync(
            FileMetadata meta, string path, int index, int count, IProgress<TransferProgress>? report)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (!TryToken(meta.Id, out var token))
                {
                    // Session unknown/expired — re-run the handshake with the same metadata.
                    prepared = await PrepareAsync(http, device, items, pin, ct).ConfigureAwait(false);
                    if (!TryToken(meta.Id, out token))
                    {
                        await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                }

                try
                {
                    var url = $"{device.BaseUrl}{LocalSendProtocol.UploadPath}" +
                              $"?sessionId={Uri.EscapeDataString(prepared!.SessionId)}" +
                              $"&fileId={Uri.EscapeDataString(meta.Id)}" +
                              $"&token={Uri.EscapeDataString(token)}";

                    await using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

                    var lastReport = 0L;
                    var reporter = new ProgressStream(fs, sent =>
                    {
                        if (sent - lastReport < 256 * 1024 && sent != meta.Size) return;
                        lastReport = sent;
                        report?.Report(new TransferProgress
                        {
                            DeviceName = device.DisplayName,
                            FileName = meta.FileName,
                            FileId = meta.Id,
                            Bytes = sent,
                            Total = meta.Size,
                            FileIndex = index,
                            FileCount = count,
                            Sending = true,
                        });
                    });

                    using var content = new StreamContent(reporter, 1 << 20);
                    content.Headers.ContentType = new MediaTypeHeaderValue(LocalSendProtocol.DefaultFileType);
                    content.Headers.ContentLength = meta.Size;

                    using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        report?.Report(new TransferProgress
                        {
                            DeviceName = device.DisplayName,
                            FileName = meta.FileName,
                            FileId = meta.Id,
                            Bytes = meta.Size,
                            Total = meta.Size,
                            FileIndex = index,
                            FileCount = count,
                            Sending = true,
                        });
                        return true;
                    }

                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        prepared = null; // force a fresh handshake on the next attempt
                    }

                    Logger.Warn($"Upload of {meta.FileName} -> {response.StatusCode} " +
                                $"(attempt {attempt}/{MaxAttempts})");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.Warn($"Upload of {meta.FileName} failed (attempt {attempt}/{MaxAttempts}): {ex.Message}");
                }

                if (attempt < MaxAttempts)
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task<PrepareUploadResponse?> PrepareAsync(
        HttpClient http,
        DiscoveredDevice device,
        IReadOnlyList<(FileMetadata Meta, string Path)> items,
        string? pin,
        CancellationToken ct)
    {
        try
        {
            var body = new PrepareUploadRequest { Info = _localInfoProvider(), Pin = pin };
            foreach (var (meta, _) in items) body.Files[meta.Id] = meta;

            var json = LocalSendJson.Serialize(body);
            var url = device.BaseUrl + LocalSendProtocol.PrepareUploadPath;
            if (!string.IsNullOrEmpty(pin)) url += "?pin=" + Uri.EscapeDataString(pin);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = await SafeReadAsync(response, ct).ConfigureAwait(false);
                Logger.Warn($"prepare-upload rejected: {response.StatusCode} {reason}");
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return LocalSendJson.Deserialize<PrepareUploadResponse>(text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // TLS handshake failures put the real cause in the inner exception, and they
            // abort before any request bytes are sent — which is otherwise indistinguishable
            // from the peer never having received anything.
            _prepareFailure = ex is HttpRequestException or AuthenticationException
                ? "TLS connection could not be established (certificate fingerprint mismatch?)"
                : ex.Message;

            Logger.Error($"prepare-upload failed: {ex.Message} " +
                         $"inner={(ex.InnerException?.Message ?? "none")}", ex);
            return null;
        }
    }

    private static async Task CancelAsync(HttpClient http, DiscoveredDevice device, string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var url = $"{device.BaseUrl}{LocalSendProtocol.CancelPath}?sessionId={Uri.EscapeDataString(sessionId)}";
            using var response = await http.PostAsync(url, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Logger.Warn($"cancel failed: {ex.Message}"); }
    }

    private static HttpClient CreateHttpClient(DiscoveredDevice device)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // Trust-on-first-use pinning: only accept the exact certificate whose SHA-256
            // matches the fingerprint advertised by the peer during discovery.
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;

                // Peers advertise the hash as raw hex or as colon-separated hex; normalise
                // both sides so a cosmetic difference cannot break pinning (which would
                // abort the TLS handshake before a single byte of the request is sent).
                var expected = CertificateManager.NormalizeFingerprint(device.Info.Fingerprint);
                if (expected.Length == 0) return true;

                var actual = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
                return string.Equals(actual, expected, StringComparison.Ordinal);
            },
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));
}

/// <summary>Wraps a read stream and reports cumulative bytes as they are consumed by HttpClient.</summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _report;

    public ProgressStream(Stream inner, Action<long> report)
    {
        _inner = inner;
        _report = report;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0) _report(Position);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) _report(Position);
        return read;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
