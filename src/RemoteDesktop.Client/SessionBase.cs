using RemoteDesktop.Core;
using RemoteDesktop.Transport.Reliability;

namespace RemoteDesktop.Client;

/// <summary>Lifetime shared by both ends of a session: background loops, liveness, and an idempotent shutdown.</summary>
public abstract class SessionBase : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _loops = new();
    private readonly Func<SessionId, string, Task>? _closeOnServer;
    private int _ended;
    private int _recovering;
    private int _targetEpoch;
    private Task? _keepAliveLoop;
    private readonly SemaphoreSlim _reattachLock = new(1, 1);
    private TaskCompletionSource _targetChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SessionEndpoint Endpoint { get; }

    /// <summary>The device on the other end.</summary>
    public DeviceId PeerDeviceId => Endpoint.PeerDeviceId;

    /// <summary>The same six groups of digits on both screens; reading them to each other rules out a man in the middle.</summary>
    public string SecurityCode => Endpoint.SecurityCode;

    /// <summary>True when this machine has never talked to this device before (so there was nothing to compare its key with).</summary>
    public bool PeerIsNew => Endpoint.PeerIsNew;

    /// <summary>The device moved to a new key since the last time and proved it with both keys; the security code is new.</summary>
    public bool PeerKeyRotated => Endpoint.PeerKeyRotated;

    /// <summary>True once the person confirmed the security code for this device's key.</summary>
    public bool PeerVerified => Endpoint.PeerVerified;

    /// <summary>Remember that the person compared the security code and it matched.</summary>
    public bool MarkPeerVerified() => Endpoint.MarkPeerVerified();
    internal KeepAliveService KeepAlive { get; }
    protected CancellationToken Token => _cts.Token;

    public SessionId SessionId { get; }

    /// <summary>True while the connection is down and being rebuilt; the session is still alive.</summary>
    public bool IsReconnecting => Volatile.Read(ref _recovering) == 1;

    /// <summary>The connection dropped and is being rebuilt. The session is not over.</summary>
    public event Action? Reconnecting;

    /// <summary>The connection was rebuilt (with a new key) and the session carries on.</summary>
    public event Action? Reconnected;

    /// <summary>Raised once per outage when the connection is lost; the client answers by rebuilding it.</summary>
    internal event Action? TransportLost;

    internal bool IsEnded => Volatile.Read(ref _ended) == 1;
    internal CancellationToken Lifetime => _cts.Token;

    /// <summary>The key epoch of the connection in use.</summary>
    internal int Epoch => Endpoint.Epoch;

    /// <summary>Which signaling connection this session last attached under (see <c>RemoteMeshClient</c>).</summary>
    internal int SignalingGeneration { get; set; }

    /// <summary>The other side said it dropped and is coming back; this side waits for it instead of resuming.</summary>
    internal bool PeerPaused { get; set; }

    /// <summary>The newest epoch known to be wanted (announced by the peer, or obtained by our own resume).</summary>
    internal int TargetEpoch => Volatile.Read(ref _targetEpoch);

    /// <summary>Raised once, with the reason, when the session is over (either side, or the connection dropped).</summary>
    public event Action<string>? Ended;

    /// <summary>Completes with the end reason.</summary>
    public Task<string> Completion => _completion.Task;

    /// <summary>The latest measured round-trip time over the relay, if any.</summary>
    public TimeSpan? Rtt => KeepAlive.LastRtt;

    internal SessionBase(SessionId sessionId, SessionEndpoint endpoint, ClientOptions options,
        Func<SessionId, string, Task>? closeOnServer)
    {
        SessionId = sessionId;
        Endpoint = endpoint;
        _closeOnServer = closeOnServer;
        KeepAlive = new KeepAliveService(endpoint[RemoteDesktop.Protocol.ChannelType.Control], new KeepAliveOptions
        {
            Interval = options.KeepAliveInterval,
            Timeout = options.KeepAliveTimeout
        });
        KeepAlive.ConnectionLost += OnTransportLost;
    }

    /// <summary>Starts a background loop. An unexpected failure ends the session instead of vanishing silently.</summary>
    protected void Run(string name, Func<CancellationToken, Task> body, bool endsSessionWhenDone = false, Action<Task>? onStarted = null)
    {
        var loop = Task.Run(async () =>
        {
            try
            {
                await body(_cts.Token);
                if (endsSessionWhenDone && !_cts.IsCancellationRequested)
                    await EndAsync($"{name} stopped", notifyServer: true);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // The message is a type name, not a payload: never put session data in a reason string.
                await EndAsync($"{name} failed ({ex.GetType().Name})", notifyServer: true);
            }
        });
        lock (_loops) _loops.Add(loop);
        onStarted?.Invoke(loop);
    }

    protected void StartKeepAliveAndWatchdog()
    {
        StartKeepAlive();
        Run("connection watchdog", async ct =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(ct))
                if (!Endpoint.IsConnected) OnTransportLost();
        });
    }

    private void StartKeepAlive() =>
        Run("keep-alive", ct => KeepAlive.RunAsync(ct), onStarted: t => _keepAliveLoop = t);

    /// <summary>
    /// The connection is gone (the socket closed, or the peer went silent). The session itself is not over: the client
    /// tries to rebuild the connection, and ends the session only if it cannot.
    /// </summary>
    private void OnTransportLost() => BeginRecovery();

    /// <summary>Marks the connection as down (once per outage) and asks the client to rebuild it.</summary>
    internal void BeginRecovery()
    {
        if (IsEnded || Interlocked.Exchange(ref _recovering, 1) == 1) return;
        Reconnecting?.Invoke();
        TransportLost?.Invoke();
    }

    /// <summary>The peer (or the server) announced a connection epoch newer than ours.</summary>
    internal void NoteEpoch(int epoch)
    {
        int seen;
        do { seen = Volatile.Read(ref _targetEpoch); if (epoch <= seen) return; }
        while (Interlocked.CompareExchange(ref _targetEpoch, epoch, seen) != seen);
        PeerPaused = false;
        var signal = Interlocked.Exchange(ref _targetChanged, new(TaskCreationOptions.RunContinuationsAsynchronously));
        signal.TrySetResult();
    }

    /// <summary>Waits until a newer epoch is announced, or the time is up. Returns whether one was.</summary>
    internal async Task<bool> WaitForTargetEpochAsync(TimeSpan timeout, CancellationToken ct)
    {
        var signal = Volatile.Read(ref _targetChanged).Task;
        if (TargetEpoch > Epoch) return true;
        try { await signal.WaitAsync(timeout, ct); return true; }
        catch (TimeoutException) { return TargetEpoch > Epoch; }
    }

    /// <summary>Rebuilds the connection at <paramref name="epoch"/>. Throws if that fails (the caller retries).</summary>
    internal async Task ReattachAsync(int epoch, CancellationToken ct)
    {
        if (IsEnded) return;
        await _reattachLock.WaitAsync(ct);
        try
        {
            await Endpoint.ReattachAsync(epoch, ct); // ignores an epoch it is already at or past
        }
        finally { _reattachLock.Release(); }
        if (IsEnded) return;

        KeepAlive.Reset();
        if (_keepAliveLoop is null or { IsCompleted: true }) StartKeepAlive();
        try { OnReattached(); } catch (Exception) { }
        Interlocked.Exchange(ref _recovering, 0);
        Reconnected?.Invoke();
    }

    /// <summary>Called after the connection was rebuilt: whatever was in flight is gone, so resynchronise.</summary>
    protected virtual void OnReattached() { }

    /// <summary>Ends the session once. <paramref name="notifyServer"/> tells the server (and so the peer).</summary>
    internal async Task EndAsync(string reason, bool notifyServer)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;

        _cts.Cancel();
        if (notifyServer && _closeOnServer is not null)
        {
            try { await _closeOnServer(SessionId, reason); }
            catch (Exception) { /* the server may be unreachable; the session is ending anyway */ }
        }

        try { await OnEndingAsync(); } catch (Exception) { }
        await Endpoint.DisposeAsync();
        _completion.TrySetResult(reason);
        Ended?.Invoke(reason);
    }

    protected virtual Task OnEndingAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await EndAsync("closed locally", notifyServer: true);
        Task[] loops;
        lock (_loops) loops = _loops.ToArray();
        try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _cts.Dispose();
    }
}
