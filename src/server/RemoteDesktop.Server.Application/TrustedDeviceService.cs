using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>A device's standing list of peers that may connect without an interactive prompt.</summary>
public sealed class TrustedDeviceService(
    SessionSupport support,
    ITrustedDeviceStore store,
    IRateLimiter rateLimiter,
    IAuditLog audit,
    SessionSecurityOptions security,
    TimeProvider clock)
{
    public async Task AddAsync(ICallerContext caller, string trustedDeviceIdStr, string deviceName, long defaultPermissions)
    {
        var ownerId = support.RequireRegistered(caller);

        if (!DeviceId.TryParse(trustedDeviceIdStr ?? string.Empty, out var trustedId) || trustedId == ownerId)
            throw new SignalingException("Invalid device ID.");
        SessionSupport.RequireText(deviceName, security.MaxDeviceNameLength, "Invalid device name.");
        // A standing trust must never silently include restart/shutdown.
        if (defaultPermissions < 0 || (defaultPermissions & ~(long)Permission.FullControl) != 0)
            throw new SignalingException("Invalid permissions.");
        if (!rateLimiter.TryConsume($"trustadd:{ownerId}", 30, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");
        if ((await store.ListAsync(ownerId)).Count >= security.MaxTrustedDevicesPerOwner
            && !await store.IsTrustedAsync(ownerId, trustedId))
            throw new SignalingException("Trusted device limit reached.");

        await store.AddAsync(new TrustedDevice
        {
            OwnerDeviceId = ownerId,
            TrustedDeviceId = trustedId,
            TrustedDeviceName = deviceName.Trim(),
            TrustedSince = clock.GetUtcNow(),
            DefaultPermissions = (Permission)defaultPermissions
        });

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = ownerId,
            TargetDeviceId = trustedId,
            EventType = AuditEventType.TrustedDeviceAdded,
            Success = true
        });
    }

    public async Task RemoveAsync(ICallerContext caller, string trustedDeviceIdStr)
    {
        var ownerId = support.RequireRegistered(caller);

        if (!DeviceId.TryParse(trustedDeviceIdStr ?? string.Empty, out var trustedId))
            throw new SignalingException("Invalid device ID.");

        await store.RemoveAsync(ownerId, trustedId);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = ownerId,
            TargetDeviceId = trustedId,
            EventType = AuditEventType.TrustedDeviceRemoved,
            Success = true
        });
    }

    public async Task<List<TrustedDeviceDto>> ListAsync(ICallerContext caller)
    {
        var ownerId = support.RequireRegistered(caller);
        var list = await store.ListAsync(ownerId);
        return list.Select(e => new TrustedDeviceDto
        {
            DeviceId = e.TrustedDeviceId.ToString(),
            DeviceName = e.TrustedDeviceName,
            TrustedSince = e.TrustedSince,
            DefaultPermissions = (long)e.DefaultPermissions
        }).ToList();
    }
}
