namespace RemoteDesktop.Encoding;

/// <summary>One rung of the quality ladder: how big the picture is, how hard it is compressed, how often it is sent.</summary>
public readonly record struct RateStep(int ScalePercent, int JpegQuality, int Fps);

/// <summary>An encoder that can be told to trade picture quality for bandwidth while it runs.</summary>
public interface IRateAdaptive
{
    /// <summary>Takes effect from the next frame. <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/> are the captured size.</summary>
    void Apply(RateStep step, int sourceWidth, int sourceHeight);
}

/// <summary>
/// Keeps the video inside a bandwidth budget by walking a quality ladder from measurements the sender already has:
/// how many bytes it produced and how long handing them to the connection took. A connection that cannot keep up
/// makes sends slow — that is the congestion signal, no separate probe is needed. It steps down quickly (a stalled
/// picture is worse than a soft one) and back up slowly, so it does not flap.
/// </summary>
public sealed class RateController
{
    /// <summary>Best to worst. A full-screen change of a busy 1080p picture costs about 36 / 28 / 20 / 12 / 8 / 6 / 4 Mbit/s down this list.</summary>
    public static IReadOnlyList<RateStep> Ladder { get; } =
    [
        new(100, 80, 20),
        new(100, 65, 20),
        new(100, 50, 15),
        new(75, 50, 15),
        new(50, 50, 12),
        new(50, 35, 10),
        new(40, 30, 8)
    ];

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private const int UpgradeAfterCalmWindows = 4;

    private readonly TimeProvider _clock;
    private long _budgetBytesPerSecond;
    private long _windowStart;
    private long _bytes;
    private TimeSpan _busy;
    private int _calm;
    private bool _cooldown;

    /// <param name="budgetKbps">The most the video should use, in kilobits per second.</param>
    public RateController(int budgetKbps, int startIndex = 0, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetKbps, 100);
        _budgetBytesPerSecond = budgetKbps * 1000L / 8;
        _clock = clock ?? TimeProvider.System;
        Index = Math.Clamp(startIndex, 0, Ladder.Count - 1);
        _windowStart = _clock.GetTimestamp();
    }

    public int Index { get; private set; }
    public RateStep Current => Ladder[Index];

    /// <summary>
    /// Lowers (or restores) the budget this controller enforces, on top of what it already sees in its own
    /// send timings — for example because an external signal (round-trip time) says the network got worse or
    /// recovered. Takes effect from the next window; never below the 100 kbit/s floor every controller accepts.
    /// </summary>
    public void AdjustBudgetKbps(int budgetKbps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetKbps, 100);
        Interlocked.Exchange(ref _budgetBytesPerSecond, budgetKbps * 1000L / 8);
    }

    /// <summary>Records one sent frame. Returns true when the ladder position changed.</summary>
    /// <param name="bytes">Size of the encoded frame.</param>
    /// <param name="sendTime">How long handing it to the connection took (long = the connection is saturated).</param>
    public bool Report(int bytes, TimeSpan sendTime)
    {
        _bytes += bytes;
        _busy += sendTime;

        var elapsed = _clock.GetElapsedTime(_windowStart);
        if (elapsed < Window) return false;

        var budgetBytesPerSecond = Interlocked.Read(ref _budgetBytesPerSecond);
        var seconds = elapsed.TotalSeconds;
        var rate = _bytes / seconds;                 // bytes per second actually produced
        var busy = _busy.TotalSeconds / seconds;     // share of the time spent waiting on the connection
        _windowStart = _clock.GetTimestamp();
        _bytes = 0;
        _busy = TimeSpan.Zero;

        // A window right after a change mixes old and new settings: do not judge on it.
        if (_cooldown) { _cooldown = false; return false; }

        var over = rate > budgetBytesPerSecond * 1.15 || busy > 0.7;
        var calm = rate < budgetBytesPerSecond * 0.6 && busy < 0.3;

        if (over)
        {
            _calm = 0;
            // Far over budget: skip rungs instead of crawling down one per second.
            var steps = rate > budgetBytesPerSecond * 2 || busy > 0.95 ? 2 : 1;
            var target = Math.Min(Ladder.Count - 1, Index + steps);
            if (target == Index) return false;
            Index = target;
            _cooldown = true;
            return true;
        }

        if (calm && Index > 0)
        {
            if (++_calm < UpgradeAfterCalmWindows) return false;
            _calm = 0;
            Index--;
            _cooldown = true;
            return true;
        }

        _calm = 0;
        return false;
    }
}
