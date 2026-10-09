using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Security;
using RemoteDesktop.Server.Session;

namespace RemoteDesktop.Server.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Replaces the in-memory device registry, short IDs, trusted devices, audit log, sessions and session tokens
    /// with PostgreSQL. Call <see cref="InitializePersistenceAsync"/> after building, before the server takes traffic.
    /// </summary>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, DatabaseOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.AddPooledDbContextFactory<RemoteMeshDbContext>(o => o.UseNpgsql(options.ConnectionString));
        services.AddSingleton<IDeviceRegistry, PostgresDeviceRegistry>();
        services.AddSingleton<IShortIdDirectory, PostgresShortIdDirectory>();
        services.AddSingleton<ITrustedDeviceStore, PostgresTrustedDeviceStore>();
        services.AddSingleton<PostgresAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());

        services.AddSingleton(sp => new InMemorySessionManager(sp.GetService<TimeProvider>()));
        services.AddSingleton<ISessionManager>(sp => new PersistentSessionManager(
            sp.GetRequiredService<InMemorySessionManager>(), sp.GetRequiredService<IDbContextFactory<RemoteMeshDbContext>>()));
        services.AddSingleton(sp => new SessionTokenService(sp.GetService<TimeProvider>()));
        services.AddSingleton<ISessionTokenService>(sp => new PersistentSessionTokenService(
            sp.GetRequiredService<SessionTokenService>(), sp.GetRequiredService<IDbContextFactory<RemoteMeshDbContext>>()));

        services.AddHostedService<DatabaseHostedService>();
        return services;
    }

    /// <summary>
    /// Brings the schema up to date (EF migrations) and loads the sessions and tokens of the previous run. A database that is
    /// down stops the server here, loudly, instead of letting it run with half its memory.
    /// </summary>
    public static async Task InitializePersistenceAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<RemoteMeshDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
            await db.Database.MigrateAsync(ct); // creates the tables on a new database, applies new migrations on an old one

        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        await SessionRestore.RunAsync(
            factory, services.GetRequiredService<InMemorySessionManager>(), services.GetRequiredService<SessionTokenService>(),
            services.GetRequiredService<SessionRecoveryOptions>(), clock.GetUtcNow(), ct);
        services.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteMesh.Persistence").LogInformation("PostgreSQL ready.");
    }
}

/// <summary>Trims the audit log and spent token rows once a day.</summary>
internal sealed class DatabaseHostedService(
    IDbContextFactory<RemoteMeshDbContext> factory, PostgresAuditLog audit, DatabaseOptions options,
    ILogger<DatabaseHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                if (options.AuditRetentionDays > 0)
                {
                    var n = await audit.PruneAsync(DateTimeOffset.UtcNow.AddDays(-options.AuditRetentionDays), stop);
                    if (n > 0) log.LogInformation("Deleted {Count} audit entries past retention.", n);
                }
                await using var db = await factory.CreateDbContextAsync(stop);
                var now = DateTimeOffset.UtcNow;
                await db.SessionTokens.Where(t => t.Expires < now).ExecuteDeleteAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Database housekeeping failed: {Type}", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
