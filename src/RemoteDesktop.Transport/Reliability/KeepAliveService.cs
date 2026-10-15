using System.Buffers.Binary;

namespace RemoteDesktop.Transport.Reliability;

public sealed class KeepAliveOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>No traffic of any kind for this long means the peer is gone.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxOutstandingPings { get; init; } = 16;
}

/// <summary>
/// Heartbeat over the Control channel: periodic ping, immediate pong, RTT measurement, dead-peer detection.
/// Run it on both ends. <see cref="TickAsync"/> is the single time-driven step, so tests drive it with a manual clock.
/// Any inbound message counts as proof of life (the transport is already authenticated and encrypted).
/// </summary>
public sealed class KeepAliveService
{
    private const byte PingTag = 0xF0;
    private const byte PongTag = 0xF1;

    private readonly IChannel _control;
    private readonly KeepAliveOptions _opt;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<uint, DateTimeOffset> _outstanding = new();
    private DateTimeOffset _lastSeen;
    private uint _nextSeq;
    private int _lost;

    public KeepAliveService(IChannel control, KeepAliveOptions? options = null, TimeProvider? clock = null)
    {
        if (control.Type != Protocol.ChannelType.Control)
            throw new ArgumentException("Keep-alive must use the Control channel.", nameof(control));
        _control = control;
        _opt = options ?? new KeepAliveOptions();
        _clock = clock ?? TimeProvider.System;
        _lastSeen = _clock.GetUtcNow();
    }

    public bool IsLost => Volatile.Read(ref _lost) == 1;
    public TimeSpan? LastRtt { get; private set; }
    public DateTimeOffset LastSeen { get { lock (_lock) return _lastSeen; } }

    /// <summary>Raised once when the peer stops responding.</summary>
    public event Action? ConnectionLost;
    /// <summary>Raised for every round trip measured.</summary>
    public event Action<TimeSpan>? RttMeasured;
    /// <summary>Control messages that are not keep-alive frames.</summary>
    public event Action<ReadOnlyMemory<byte>>? ControlMessage;

    /// <summary>
    /// Starts counting again after the connection under the channel was replaced: the silence of the outage must not
    /// count against the new connection, and a peer declared lost gets a clean slate.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastSeen = _clock.GetUtcNow();
            _outstanding.Clear();
        }
        Volatile.Write(ref _lost, 0);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(_opt.Interval, _clock);
        // The receive loop must stop as soon as the peer is declared lost, not only on external cancellation.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = RunReceiveLoopAsync(stop.Token);
        try
        {
            while (!IsLost && await timer.WaitForNextTickAsync(ct))
                await TickAsync(ct);
        }
        catch (OperationCanceledException) { }
        finally { stop.Cancel(); }
        await receive;
    }

    /// <summary>Checks liveness, then sends the next ping.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (IsLost) return;

        var now = _clock.GetUtcNow();
        bool timedOut;
        lock (_lock) timedOut = now - _lastSeen > _opt.Timeout;
        if (timedOut)
        {
            RaiseLost();
            return;
        }

        uint seq;
        lock (_lock)
        {
            seq = _nextSeq++;
            _outstanding[seq] = now;
            while (_outstanding.Count > _opt.MaxOutstandingPings)
                _outstanding.Remove(_outstanding.Keys.Min());
        }
        await SendFrameAsync(PingTag, seq, ct);
    }

    public async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var raw in _control.ReceiveAsync(ct))
                await HandleAsync(raw, ct);
        }
        catch (OperationCanceledException) { }
    }

    public async Task HandleAsync(ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        lock (_lock) _lastSeen = now;

        var s = raw.Span;
        if (s.Length == 5 && s[0] == PingTag)
        {
            await SendFrameAsync(PongTag, BinaryPrimitives.ReadUInt32BigEndian(s[1..]), ct);
        }
        else if (s.Length == 5 && s[0] == PongTag)
        {
            DateTimeOffset sent;
            bool known;
            lock (_lock)
            {
                var seq = BinaryPrimitives.ReadUInt32BigEndian(s[1..]);
                known = _outstanding.Remove(seq, out sent);
            }
            if (known)
            {
                var rtt = now - sent;
                LastRtt = rtt;
                RttMeasured?.Invoke(rtt);
            }
        }
        else
        {
            ControlMessage?.Invoke(raw);
        }
    }

    private Task SendFrameAsync(byte tag, uint seq, CancellationToken ct)
    {
        var frame = new byte[5];
        frame[0] = tag;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), seq);
        return _control.SendAsync(frame, ct);
    }

    private void RaiseLost()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
            ConnectionLost?.Invoke();
    }
}
