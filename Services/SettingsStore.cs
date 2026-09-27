using System.Text.Json;

namespace LocalSendWinForms.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> and exposes the per-user data
/// directories used across the application.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSendWin");

    public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public static string CertificatePath => Path.Combine(ConfigDirectory, "certificate.pfx");

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.DeviceName))
                        settings.DeviceName = Environment.MachineName;
                    if (string.IsNullOrWhiteSpace(settings.DownloadDirectory))
                        settings.DownloadDirectory = AppSettings.DefaultDownloadDirectory();
                    if (settings.Port is < 1 or > 65535)
                        settings.Port = 53317;
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Core.Logger.Error("Failed to load settings, using defaults", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            Core.Logger.Error("Failed to save settings", ex);
        }
    }
}
