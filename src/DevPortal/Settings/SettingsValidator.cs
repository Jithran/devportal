using System.Net;
using System.Net.Sockets;

namespace DevPortal.Settings;

public interface IPortAvailability
{
    bool IsFree(int port);
}

/// <summary>Checks a port by binding it on both loopback addresses, the same way the portal binds.</summary>
public sealed class LoopbackPortAvailability : IPortAvailability
{
    public bool IsFree(int port) => CanBind(IPAddress.Loopback, port) && CanBind(IPAddress.IPv6Loopback, port);

    private static bool CanBind(IPAddress address, int port)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try
        {
            socket.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public static class SettingsValidator
{
    /// <param name="boundPort">The port the portal holds right now; it is not "taken" by anyone else.</param>
    public static IReadOnlyList<string> Validate(UserSettings settings, int boundPort, IPortAvailability ports)
    {
        var errors = new List<string>();
        if (settings.Port is < UserSettings.MinPort or > UserSettings.MaxPort)
            errors.Add($"Port must be between {UserSettings.MinPort} and {UserSettings.MaxPort}.");
        else if (settings.Port != boundPort && !ports.IsFree(settings.Port))
            errors.Add($"Port {settings.Port} is already in use on localhost. Pick another port.");

        if (settings.RefreshSeconds is < UserSettings.MinRefreshSeconds or > UserSettings.MaxRefreshSeconds)
            errors.Add($"Refresh interval must be between {UserSettings.MinRefreshSeconds} and {UserSettings.MaxRefreshSeconds} seconds.");

        if (!Enum.IsDefined(settings.Theme))
            errors.Add("Unknown theme.");

        return errors;
    }
}
