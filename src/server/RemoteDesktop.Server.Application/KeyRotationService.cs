using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Application;

/// <summary>
/// A device changing its key (a new install of the key store, a suspected leak). The device must be registered on
/// this connection (it proved the OLD key when it registered) and hand in a statement signed by BOTH keys: the old
/// one says "I agree", the new one says "I hold it". The server cannot make one up. Peers that pinned the old key
/// can check the same statement themselves (see <c>SessionLifecycleService.GetPeerKeyRotationsAsync</c>).
/// </summary>
public sealed class KeyRotationService(
    IDeviceRegistry devices,
    IConnectionTracker connections,
    ISessionManager sessions,
    IRateLimiter rateLimiter,
    IAuditLog audit,
    TimeProvider clock)
{
    public async Task RotateAsync(ICallerContext caller, string newKeyBase64, string oldKeySignatureBase64, string newKeySignatureBase64)
    {
        var deviceId = connections.GetDeviceId(caller.ConnectionId)
            ?? throw new SignalingException("Device is not registered on this connection. Call BeginRegistration and RegisterDevice first.");
        if (!rateLimiter.TryConsume($"rotate:{deviceId}", 3, TimeSpan.FromHours(1)))
            throw new SignalingException("Too many requests.");

        var failure = await TryRotateAsync(deviceId, newKeyBase64, oldKeySignatureBase64, newKeySignatureBase64);
        await audit.WriteAsync(new AuditEvent
        {
            ActorDeviceId = deviceId,
            EventType = AuditEventType.DeviceKeyRotated,
            Success = failure is null,
            Reason = failure ?? "rotated" // detail stays here; the caller only hears that it did not work
        });
        if (failure is not null) throw new SignalingException("Key change failed.");
    }

    private async Task<string?> TryRotateAsync(DeviceId deviceId, string newKeyBase64, string oldSigBase64, string newSigBase64)
    {
        byte[] newKey, oldSig, newSig;
        try
        {
            newKey = SessionSupport.DecodeBase64(newKeyBase64, KeyRotationProof.MaxKeyBytes);
            oldSig = SessionSupport.DecodeBase64(oldSigBase64, RegistrationProof.MaxSignatureBytes);
            newSig = SessionSupport.DecodeBase64(newSigBase64, RegistrationProof.MaxSignatureBytes);
        }
        catch (SignalingException) { return "malformed"; }

        if (!KeyChecks.IsValidP256PublicKey(newKey)) return "invalid-new-key";
        var oldKey = await devices.GetPublicKeyAsync(deviceId);
        if (oldKey is null) return "unknown-device";
        if (oldKey.AsSpan().SequenceEqual(newKey)) return "same-key";

        var payload = KeyRotationProof.BuildPayload(deviceId.ToString(), oldKey, newKey);
        if (!KeyChecks.Verifies(oldKey, payload, oldSig)) return "bad-old-key-signature";
        if (!KeyChecks.Verifies(newKey, payload, newSig)) return "bad-new-key-signature";

        // The peers' sessions were keyed with the old key; changing it under them would break the next reconnect.
        if ((await sessions.GetActiveSessionsAsync(deviceId)).Count > 0) return "sessions-active";

        var rotations = await devices.GetRotationsAsync(deviceId);
        var ok = await devices.TryRotateKeyAsync(new KeyRotation
        {
            DeviceId = deviceId,
            Sequence = rotations.Count,
            OldKey = oldKey,
            NewKey = newKey,
            OldKeySignature = oldSig,
            NewKeySignature = newSig,
            RotatedAt = clock.GetUtcNow()
        });
        return ok ? null : "conflict";
    }
}
