using System.Collections.Concurrent;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Infrastructure;

public sealed class InMemoryRateLimiter : IRateLimiter
{
    private const int PurgeEveryNCalls = 1024;
    // Attacker-chosen keys (device IDs, IPs) must not be able to grow memory without bound.
    private const int MaxBuckets = 100_000;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private int _calls;

    public InMemoryRateLimiter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public bool TryConsume(string key, int maxAttempts, TimeSpan window)
    {
        if (Interlocked.Increment(ref _calls) % PurgeEveryNCalls == 0 || _buckets.Count > MaxBuckets)
            Purge();

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        lock (bucket)
        {
            var now = _clock.GetUtcNow();
            Prune(bucket, now, window);
            bucket.Window = window;
            if (bucket.Timestamps.Count >= maxAttempts)
                return false;
            bucket.Timestamps.Enqueue(now);
            return true;
        }
    }

    public bool IsLockedOut(string key, int maxAttempts, TimeSpan window)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
            return false;
        lock (bucket)
        {
            Prune(bucket, _clock.GetUtcNow(), window);
            return bucket.Timestamps.Count >= maxAttempts;
        }
    }

    public void Reset(string key) => _buckets.TryRemove(key, out _);

    public int BucketCount => _buckets.Count;

    private void Purge()
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                Prune(bucket, now, bucket.Window);
                if (bucket.Timestamps.Count == 0) _buckets.TryRemove(key, out _);
            }
        }
    }

    private static void Prune(Bucket bucket, DateTimeOffset now, TimeSpan window)
    {
        var cutoff = now - window;
        while (bucket.Timestamps.TryPeek(out var ts) && ts < cutoff)
            bucket.Timestamps.Dequeue();
    }

    private sealed class Bucket
    {
        public Queue<DateTimeOffset> Timestamps { get; } = new();
        public TimeSpan Window { get; set; }
    }
}
