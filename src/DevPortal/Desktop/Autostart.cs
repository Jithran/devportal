using Microsoft.Win32;

namespace DevPortal.Desktop;

/// <summary>"Start with Windows" as a per-user Run entry, so it needs no admin rights.</summary>
public sealed class Autostart(string keyPath = Autostart.RunKey, string valueName = AppInfo.Name)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, $"\"{executablePath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Velopack runs the app from <c>&lt;root&gt;\current</c> and keeps a launcher stub in the root; the stub's path
    /// never changes across updates. A dev build has no stub and uses its own path.
    /// </summary>
    public static string LaunchPath(string processPath)
    {
        var directory = Path.GetDirectoryName(processPath);
        if (directory is not null && string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var stub = Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(processPath));
            if (File.Exists(stub))
                return stub;
        }
        return processPath;
    }
}
