using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Application;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Infrastructure;

namespace RemoteDesktop.Server.Signaling;

public static class SignalingExtensions
{
    public static IServiceCollection AddSignaling(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionTracker, InMemoryConnectionTracker>();
        services.AddSingleton<IClientNotifier, SignalRClientNotifier>();
        services.AddSignalR(o =>
        {
            // Signaling messages are tiny; an explicit cap stops oversized-message memory abuse.
            o.MaximumReceiveMessageSize = 64 * 1024;
            o.EnableDetailedErrors = false;
            o.MaximumParallelInvocationsPerClient = 1;
            o.AddFilter<SignalingExceptionFilter>();
        });
        services.AddSingleton<SignalingExceptionFilter>();

        services.AddSignalingApplication();
        return services;
    }

    public static IEndpointRouteBuilder MapSignalingHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<SignalingHub>("/hubs/signaling");
        return endpoints;
    }
}
