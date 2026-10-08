using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// Device authentication: a connection acts as a device only after it proved possession of that device's private
/// key. The first registration of a DeviceId pins its public key; later ones must be signed by the same key.
/// </summary>
public sealed class DeviceRegistrationService(
    IDeviceAuthenticator auth,
    IDeviceRegistry devices,
    IPresenceService presence,
    IConnectionTracker connections,
    IOtpService otp,
    IRateLimiter rateLimiter,
    IAuditLog audit,
    SessionLifecycleService lifecycle,
    SessionSecurityOptions security,
    TimeProvider clock)
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    /// <summary>Step 1: a fresh random challenge bound to this connection.</summary>
    public string BeginRegistration(ICallerContext caller)
    {
        if (!rateLimiter.TryConsume($"regbegin:{caller.ConnectionId}", 10, TimeSpan.FromMinutes(1)))
            throw new SignalingException("Too many requests.");
        return Convert.ToBase64String(auth.IssueChallenge(caller.ConnectionId));
    }

    /// <summary>Step 2: the device signs <see cref="RegistrationProof.BuildPayload"/> with its private key.</summary>
    public async Task RegisterAsync(ICallerContext caller, DeviceInfoDto dto, string signatureBase64)
    {
        var ipKey = $"regfail:{caller.IpAddress}";
        if (rateLimiter.IsLockedOut(ipKey, security.RegistrationFailuresPerIpPer10Min, TenMinutes))
            throw new SignalingException("Too many failed attempts. Try again later.");

        var (device, publicKey) = Validate(dto);
        var signature = SessionSupport.DecodeBase64(signatureBase64, RegistrationProof.MaxSignatureBytes);

        var result = await auth.AuthenticateAsync(caller.ConnectionId, device.DeviceId, publicKey, signature);
        if (result != DeviceAuthResult.Ok)
        {
            rateLimiter.TryConsume(ipKey, security.RegistrationFailuresPerIpPer10Min, TenMinutes);
            await audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = device.DeviceId,
                EventType = AuditEventType.AuthenticationFailure,
                Success = false,
                Reason = $"device-registration:{result}"
            });

            caller.RegistrationFailures++;
            if (caller.RegistrationFailures >= security.RegistrationFailuresPerConnection)
                caller.Abort(); // stop guessing on this connection
            throw new SignalingException("Device authentication failed.");
        }

        await devices.RegisterAsync(device, publicKey);
        await presence.UpdateAsync(device.DeviceId, PresenceStatus.Online);
        connections.Register(caller.ConnectionId, device.DeviceId);

        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = device.DeviceId,
            EventType = AuditEventType.DeviceRegistered,
            Success = true
        });
    }

    public async Task HeartbeatAsync(ICallerContext caller)
    {
        var deviceId = connections.GetDeviceId(caller.ConnectionId);
        if (deviceId.HasValue)
            await presence.HeartbeatAsync(deviceId.Value);
    }

    /// <summary>Forgets the connection; its device goes offline and its Connected sessions pause for recovery.</summary>
    public async Task DisconnectedAsync(ICallerContext caller)
    {
        auth.Forget(caller.ConnectionId);
        var deviceId = connections.GetDeviceId(caller.ConnectionId);
        if (!deviceId.HasValue) return;

        connections.Unregister(caller.ConnectionId);
        await presence.UpdateAsync(deviceId.Value, PresenceStatus.Offline);
        otp.Invalidate(deviceId.Value);
        await lifecycle.PauseConnectedSessionsAsync(deviceId.Value);
    }

    private (DeviceInfo Device, byte[] PublicKey) Validate(DeviceInfoDto? dto)
    {
        if (dto is null || !DeviceId.TryParse(dto.DeviceId ?? string.Empty, out var id))
            throw new SignalingException("Invalid device information.");
        SessionSupport.RequireText(dto.DeviceName, security.MaxDeviceNameLength, "Invalid device information.");
        SessionSupport.RequireText(dto.Platform, 32, "Invalid device information.");
        var key = SessionSupport.DecodeBase64(dto.PublicKeyBase64, 256);

        return (new DeviceInfo
        {
            DeviceId = id,
            DeviceName = dto.DeviceName.Trim(),
            Platform = dto.Platform.Trim(),
            CreatedAt = clock.GetUtcNow()
        }, key);
    }
}
