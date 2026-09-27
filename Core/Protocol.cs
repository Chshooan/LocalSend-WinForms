using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSendWinForms.Core;

/// <summary>
/// Static constants that describe the LocalSend v2 protocol surface.
/// See https://github.com/localsend/protocol for the specification.
/// </summary>
public static class LocalSendProtocol
{
    /// <summary>Default IPv4 multicast group used for UDP announcements.</summary>
    public const string MulticastAddress = "224.0.0.167";

    /// <summary>Default UDP/TCP port used by every LocalSend device.</summary>
    public const int DefaultPort = 53317;

    /// <summary>Protocol version advertised in <see cref="DeviceInfo.Version"/>.</summary>
    public const string Version = "2.1";

    /// <summary>mDNS service type (used by the official clients for Bonjour discovery).</summary>
    public const string MdnsService = "_localsend._tcp.local";

    public const string ApiRoot = "/api/localsend";
    public const string V1 = "/api/localsend/v1";
    public const string V2 = "/api/localsend/v2";

    public const string InfoPath = V2 + "/info";
    public const string RegisterPath = V2 + "/register";
    public const string PrepareUploadPath = V2 + "/prepare-upload";
    public const string UploadPath = V2 + "/upload";
    public const string CancelPath = V2 + "/cancel";
    public const string PrepareDownloadPath = V2 + "/prepare-download";
    public const string DownloadPath = V2 + "/download";

    public const string DefaultFileType = "application/octet-stream";

    /// <summary>Announcements older than this are pruned from the device list.</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Identity document exchanged during discovery and during the upload handshake.
/// Serialized with camelCase keys, matching the reference implementation.
/// </summary>
public sealed class DeviceInfo
{
    public string Alias { get; set; } = Environment.MachineName;
    public string Version { get; set; } = LocalSendProtocol.Version;
    public string DeviceModel { get; set; } = "Windows";
    public string DeviceType { get; set; } = "desktop";
    public string Fingerprint { get; set; } = string.Empty;
    public int Port { get; set; } = LocalSendProtocol.DefaultPort;
    public string Protocol { get; set; } = "https";
    public bool Download { get; set; }

    [JsonPropertyName("announcement")]
    public bool Announcement { get; set; } = true;

    public DeviceInfo Clone() => new()
    {
        Alias = Alias,
        Version = Version,
        DeviceModel = DeviceModel,
        DeviceType = DeviceType,
        Fingerprint = Fingerprint,
        Port = Port,
        Protocol = Protocol,
        Download = Download,
        Announcement = Announcement,
    };
}

/// <summary>Metadata for a single file exchanged in the prepare-upload request.</summary>
public sealed class FileMetadata
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string FileType { get; set; } = LocalSendProtocol.DefaultFileType;
    public string? Sha256 { get; set; }
    public string? Preview { get; set; }
}

/// <summary>Body of POST /api/localsend/v2/prepare-upload.</summary>
public sealed class PrepareUploadRequest
{
    public DeviceInfo Info { get; set; } = new();
    public Dictionary<string, FileMetadata> Files { get; set; } = new();
    public string? Pin { get; set; }
}

/// <summary>
/// Body returned by POST /api/localsend/v2/prepare-upload.
/// The official clients decode <c>files</c> as a FLAT map of
/// <c>fileId -&gt; token string</c> (Rust: <c>HashMap&lt;String, Option&lt;String&gt;&gt;</c>,
/// Go: <c>map[string]string</c>, see the protocol spec section 4.1), i.e.
/// <c>{"sessionId":"abc","files":{"file-1":"token-1"}}</c>.
/// A <c>null</c> entry means the receiver declined that particular file.
/// Emitting a nested object here instead of a string makes those clients fail to
/// decode the body ("invalid type: map, expected a string"), so the value type
/// must stay <see cref="string"/>.
/// </summary>
public sealed class PrepareUploadResponse
{
    public string SessionId { get; set; } = string.Empty;
    public Dictionary<string, string?> Files { get; set; } = new();
}

/// <summary>A device found on the local network together with its network address.</summary>
public sealed class DiscoveredDevice
{
    public DeviceInfo Info { get; set; } = new();
    public IPAddress Address { get; set; } = IPAddress.None;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Stable identity: certificate fingerprint when available, else the address.</summary>
    public string Id => string.IsNullOrEmpty(Info.Fingerprint) ? Address.ToString() : Info.Fingerprint;

    public string DisplayName => string.IsNullOrWhiteSpace(Info.Alias) ? Address.ToString() : Info.Alias;

    /// <summary>Base URL (scheme + host + port) used to reach the device's API.</summary>
    public string BaseUrl =>
        $"{(string.Equals(Info.Protocol, "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https")}" +
        $"://{Address}:{Info.Port}";

    public override string ToString() => $"{DisplayName} ({Address})";
}

/// <summary>Progress payload shared by the send and receive pipelines.</summary>
public sealed class TransferProgress
{
    public string SessionId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public long Total { get; set; }
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
    public bool Sending { get; set; }

    public int Percent => Total > 0 ? (int)Math.Clamp(Bytes * 100L / Total, 0, 100) : 0;
}

/// <summary>Serialization + small protocol helpers shared across the app.</summary>
public static class LocalSendJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options);

    /// <summary>Best-effort MIME type lookup from a file extension.</summary>
    public static string MimeFromExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" or ".tgz" => "application/gzip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",
            ".tar" => "application/x-tar",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".exe" => "application/vnd.microsoft.portable-executable",
            ".msi" => "application/x-msdownload",
            ".apk" => "application/vnd.android.package-archive",
            _ => LocalSendProtocol.DefaultFileType,
        };
    }

    /// <summary>Lower-case hex SHA-256 of a file, streamed so large files stay memory-friendly.</summary>
    public static async Task<string> Sha256HexAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
