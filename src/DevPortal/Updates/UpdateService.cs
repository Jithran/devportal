using Velopack;
using Velopack.Sources;

namespace DevPortal.Updates;

public enum UpdateCheckOutcome { UpToDate, Available, NotInstalled, Failed }

public sealed record AvailableUpdate(string Version, string? NotesMarkdown, UpdateInfo Info);

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, AvailableUpdate? Update = null, string? Error = null);

/// <summary>Velopack against the public GitHub releases, same pattern as EVE Together. Updates are never applied without the user's click.</summary>
public sealed class UpdateService
{
    /// <summary>Must match <c>vpk pack --channel</c> in the release workflow, or no update is ever found.</summary>
    public const string Channel = "win-x64-stable";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    public bool IsInstalled => CreateManager().IsInstalled;

    public async Task<UpdateCheckResult> CheckAsync()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled)
            return new UpdateCheckResult(UpdateCheckOutcome.NotInstalled);
        try
        {
            var info = await manager.CheckForUpdatesAsync().WaitAsync(CheckTimeout);
            if (info is null)
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
            var target = info.TargetFullRelease;
            return new UpdateCheckResult(UpdateCheckOutcome.Available, new AvailableUpdate(target.Version.ToString(), target.NotesMarkdown, info));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Update check failed", ex);
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: ex.Message);
        }
    }

    public async Task DownloadAsync(AvailableUpdate update, Action<int>? progress = null) =>
        await CreateManager().DownloadUpdatesAsync(update.Info, progress);

    /// <summary>Exits this process; Velopack swaps in the new version and starts it again.</summary>
    public void ApplyAndRestart(AvailableUpdate update) =>
        CreateManager().ApplyUpdatesAndRestart(update.Info.TargetFullRelease);

    private static UpdateManager CreateManager() =>
        new(new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false),
            new UpdateOptions { ExplicitChannel = Channel });
}
