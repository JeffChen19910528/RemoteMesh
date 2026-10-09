using Microsoft.EntityFrameworkCore;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// What the server remembers across restarts: who the devices are (and their public keys), which short ID belongs to
/// whom, who trusts whom, and the audit trail. Never stored: screen frames, clipboard data, file content, private keys.
/// </summary>
public sealed class RemoteMeshDbContext(DbContextOptions<RemoteMeshDbContext> options) : DbContext(options)
{
    public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();
    public DbSet<KeyRotationRecord> KeyRotations => Set<KeyRotationRecord>();
    public DbSet<ShortIdRecord> ShortIds => Set<ShortIdRecord>();
    public DbSet<TrustedDeviceRecord> TrustedDevices => Set<TrustedDeviceRecord>();
    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SessionTokenRecord> SessionTokens => Set<SessionTokenRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DeviceRecord>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.DeviceName).HasMaxLength(256).IsRequired();
            e.Property(x => x.Platform).HasMaxLength(64).IsRequired();
            e.Property(x => x.PublicKey).IsRequired();
        });

        b.Entity<KeyRotationRecord>(e =>
        {
            e.ToTable("DeviceKeyRotations");
            e.HasKey(x => new { x.DeviceId, x.Sequence }); // two rotations racing for the same step: one loses on this key
        });

        b.Entity<ShortIdRecord>(e =>
        {
            e.ToTable("ShortIds");
            e.HasKey(x => x.ShortId);
            e.Property(x => x.ShortId).HasMaxLength(9).IsFixedLength();
            e.HasIndex(x => x.DeviceId).IsUnique(); // one number per device, one device per number
        });

        b.Entity<TrustedDeviceRecord>(e =>
        {
            e.ToTable("TrustedDevices");
            e.HasKey(x => new { x.OwnerDeviceId, x.TrustedDeviceId });
            e.Property(x => x.TrustedDeviceName).HasMaxLength(256).IsRequired();
        });

        b.Entity<SessionRecord>(e =>
        {
            e.ToTable("Sessions");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.RequesterShortId).HasMaxLength(9);
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.RequesterDeviceId);
            e.HasIndex(x => x.TargetDeviceId);
        });

        b.Entity<SessionTokenRecord>(e =>
        {
            e.ToTable("SessionTokens");
            e.HasKey(x => x.TokenHash); // the SHA-256 of the token; the token itself is never stored
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.Expires);
        });

        b.Entity<AuditLogRecord>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(x => x.EventId);
            e.Property(x => x.Reason).HasMaxLength(1024);
            e.HasIndex(x => x.ActorDeviceId);
            e.HasIndex(x => x.TargetDeviceId);
            e.HasIndex(x => x.Timestamp);
        });
    }
}

public sealed class DeviceRecord
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] PublicKey { get; set; } = [];
}

public sealed class KeyRotationRecord
{
    public Guid DeviceId { get; set; }
    public int Sequence { get; set; }
    public byte[] OldKey { get; set; } = [];
    public byte[] NewKey { get; set; } = [];
    public byte[] OldKeySignature { get; set; } = [];
    public byte[] NewKeySignature { get; set; } = [];
    public DateTimeOffset RotatedAt { get; set; }
}

public sealed class ShortIdRecord
{
    public string ShortId { get; set; } = "";
    public Guid DeviceId { get; set; }
}

public sealed class TrustedDeviceRecord
{
    public Guid OwnerDeviceId { get; set; }
    public Guid TrustedDeviceId { get; set; }
    public string TrustedDeviceName { get; set; } = "";
    public DateTimeOffset TrustedSince { get; set; }
    public int DefaultPermissions { get; set; }
}

public sealed class AuditLogRecord
{
    public Guid EventId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid ActorDeviceId { get; set; }
    public Guid? TargetDeviceId { get; set; }
    public Guid? SessionId { get; set; }
    public int EventType { get; set; }
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

public sealed class SessionRecord
{
    public Guid SessionId { get; set; }
    public Guid RequesterDeviceId { get; set; }
    public Guid TargetDeviceId { get; set; }
    public string? RequesterShortId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int State { get; set; }
    public int ConnectionMode { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public int Permissions { get; set; }
    public int Epoch { get; set; }
}

/// <summary>A token that can still be used. Used or revoked tokens are deleted, so "not here" always means "refuse".</summary>
public sealed class SessionTokenRecord
{
    public byte[] TokenHash { get; set; } = [];
    public Guid SessionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset Expires { get; set; }
}
