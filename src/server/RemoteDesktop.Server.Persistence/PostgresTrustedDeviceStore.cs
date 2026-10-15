using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Persistence;

public sealed class PostgresTrustedDeviceStore(IDbContextFactory<RemoteMeshDbContext> factory) : ITrustedDeviceStore
{
    public async Task AddAsync(TrustedDevice entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""TrustedDevices"" (""OwnerDeviceId"", ""TrustedDeviceId"", ""TrustedDeviceName"", ""TrustedSince"", ""DefaultPermissions"")
            VALUES ({entry.OwnerDeviceId.Value}, {entry.TrustedDeviceId.Value}, {entry.TrustedDeviceName}, {entry.TrustedSince}, {(int)entry.DefaultPermissions})
            ON CONFLICT (""OwnerDeviceId"", ""TrustedDeviceId"") DO UPDATE SET
                ""TrustedDeviceName"" = EXCLUDED.""TrustedDeviceName"", ""TrustedSince"" = EXCLUDED.""TrustedSince"",
                ""DefaultPermissions"" = EXCLUDED.""DefaultPermissions""", ct);
    }

    public async Task RemoveAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.TrustedDevices.Where(t => t.OwnerDeviceId == owner.Value && t.TrustedDeviceId == trusted.Value).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> IsTrustedAsync(DeviceId owner, DeviceId requester, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TrustedDevices.AnyAsync(t => t.OwnerDeviceId == owner.Value && t.TrustedDeviceId == requester.Value, ct);
    }

    public async Task<TrustedDevice?> GetAsync(DeviceId owner, DeviceId trusted, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.TrustedDevices.AsNoTracking()
            .FirstOrDefaultAsync(t => t.OwnerDeviceId == owner.Value && t.TrustedDeviceId == trusted.Value, ct);
        return r is null ? null : ToModel(r);
    }

    public async Task<IReadOnlyList<TrustedDevice>> ListAsync(DeviceId owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.TrustedDevices.AsNoTracking().Where(t => t.OwnerDeviceId == owner.Value).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    private static TrustedDevice ToModel(TrustedDeviceRecord r) => new()
    {
        OwnerDeviceId = new DeviceId(r.OwnerDeviceId),
        TrustedDeviceId = new DeviceId(r.TrustedDeviceId),
        TrustedDeviceName = r.TrustedDeviceName,
        TrustedSince = r.TrustedSince,
        DefaultPermissions = (Permission)r.DefaultPermissions
    };
}
