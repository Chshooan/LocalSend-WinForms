using System.Drawing;

namespace LocalSendWinForms.Services;

/// <summary>Loads the application icon (generated from localsend-win.svg) for windows and the tray.</summary>
public static class AppIcon
{
    private static Icon? _cached;

    public static Icon Load()
    {
        if (_cached is not null) return _cached;

        // Preferred: the multi-resolution .ico shipped next to the executable.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "localsend-win.ico");
            if (File.Exists(path))
            {
                _cached = new Icon(path);
                return _cached;
            }
        }
        catch { /* fall through */ }

        // Fallback: the icon embedded into the executable by the build.
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                {
                    _cached = icon;
                    return _cached;
                }
            }
        }
        catch { /* fall through */ }

        _cached = SystemIcons.Application;
        return _cached;
    }
}
