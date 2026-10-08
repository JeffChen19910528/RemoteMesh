using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

/// <summary>
/// Pushes an event to a device's live connection. Business logic depends on this instead of SignalR, so it can
/// be exercised without a hub and the transport can change without touching the rules.
/// </summary>
public interface IClientNotifier
{
    /// <summary>Delivers to the device if it is currently connected; silently does nothing otherwise.</summary>
    Task NotifyAsync(DeviceId device, string eventName, object?[] args, CancellationToken ct = default);
}

public static class ClientNotifierExtensions
{
    public static Task NotifyAsync(this IClientNotifier notifier, DeviceId device, string eventName, params object?[] args) =>
        notifier.NotifyAsync(device, eventName, args, CancellationToken.None);
}
