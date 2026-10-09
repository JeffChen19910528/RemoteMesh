using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Security;

public sealed class DeviceAuthenticator : IDeviceAuthenticator
{
    public const int MaxPublicKeyBytes = 256;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);
    private const int MaxOutstandingChallenges = 10_000;

    private readonly IDeviceRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (byte[] Challenge, DateTimeOffset Expires)> _challenges = new();

    public DeviceAuthenticator(IDeviceRegistry registry, TimeProvider? clock = null)
    {
        _registry = registry;
        _clock = clock ?? TimeProvider.System;
    }

    public byte[] IssueChallenge(string connectionId)
    {
        var now = _clock.GetUtcNow();
        if (_challenges.Count > MaxOutstandingChallenges)
            foreach (var (key, value) in _challenges)
                if (value.Expires < now) _challenges.TryRemove(key, out _);

        var challenge = RandomNumberGenerator.GetBytes(RegistrationProof.ChallengeSize);
        _challenges[connectionId] = (challenge, now + ChallengeLifetime);
        return challenge;
    }

    public async Task<DeviceAuthResult> AuthenticateAsync(string connectionId, DeviceId deviceId,
        byte[] claimedPublicKey, byte[] signature, CancellationToken ct = default)
    {
        // Single use: removed before anything is checked, so a failed guess burns the challenge.
        if (!_challenges.TryRemove(connectionId, out var entry) || entry.Expires < _clock.GetUtcNow())
            return DeviceAuthResult.NoChallenge;

        if (claimedPublicKey.Length is 0 or > MaxPublicKeyBytes || !KeyChecks.IsValidP256PublicKey(claimedPublicKey))
            return DeviceAuthResult.InvalidKey;

        var pinned = await _registry.GetPublicKeyAsync(deviceId, ct);
        if (pinned is not null && !CryptographicOperations.FixedTimeEquals(pinned, claimedPublicKey))
            return DeviceAuthResult.KeyMismatch;

        if (signature.Length is 0 or > RegistrationProof.MaxSignatureBytes)
            return DeviceAuthResult.BadSignature;

        var payload = RegistrationProof.BuildPayload(entry.Challenge, deviceId.ToString(), connectionId);
        try
        {
            return DeviceKeyGenerator.Verify(claimedPublicKey, payload, signature)
                ? DeviceAuthResult.Ok
                : DeviceAuthResult.BadSignature;
        }
        catch (CryptographicException) { return DeviceAuthResult.BadSignature; }
        catch (ArgumentException) { return DeviceAuthResult.BadSignature; }
    }

    public void Forget(string connectionId) => _challenges.TryRemove(connectionId, out _);
}
