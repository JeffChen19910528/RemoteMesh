using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// Short IDs that survive a restart. The database's two unique constraints (number, device) are the arbiter, so
/// several server instances or parallel requests can never hand out the same number twice.
/// </summary>
public sealed class PostgresShortIdDirectory(IDbContextFactory<RemoteMeshDbContext> factory) : IShortIdDirectory
{
    public const int MaxEntries = 100_000_000;
    private const int MaxAttempts = 20;

    public async Task<string> AssignAsync(DeviceId device, string? preferred, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await Current(db, device, ct);
        if (existing is not null) return existing;
        if (await db.ShortIds.CountAsync(ct) >= MaxEntries) throw new InvalidOperationException("No short IDs left.");

        var candidate = ShortId.TryNormalize(preferred, out var wanted) ? wanted : null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            candidate ??= RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""ShortIds"" (""ShortId"", ""DeviceId"") VALUES ({candidate}, {device.Value})
                ON CONFLICT DO NOTHING", ct);
            if (inserted == 1) return candidate;

            // Lost a race for this device (it has a number now) or the number is taken: use what exists, else try another.
            var now = await Current(db, device, ct);
            if (now is not null) return now;
            candidate = null;
        }
        throw new InvalidOperationException("Could not find a free short ID.");
    }

    public async Task<DeviceId?> ResolveAsync(string shortId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = await db.ShortIds.AsNoTracking().Where(s => s.ShortId == shortId)
            .Select(s => (Guid?)s.DeviceId).FirstOrDefaultAsync(ct);
        return id is { } g ? new DeviceId(g) : null;
    }

    public async Task<string?> GetAsync(DeviceId device, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Current(db, device, ct);
    }

    private static Task<string?> Current(RemoteMeshDbContext db, DeviceId device, CancellationToken ct) =>
        db.ShortIds.AsNoTracking().Where(s => s.DeviceId == device.Value).Select(s => s.ShortId).FirstOrDefaultAsync(ct);
}
