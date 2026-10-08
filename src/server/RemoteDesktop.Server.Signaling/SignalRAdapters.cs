using Microsoft.AspNetCore.SignalR;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Signaling;

/// <summary>Delivers business-layer events over the device's live SignalR connection.</summary>
public sealed class SignalRClientNotifier(IHubContext<SignalingHub> hub, IConnectionTracker connections) : IClientNotifier
{
    public async Task NotifyAsync(DeviceId device, string eventName, object?[] args, CancellationToken ct = default)
    {
        var connectionId = connections.GetConnectionId(device);
        if (connectionId is null) return;
        await hub.Clients.Client(connectionId).SendCoreAsync(eventName, args, ct);
    }
}

/// <summary>The caller's connection as the business layer sees it (no SignalR types leak through).</summary>
internal sealed class HubCallerAdapter(HubCallerContext context) : ICallerContext
{
    private const string FailuresKey = "regFails";

    public string ConnectionId => context.ConnectionId;
    public string IpAddress => context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public int RegistrationFailures
    {
        get => context.Items.TryGetValue(FailuresKey, out var v) && v is int n ? n : 0;
        set => context.Items[FailuresKey] = value;
    }

    public void Abort() => context.Abort();
}

/// <summary>Turns a business refusal into the error type SignalR sends to the client (same message).</summary>
public sealed class SignalingExceptionFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (SignalingException ex)
        {
            throw new HubException(ex.Message);
        }
    }
}
