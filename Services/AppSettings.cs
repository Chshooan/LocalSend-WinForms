namespace LocalSendWinForms.Services;

/// <summary>
/// User-configurable application state. Persisted as JSON in
/// %LocalAppData%\LocalSendWin\settings.json.
/// </summary>
public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;

    public string DownloadDirectory { get; set; } = DefaultDownloadDirectory();

    public int Port { get; set; } = 53317;

    public bool UseHttps { get; set; } = true;

    /// <summary>"zh-CN" or "en".</summary>
    public string Language { get; set; } = "zh-CN";

    public bool AutoAccept { get; set; }

    public bool NotifyOnReceive { get; set; } = true;

    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>"rename" (default) or "overwrite".</summary>
    public string ConflictMode { get; set; } = "rename";

    /// <summary>Optional PIN required from senders.</summary>
    public string? Pin { get; set; }

    public bool ComputeSha256 { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool LaunchAtStartup { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static string DefaultDownloadDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "LocalSend");
    }
}
