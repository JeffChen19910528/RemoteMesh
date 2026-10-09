using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Security;

public sealed class SessionTokenService : ISessionTokenService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    private const int PurgeEveryNIssues = 256;
    private const int MaxTokenLength = 128; // base64 of 32 bytes is 44; anything longer cannot be ours

    private readonly TimeProvider _clock;
    private readonly TimeSpan _defaultLifetime;
    // Keyed by the SHA-256 of the token, never the token itself: a memory dump or a database copy holds nothing usable.
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();
    private int _issued;

    public SessionTokenService(TimeProvider? clock = null, TimeSpan? defaultLifetime = null)
    {
        _clock = clock ?? TimeProvider.System;
        _defaultLifetime = defaultLifetime ?? DefaultLifetime;
    }

    public string Issue(SessionId sessionId, DeviceId deviceId, TimeSpan? lifetime = null) =>
        IssueWithHash(sessionId, deviceId, lifetime).Token;

    /// <summary>Like <see cref="Issue"/>, also returning what a durable copy needs (the hash, never the token) and the expiry.</summary>
    public (string Token, byte[] Hash, DateTimeOffset Expires) IssueWithHash(SessionId sessionId, DeviceId deviceId, TimeSpan? lifetime = null)
    {
        if (Interlocked.Increment(ref _issued) % PurgeEveryNIssues == 0) Purge();

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = _clock.GetUtcNow() + (lifetime ?? _defaultLifetime);
        var hash = Hash(token);
        _tokens[Convert.ToHexString(hash)] = new TokenEntry(sessionId, deviceId, expires);
        return (token, hash, expires);
    }

    /// <summary>Puts a still-valid token back after a restart (by hash). Consumed and revoked tokens are simply not restored.</summary>
    public void Restore(byte[] tokenHash, SessionId sessionId, DeviceId deviceId, DateTimeOffset expires) =>
        _tokens[Convert.ToHexString(tokenHash)] = new TokenEntry(sessionId, deviceId, expires);

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public bool Validate(string token, SessionId sessionId, DeviceId deviceId) =>
        Lookup(token) is { } e && e.Matches(sessionId, deviceId) && e.IsLive(_clock.GetUtcNow());

    public bool TryConsume(string token, SessionId sessionId, DeviceId deviceId)
    {
        if (Lookup(token) is not { } e || !e.Matches(sessionId, deviceId)) return false;
        // The lock makes check-and-mark atomic: two racing consumers cannot both win.
        lock (e)
        {
            if (!e.IsLive(_clock.GetUtcNow())) return false;
            e.Consumed = true;
            return true;
        }
    }

    public void Revoke(SessionId sessionId)
    {
        foreach (var e in _tokens.Values)
            if (e.SessionId == sessionId)
                lock (e) e.Revoked = true;
    }

    private TokenEntry? Lookup(string token) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength && _tokens.TryGetValue(Convert.ToHexString(Hash(token)), out var e) ? e : null;

    private void Purge()
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, e) in _tokens)
            if (e.Expires < now) _tokens.TryRemove(key, out _);
    }

    private sealed class TokenEntry(SessionId sessionId, DeviceId deviceId, DateTimeOffset expires)
    {
        public SessionId SessionId { get; } = sessionId;
        public DeviceId DeviceId { get; } = deviceId;
        public DateTimeOffset Expires { get; } = expires;
        public bool Revoked { get; set; }
        public bool Consumed { get; set; }

        public bool Matches(SessionId s, DeviceId d) => SessionId == s && DeviceId == d;
        public bool IsLive(DateTimeOffset now) => !Revoked && !Consumed && now <= Expires;
    }
}
