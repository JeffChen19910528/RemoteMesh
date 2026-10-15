using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Security;

public sealed class OtpService : IOtpService
{
    private readonly TimeSpan _ttl;
    private readonly int _maxAttempts;

    private readonly ConcurrentDictionary<DeviceId, OtpEntry> _otps = new();
    private readonly ConcurrentDictionary<DeviceId, int> _attempts = new();

    public OtpService(TimeSpan? ttl = null, int maxAttempts = 5)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _maxAttempts = maxAttempts;
    }

    public string Generate(DeviceId forDevice)
    {
        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString("D6");
        _otps[forDevice] = new OtpEntry(code, DateTimeOffset.UtcNow.Add(_ttl));
        _attempts.TryRemove(forDevice, out _);
        return code;
    }

    public OtpVerifyResult Verify(DeviceId targetDevice, string code)
    {
        if (code is null) return OtpVerifyResult.Invalid;
        var attempts = _attempts.AddOrUpdate(targetDevice, 1, (_, n) => n + 1);
        if (attempts > _maxAttempts)
            return OtpVerifyResult.RateLimited;

        if (!_otps.TryGetValue(targetDevice, out var entry))
            return OtpVerifyResult.NotFound;

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            _otps.TryRemove(targetDevice, out _);
            return OtpVerifyResult.Expired;
        }

        // Constant-time comparison: response time must not reveal how many leading digits were right.
        if (!CodesMatch(entry.Code, code))
            return OtpVerifyResult.Invalid;

        _otps.TryRemove(targetDevice, out _);
        _attempts.TryRemove(targetDevice, out _);
        return OtpVerifyResult.Valid;
    }

    // FixedTimeEquals returns false at once for different lengths; that only reveals the (public) code length.
    private static bool CodesMatch(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied.Trim()));

    public void Invalidate(DeviceId forDevice)
    {
        _otps.TryRemove(forDevice, out _);
        _attempts.TryRemove(forDevice, out _);
    }

    private sealed record OtpEntry(string Code, DateTimeOffset ExpiresAt);
}
