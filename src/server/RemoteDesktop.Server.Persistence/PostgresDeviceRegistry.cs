using Microsoft.EntityFrameworkCore;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Persistence;

public sealed class PostgresDeviceRegistry(IDbContextFactory<RemoteMeshDbContext> factory) : IDeviceRegistry
{
    public async Task RegisterAsync(DeviceInfo device, byte[] publicKey, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // One atomic statement: two registrations of the same device at once cannot collide.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Devices"" (""DeviceId"", ""DeviceName"", ""Platform"", ""CreatedAt"", ""PublicKey"")
            VALUES ({device.DeviceId.Value}, {device.DeviceName}, {device.Platform}, {device.CreatedAt}, {publicKey})
            ON CONFLICT (""DeviceId"") DO UPDATE SET
                ""DeviceName"" = EXCLUDED.""DeviceName"", ""Platform"" = EXCLUDED.""Platform"",
                ""CreatedAt"" = EXCLUDED.""CreatedAt"", ""PublicKey"" = EXCLUDED.""PublicKey""", ct);
    }

    public async Task<DeviceInfo?> FindAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId.Value, ct);
        return r is null ? null : new DeviceInfo
        {
            DeviceId = new DeviceId(r.DeviceId), DeviceName = r.DeviceName, Platform = r.Platform, CreatedAt = r.CreatedAt
        };
    }

    public async Task<byte[]?> GetPublicKeyAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Devices.AsNoTracking().Where(d => d.DeviceId == deviceId.Value)
            .Select(d => d.PublicKey).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryRotateKeyAsync(KeyRotation r, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The key is replaced only if it is still the one that was signed over, and the step is recorded in the same
        // transaction, so a key never changes without its history and two rotations cannot both win.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""Devices"" SET ""PublicKey"" = {r.NewKey}
            WHERE ""DeviceId"" = {r.DeviceId.Value} AND ""PublicKey"" = {r.OldKey}
              AND (SELECT COUNT(*) FROM ""DeviceKeyRotations"" WHERE ""DeviceId"" = {r.DeviceId.Value}) = {r.Sequence}
              AND {r.Sequence} < {KeyRotation.MaxPerDevice}", ct);
        if (changed != 1) return false;

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""DeviceKeyRotations"" (""DeviceId"", ""Sequence"", ""OldKey"", ""NewKey"", ""OldKeySignature"", ""NewKeySignature"", ""RotatedAt"")
                VALUES ({r.DeviceId.Value}, {r.Sequence}, {r.OldKey}, {r.NewKey}, {r.OldKeySignature}, {r.NewKeySignature}, {r.RotatedAt})", ct);
        }
        catch (DbUpdateException) { return false; } // lost the race for this sequence number (rolled back with the transaction)
        catch (Npgsql.PostgresException) { return false; }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<KeyRotation>> GetRotationsAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.KeyRotations.AsNoTracking().Where(k => k.DeviceId == deviceId.Value).OrderBy(k => k.Sequence).ToListAsync(ct);
        return rows.Select(k => new KeyRotation
        {
            DeviceId = deviceId, Sequence = k.Sequence, OldKey = k.OldKey, NewKey = k.NewKey,
            OldKeySignature = k.OldKeySignature, NewKeySignature = k.NewKeySignature, RotatedAt = k.RotatedAt
        }).ToList();
    }

    public async Task UnregisterAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Devices.Where(d => d.DeviceId == deviceId.Value).ExecuteDeleteAsync(ct);
    }
}
