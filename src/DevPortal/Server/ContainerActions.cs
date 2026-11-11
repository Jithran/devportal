using System.Security.Cryptography;
using System.Text;
using DevPortal.Discovery;
using DevPortal.Settings;

namespace DevPortal.Server;

/// <summary>
/// The only state-changing endpoint of the portal (D-4): stop or restart a Docker container, when the operator enabled it in the tray.
/// Every check runs here on the server; hiding the buttons is not a guard.
/// </summary>
public sealed class ContainerActions(
    SettingsStore settings,
    Func<Snapshot?> latest,
    Func<string, ContainerAction, CancellationToken, Task<string?>> act,
    TimeProvider clock)
{
    internal const int MaxActions = 5;
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private static readonly string[] Allowed = ["stop", "restart"];

    private readonly byte[] token = RandomNumberGenerator.GetBytes(32);
    private readonly Queue<DateTimeOffset> recent = new();
    private readonly object sync = new();

    /// <summary>Per server run; handed out by /api/config only while actions are on, which only the same origin can read.</summary>
    public string Token => Convert.ToHexString(token);

    public async Task<IResult> HandleAsync(string id, string action, string? presentedToken, string who, CancellationToken cancellationToken)
    {
        if (!settings.Current.ContainerActionsEnabled)
            return Results.NotFound();
        if (!TokenMatches(presentedToken))
            return Refuse(StatusCodes.Status403Forbidden, "bad token", action, id, who);
        if (!Allowed.Contains(action, StringComparer.Ordinal))
            return Results.NotFound();

        var container = Find(latest(), id);
        if (container is null)
            return Refuse(StatusCodes.Status404NotFound, "container not in the last scan", action, id, who);
        if (!TakeSlot())
            return Refuse(StatusCodes.Status429TooManyRequests, "rate limit", action, id, who);

        var label = $"{action} {container.ContainerName} ({id})";
        var error = await act(id, Enum.Parse<ContainerAction>(action, ignoreCase: true), cancellationToken);
        AppLog.Info($"Container action: {label} by {who} -> {(error is null ? "ok" : $"failed: {error}")}");
        return error is null ? Results.NoContent() : Results.Problem(error, statusCode: StatusCodes.Status502BadGateway);
    }

    internal static Instance? Find(Snapshot? snapshot, string id) => snapshot?.Projects
        .SelectMany(p => p.Versions).SelectMany(v => v.Instances)
        .FirstOrDefault(i => i.Source == InstanceSource.Docker && i.ContainerId == id);

    private bool TokenMatches(string? presented) =>
        presented is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Token));

    private bool TakeSlot()
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            while (recent.Count > 0 && now - recent.Peek() > Window)
                recent.Dequeue();
            if (recent.Count >= MaxActions)
                return false;
            recent.Enqueue(now);
            return true;
        }
    }

    private static IResult Refuse(int status, string reason, string action, string id, string who)
    {
        AppLog.Warn($"Container action refused: {action} {id} by {who} ({reason})");
        return Results.StatusCode(status);
    }
}
