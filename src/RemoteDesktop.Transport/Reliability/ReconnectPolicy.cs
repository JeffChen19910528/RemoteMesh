namespace RemoteDesktop.Transport.Reliability;

/// <summary>Exponential backoff with jitter, bounded by attempt count and an overall session-recovery timeout.</summary>
public sealed class ReconnectPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double Multiplier { get; init; } = 2.0;
    /// <summary>±fraction applied to each delay so many clients do not retry in lockstep.</summary>
    public double JitterRatio { get; init; } = 0.2;
    public int MaxAttempts { get; init; } = 10;
    /// <summary>Total time allowed to recover before the session is abandoned.</summary>
    public TimeSpan RecoveryTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <param name="attempt">1-based attempt number.</param>
    /// <param name="random">Returns [0,1); injectable for tests.</param>
    public TimeSpan DelayFor(int attempt, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var baseMs = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        baseMs = Math.Min(baseMs, MaxDelay.TotalMilliseconds);
        var r = (random ?? Random.Shared.NextDouble)();
        var jittered = baseMs * (1 + JitterRatio * (2 * r - 1));
        return TimeSpan.FromMilliseconds(Math.Max(0, jittered));
    }
}
