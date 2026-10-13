using System.Reflection;

namespace DevPortal;

public static class AppInfo
{
    public const string Name = "DevPortal";
    public const string RepositoryUrl = "https://github.com/Jithran/devportal";

    public static string Version { get; } = ReadVersion();

    /// <summary>The Velopack install root. Updates replace only its <c>current</c> folder, so files here survive them.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string ReleaseUrl(string version) => $"{RepositoryUrl}/releases/tag/v{version}";

    private static string ReadVersion()
    {
        var version = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
