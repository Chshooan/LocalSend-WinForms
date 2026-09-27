using System.Diagnostics;
using Microsoft.Win32;
using LocalSendWinForms.Core;

namespace LocalSendWinForms.Services;

/// <summary>
/// Registers the "Send with LocalSend" shell verb for the current user and
/// manages the run-at-startup entry. All writes go under HKEY_CURRENT_USER so
/// no administrator rights are required.
///
/// The verb is registered on the "*" (all files) class. Windows invokes the
/// command once per selected item, so a multi-file selection results in the
/// app being launched several times in quick succession; the single-instance
/// bootstrap (see Program.cs) forwards each path to the running instance, which
/// aggregates them into one send window — giving true multi-select support.
/// </summary>
public static class ShellIntegration
{
    private const string VerbKeyName = "LocalSend-WinForms";
    private const string VerbName = "SendWithLocalSend";
    private const string StartupValueName = "LocalSend-WinForms";
    private const string ShellRoot = @"Software\Classes\*\shell";

    public static bool IsMenuInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ShellRoot}\{VerbKeyName}");
        return key is not null;
    }

    public static void InstallMenu(string exePath, string verbText)
    {
        try
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{ShellRoot}\{VerbKeyName}");
            if (verb is null) return;

            verb.SetValue(null, verbText);                                   // default = display default action label
            verb.SetValue("MUIVerb", verbText, RegistryValueKind.String);    // display text
            verb.SetValue("Icon", exePath, RegistryValueKind.String);        // verb icon
            verb.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);

            using var command = verb.CreateSubKey("command");
            command?.SetValue(null, $"\"{exePath}\" --send \"%1\"", RegistryValueKind.String);

            Logger.Info($"Registered Explorer context menu entry at HKCU\\{ShellRoot}\\{VerbKeyName}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to install shell context menu", ex);
        }
    }

    public static void RemoveMenu()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ShellRoot}\{VerbKeyName}", throwOnMissingSubKey: false);
            Logger.Info("Removed Explorer context menu entry");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to remove shell context menu", ex);
        }
    }

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(StartupValueName) is string;
    }

    public static void SetStartup(bool enable, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;

            if (enable)
                key.SetValue(StartupValueName, $"\"{exePath}\" --startup", RegistryValueKind.String);
            else
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);

            Logger.Info($"Run-at-startup set to {enable}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update startup entry", ex);
        }
    }

    /// <summary>Opens a folder (or file) in File Explorer.</summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open {path}", ex);
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open {url}", ex);
        }
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LocalSend-WinForms.exe");
}
