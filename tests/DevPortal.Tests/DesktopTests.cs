using DevPortal.Desktop;
using DevPortal.Updates;
using Microsoft.Win32;

namespace DevPortal.Tests;

public sealed class DesktopTests : IDisposable
{
    private readonly string keyPath = $@"Software\DevPortal.Tests\{Guid.NewGuid():N}";
    private readonly string root = Directory.CreateTempSubdirectory("devportal-install-").FullName;

    [Fact]
    public void Autostart_writes_and_removes_a_quoted_run_entry()
    {
        var autostart = new Autostart(keyPath);

        autostart.Enable(@"C:\Users\me\AppData\Local\DevPortal\DevPortal.exe");
        var command = autostart.Command;
        autostart.Disable();

        Assert.Equal("\"C:\\Users\\me\\AppData\\Local\\DevPortal\\DevPortal.exe\"", command);
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void Autostart_targets_the_velopack_stub_so_updates_do_not_break_it()
    {
        var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
        File.WriteAllText(Path.Combine(root, "DevPortal.exe"), "");

        Assert.Equal(Path.Combine(root, "DevPortal.exe"), Autostart.LaunchPath(Path.Combine(current, "DevPortal.exe")));
    }

    [Fact]
    public void Autostart_uses_the_process_itself_outside_an_install()
    {
        var exe = Path.Combine(root, "bin", "DevPortal.exe");

        Assert.Equal(exe, Autostart.LaunchPath(exe));
    }

    [Theory]
    [InlineData("1.0.1", "- one")]
    [InlineData("v1.0.10", "- ten")]
    [InlineData("2.0.0", "")]
    public void Changelog_section_matches_the_exact_version(string version, string expected)
    {
        const string changelog = "# Changelog\n\n## [Unreleased]\n- next\n\n## v1.0.10 — 2026-11-01\n- ten\n\n## v1.0.1 — 2026-10-20\r\n- one\r\n";

        Assert.Equal(expected, Changelog.Section(changelog, version));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        Directory.Delete(root, recursive: true);
    }
}
