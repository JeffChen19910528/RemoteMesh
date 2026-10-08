using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface ISessionTokenService
{
    /// <summary>
    /// Issues a 32-byte random opaque token bound to the given session and device. Tokens are short-lived
    /// (<paramref name="lifetime"/>, default a few minutes), revocable and consumable once.
    /// </summary>
    string Issue(SessionId sessionId, DeviceId deviceId, TimeSpan? lifetime = null);

    /// <summary>Checks a token without using it up. False if unknown, expired, revoked, already consumed, or bound elsewhere.</summary>
    bool Validate(string token, SessionId sessionId, DeviceId deviceId);

    /// <summary>
    /// Atomically validates and uses the token up: exactly one caller can ever succeed for a given token,
    /// even under concurrent attempts. Use this wherever a token grants access (relay connect, resume).
    /// </summary>
    bool TryConsume(string token, SessionId sessionId, DeviceId deviceId);

    /// <summary>Revokes all tokens issued for a session.</summary>
    void Revoke(SessionId sessionId);
}
