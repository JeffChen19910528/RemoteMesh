using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Server.Infrastructure;
using RemoteDesktop.Server.Security;

namespace RemoteDesktop.Integration.Tests;

public sealed class HardeningUnitTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    // ── Session tokens ───────────────────────────────────────────────────

    [Fact]
    public void Token_ExpiresAfterItsLifetime()
    {
        var clock = new ManualClock();
        var svc = new SessionTokenService(clock);
        var (s, d) = (SessionId.New(), DeviceId.New());
        var token = svc.Issue(s, d, TimeSpan.FromMinutes(1));

        Assert.True(svc.Validate(token, s, d));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(svc.Validate(token, s, d));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(svc.Validate(token, s, d));
        Assert.False(svc.TryConsume(token, s, d));
    }

    [Fact]
    public void Token_DefaultLifetime_IsShort()
    {
        var clock = new ManualClock();
        var svc = new SessionTokenService(clock);
        var (s, d) = (SessionId.New(), DeviceId.New());
        var token = svc.Issue(s, d);

        clock.Advance(SessionTokenService.DefaultLifetime + TimeSpan.FromSeconds(1));

        Assert.True(SessionTokenService.DefaultLifetime <= TimeSpan.FromMinutes(10));
        Assert.False(svc.Validate(token, s, d));
    }

    [Fact]
    public void Token_ConsumeWorksOnce_AndThenValidateFails()
    {
        var svc = new SessionTokenService();
        var (s, d) = (SessionId.New(), DeviceId.New());
        var token = svc.Issue(s, d);

        Assert.True(svc.TryConsume(token, s, d));
        Assert.False(svc.TryConsume(token, s, d));
        Assert.False(svc.Validate(token, s, d));
    }

    [Fact]
    public void Token_ConsumeUnderRaces_HasExactlyOneWinner()
    {
        for (var round = 0; round < 50; round++)
        {
            var svc = new SessionTokenService();
            var (s, d) = (SessionId.New(), DeviceId.New());
            var token = svc.Issue(s, d);
            var wins = 0;

            Parallel.For(0, 16, _ =>
            {
                if (svc.TryConsume(token, s, d)) Interlocked.Increment(ref wins);
            });

            Assert.Equal(1, wins);
        }
    }

    [Fact]
    public void Token_WrongSessionOrDevice_DoesNotConsumeIt()
    {
        var svc = new SessionTokenService();
        var (s, d) = (SessionId.New(), DeviceId.New());
        var token = svc.Issue(s, d);

        Assert.False(svc.TryConsume(token, SessionId.New(), d));
        Assert.False(svc.TryConsume(token, s, DeviceId.New()));

        Assert.True(svc.TryConsume(token, s, d)); // still usable by its rightful owner
    }

    [Fact]
    public void Token_RevokeBlocksValidateAndConsume()
    {
        var svc = new SessionTokenService();
        var (s, d) = (SessionId.New(), DeviceId.New());
        var a = svc.Issue(s, d);
        var b = svc.Issue(s, DeviceId.New());

        svc.Revoke(s);

        Assert.False(svc.Validate(a, s, d));
        Assert.False(svc.TryConsume(a, s, d));
        Assert.False(svc.Validate(b, s, d));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Token_GarbageInput_IsRejected_WithoutThrowing(string? garbage)
    {
        var svc = new SessionTokenService();
        Assert.False(svc.Validate(garbage!, SessionId.New(), DeviceId.New()));
        Assert.False(svc.TryConsume(garbage!, SessionId.New(), DeviceId.New()));
        Assert.False(svc.TryConsume(new string('A', 100_000), SessionId.New(), DeviceId.New()));
    }

    [Fact]
    public void Token_IsRandomAndFullLength()
    {
        var svc = new SessionTokenService();
        var tokens = Enumerable.Range(0, 1000).Select(_ => svc.Issue(SessionId.New(), DeviceId.New())).ToList();

        Assert.Equal(1000, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Equal(32, Convert.FromBase64String(t).Length));
    }

    [Fact]
    public void Token_ExpiredEntries_ArePurged()
    {
        var clock = new ManualClock();
        var svc = new SessionTokenService(clock);
        var old = svc.Issue(SessionId.New(), DeviceId.New(), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(1));

        for (var i = 0; i < 600; i++) svc.Issue(SessionId.New(), DeviceId.New()); // crosses the purge interval

        Assert.False(svc.Validate(old, SessionId.New(), DeviceId.New()));
    }

    // ── Rate limiter / audit memory bounds ───────────────────────────────

    [Fact]
    public void RateLimiter_CountsWithinWindow_AndRecoversAfterIt()
    {
        var clock = new ManualClock();
        var rl = new InMemoryRateLimiter(clock);
        var w = TimeSpan.FromMinutes(1);

        Assert.True(rl.TryConsume("k", 3, w));
        Assert.True(rl.TryConsume("k", 3, w));
        Assert.True(rl.TryConsume("k", 3, w));
        Assert.False(rl.TryConsume("k", 3, w));
        Assert.True(rl.IsLockedOut("k", 3, w));
        Assert.True(rl.TryConsume("other", 3, w));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(rl.IsLockedOut("k", 3, w));
        Assert.True(rl.TryConsume("k", 3, w));
    }

    [Fact]
    public void RateLimiter_AttackerChosenKeys_CannotGrowMemoryWithoutBound()
    {
        var clock = new ManualClock();
        var rl = new InMemoryRateLimiter(clock);
        var w = TimeSpan.FromSeconds(10);

        for (var i = 0; i < 5000; i++) rl.TryConsume("k" + i, 5, w);
        clock.Advance(TimeSpan.FromMinutes(1)); // all buckets are now stale
        for (var i = 0; i < 2048; i++) rl.TryConsume("fresh" + i, 5, w); // crosses the purge interval

        Assert.True(rl.BucketCount < 3000, $"stale buckets were not purged ({rl.BucketCount})");
    }

    [Fact]
    public async Task AuditLog_IsBounded()
    {
        var log = new InMemoryAuditLog();
        for (var i = 0; i < InMemoryAuditLog.MaxEvents + 500; i++)
            await log.WriteAsync(new AuditEvent { EventType = AuditEventType.RateLimitTriggered, Success = false });

        Assert.Equal(InMemoryAuditLog.MaxEvents, log.Count);
    }

    // ── Registration proof format ────────────────────────────────────────

    [Fact]
    public void RegistrationProof_BindsEveryField()
    {
        var c = new byte[32];
        var baseline = RegistrationProof.BuildPayload(c, "device-1", "conn-1");

        Assert.NotEqual(baseline, RegistrationProof.BuildPayload(new byte[32].Select(_ => (byte)1).ToArray(), "device-1", "conn-1"));
        Assert.NotEqual(baseline, RegistrationProof.BuildPayload(c, "device-2", "conn-1"));
        Assert.NotEqual(baseline, RegistrationProof.BuildPayload(c, "device-1", "conn-2"));
        Assert.Equal(baseline, RegistrationProof.BuildPayload(c, "device-1", "conn-1"));
    }

    [Fact]
    public void RegistrationProof_FieldBoundaries_AreUnambiguous()
    {
        var c = new byte[32];
        // "ab"+"c" and "a"+"bc" must not produce the same bytes (no concatenation ambiguity).
        Assert.NotEqual(RegistrationProof.BuildPayload(c, "ab", "c"), RegistrationProof.BuildPayload(c, "a", "bc"));
    }
}
