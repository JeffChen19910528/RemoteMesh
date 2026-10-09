using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

public static class ApplicationExtensions
{
    /// <summary>
    /// Registers the signaling business services. They need an <see cref="IClientNotifier"/> and an
    /// <see cref="IConnectionTracker"/> from the host (the SignalR adapter provides both).
    /// </summary>
    public static IServiceCollection AddSignalingApplication(this IServiceCollection services)
    {
        // TryAdd so tests/hosts can substitute their own clock or limits.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new SessionRecoveryOptions());
        services.TryAddSingleton(new SessionSecurityOptions());

        services.AddSingleton<SessionSupport>();
        services.AddSingleton<SessionLifecycleService>();
        services.AddSingleton<SessionNegotiationService>();
        services.AddSingleton<DeviceRegistrationService>();
        services.AddSingleton<KeyRotationService>();
        services.AddSingleton<TrustedDeviceService>();

        services.AddSingleton<SessionRecoveryService>();
        services.AddHostedService<SessionRecoveryReaper>();
        return services;
    }
}
