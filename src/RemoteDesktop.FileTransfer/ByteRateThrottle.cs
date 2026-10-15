namespace RemoteDesktop.FileTransfer;

/// <summary>Fixed one-second-window byte rate limiter (Maximum Transfer Rate). 0 = unlimited.</summary>
public sealed class ByteRateThrottle
{
    private readonly long _bytesPerSecond;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTimeOffset _windowStart;
    private long _inWindow;

    public ByteRateThrottle(long bytesPerSecond, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _bytesPerSecond = bytesPerSecond;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _windowStart = _now();
    }

    public async Task WaitAsync(int bytes, CancellationToken ct = default)
    {
        if (_bytesPerSecond <= 0) return;

        var now = _now();
        if (now - _windowStart >= TimeSpan.FromSeconds(1))
        {
            _windowStart = now;
            _inWindow = 0;
        }

        if (_inWindow > 0 && _inWindow + bytes > _bytesPerSecond)
        {
            var wait = _windowStart + TimeSpan.FromSeconds(1) - now;
            if (wait > TimeSpan.Zero) await _delay(wait, ct);
            _windowStart = _now();
            _inWindow = 0;
        }
        _inWindow += bytes;
    }
}
