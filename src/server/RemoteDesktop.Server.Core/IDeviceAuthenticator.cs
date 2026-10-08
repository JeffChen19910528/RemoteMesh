using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public enum DeviceAuthResult
{
    Ok,
    NoChallenge,   // none issued, already used, or expired
    InvalidKey,    // not a well-formed P-256 public key
    KeyMismatch,   // DeviceId already registered with a different key
    BadSignature
}

/// <summary>
/// Proof-of-possession for device registration. The first registration of a DeviceId pins its public key
/// (trust on first use); every later registration must be signed by that same key.
/// </summary>
public interface IDeviceAuthenticator
{
    /// <summary>Random single-use challenge bound to one connection, valid for a short time.</summary>
    byte[] IssueChallenge(string connectionId);

    /// <summary>Consumes the challenge (success or not) and checks the signature.</summary>
    Task<DeviceAuthResult> AuthenticateAsync(string connectionId, DeviceId deviceId, byte[] claimedPublicKey,
        byte[] signature, CancellationToken ct = default);

    void Forget(string connectionId);
}
