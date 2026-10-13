using System.Net;
using System.Net.Sockets;
using DevPortal.Settings;

namespace DevPortal.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("devportal-settings-").FullName;

    public static IEnumerable<object[]> ValidationCases =>
    [
        [new UserSettings { Port = 7900 }, Array.Empty<string>()],
        [new UserSettings { Port = 7800 }, Array.Empty<string>()],
        [new UserSettings { Port = 80 }, new[] { "Port must be between 1024 and 65535." }],
        [new UserSettings { Port = 5270 }, new[] { "Port 5270 is already in use on localhost. Pick another port." }],
        [new UserSettings { RefreshSeconds = 1 }, new[] { "Refresh interval must be between 2 and 300 seconds." }],
    ];

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void Validation_checks_range_and_that_a_new_port_is_free(object settings, string[] expected)
    {
        var taken = new TakenPorts(7800, 5270);

        var errors = SettingsValidator.Validate((UserSettings)settings, boundPort: 7800, taken);

        Assert.Equal(expected, errors);
    }

    [Fact]
    public void Port_check_sees_a_real_loopback_listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(new LoopbackPortAvailability().IsFree(port));
    }

    [Fact]
    public void Saved_settings_are_read_back_by_a_new_store()
    {
        var path = Path.Combine(directory, "settings.json");
        var saved = new UserSettings { Port = 7901, RefreshSeconds = 15, ShowSystemByDefault = true, Theme = ThemePreference.Light, CheckForUpdates = false };

        new SettingsStore(path).Save(saved);

        Assert.Equal(saved, new SettingsStore(path).Load());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "port": 80 }""")]
    public void Unreadable_or_out_of_range_file_falls_back_to_defaults(string content)
    {
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, content);

        Assert.Equal(new UserSettings(), new SettingsStore(path).Load());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class TakenPorts(params int[] taken) : IPortAvailability
    {
        public bool IsFree(int port) => !taken.Contains(port);
    }
}
