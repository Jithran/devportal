namespace DevPortal.Settings;

public enum ThemePreference { System, Dark, Light }

/// <summary>
/// Per-user settings, edited in the tray's Settings dialog.
/// The bind address is deliberately not here: DevPortal always listens on loopback only.
/// </summary>
public sealed record UserSettings
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MinRefreshSeconds = 2;
    public const int MaxRefreshSeconds = 300;

    public int Port { get; init; } = 7800;
    public int RefreshSeconds { get; init; } = 5;
    public bool ShowSystemByDefault { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool CheckForUpdates { get; init; } = true;
}
