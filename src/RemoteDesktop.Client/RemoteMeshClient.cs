using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Signaling;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client;

/// <summary>
/// The whole client in one object, with no UI: it owns this machine's identity, stays registered with the
/// server, answers incoming connection requests (through <see cref="IApprovalHandler"/>) and serves them as a
/// host, and connects out to other devices as a viewer. A UI or a background service drives it.
/// </summary>
public sealed class RemoteMeshClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly HostServices _host;
    private readonly IApprovalHandler? _approval;
    private readonly SignalingClient _signaling;
    private readonly ConcurrentDictionary<SessionId, SessionBase> _sessions = new();
    private readonly ConcurrentDictionary<SessionId, (DeviceId Id, string? ShortId)> _requesters = new();
    private PeerPinStore _pins = new();
    private IKeyStore? _keyStore;
    private int _approvalsOpen;
    private int _signalingGeneration; // bumped each time the link to the server was lost and came back
    private string? _shortId;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _background = new();
    private DeviceKeyPair? _keys;
    private DeviceIdentity? _identity;

    /// <param name="host">What this machine offers to viewers. Null = it cannot be controlled.</param>
    /// <param name="approval">Asked for every interactive request. Null = every such request is declined.</param>
    public RemoteMeshClient(ClientOptions options, HostServices? host = null, IApprovalHandler? approval = null)
    {
        _options = options;
        _host = host ?? new HostServices();
        _approval = approval;
        _signaling = new SignalingClient(options.ConfigureHub);
    }

    public DeviceId DeviceId => _identity?.DeviceId ?? throw new InvalidOperationException("Call StartAsync first.");
    public string DeviceName => _identity?.DeviceName ?? _options.DeviceName;

    /// <summary>
    /// The nine-digit number to tell other people (for example <c>482913057</c>); <c>ShortId.Format</c> groups it for
    /// display. The full <see cref="DeviceId"/> works too.
    /// </summary>
    public string ShortId => _shortId ?? throw new InvalidOperationException("Call StartAsync first.");
    public bool IsConnected => _signaling.IsConnected;

    /// <summary>Raised when this machine starts being controlled.</summary>
    public event Action<HostSession>? HostSessionStarted;

    /// <summary>Something went wrong with one session or request; the client itself keeps running.</summary>
    public event Action<string, Exception>? Error;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var keyStore = new OsKeyStore(_options.DataDirectory);
        var identityService = new DeviceIdentityService(keyStore, _options.DataDirectory);
        _identity = await identityService.LoadOrCreateAsync(_options.DeviceName, ct);
        _keys = await keyStore.LoadOrCreateAsync(_identity.DeviceId, ct);
        _pins = new PeerPinStore(Path.Combine(_options.DataDirectory ?? DeviceIdentityService.GetDefaultDirectory(), "known-peers.json"));

        // Resuming sessions after a reconnect is done here, together with rebuilding their connections.
        _signaling.ResumeSessionsAfterReconnect = false;
        _signaling.ConnectionRestored += () => Interlocked.Increment(ref _signalingGeneration);
        _signaling.SessionStateChanged += OnSessionStateChanged;

        _keyStore = keyStore;
        await _signaling.ConnectAsync(_options.HubUri, _identity.DeviceId, ct);
        await RegisterWithPendingKeyFirstAsync(ct);
        _shortId = await ClaimShortIdAsync(ct);

        _signaling.Authorized += auth => Spawn("host session", () => StartHostAsync(auth));
        _signaling.Established += auth => Spawn("host session", () => StartHostAsync(auth));
        _signaling.SessionEnded += OnSessionEnded;

        Spawn("incoming requests", ListenForRequestsAsync);
        Spawn("heartbeat", HeartbeatAsync);
    }

    /// <summary>A one-time code to show to the person who should be allowed in without an approval prompt.</summary>
    public Task<string> GenerateOtpAsync(CancellationToken ct = default) => _signaling.GenerateOtpAsync(ct);

    /// <summary>
    /// Connects to another device. With an <paramref name="otp"/> (read from that device's screen) no one has to
    /// approve; otherwise the person at the other device is asked, and a trusted device connects straight away.
    /// </summary>
    public Task<ViewerSession> ConnectAsync(
        DeviceId target, IFrameRenderer renderer, string? otp = null, TimeSpan? approvalTimeout = null, CancellationToken cancellationToken = default) =>
        ConnectToAsync(target.ToString(), renderer, otp, approvalTimeout, cancellationToken);

    /// <summary>
    /// Connects to whatever the user typed: a short ID (<c>482 913 057</c>) or a full ID. Anything else is a mistake
    /// in the caller (check with <see cref="ConnectionAddress.TryNormalize"/> first).
    /// </summary>
    public Task<ViewerSession> ConnectAsync(
        string address, IFrameRenderer renderer, string? otp = null, TimeSpan? approvalTimeout = null, CancellationToken cancellationToken = default)
    {
        if (!ConnectionAddress.TryNormalize(address, out var normalized))
            throw new ArgumentException("Not a short ID or a device ID.", nameof(address));
        return ConnectToAsync(normalized, renderer, otp, approvalTimeout, cancellationToken);
    }

    private async Task<ViewerSession> ConnectToAsync(
        string target, IFrameRenderer renderer, string? otp = null, TimeSpan? approvalTimeout = null, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        // Closing the client abandons any connection attempt still waiting for the other side.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var ct = linked.Token;
        var approved = new TaskCompletionSource<AuthResultDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnApproved(AuthResultDto a) => approved.TrySetResult(a);
        _signaling.Approved += OnApproved;
        try
        {
            AuthResultDto auth;
            if (otp is not null)
            {
                auth = await _signaling.RequestSessionWithOtpAsync(target, otp, ct);
            }
            else
            {
                var (session, immediate) = await _signaling.RequestSessionWithAuthAsync(target, ct);
                if (immediate is not null) auth = immediate; // trusted device: no prompt
                else
                {
                    // Wait for the person at the other device. Decline/timeouts surface as SessionEnded or a timeout.
                    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnEnded(SessionId id, string _) { if (id == session.SessionId) ended.TrySetResult(); }
                    // A decline arrives as a state change (Failed), not as a close.
                    void OnState(SessionInfo s)
                    {
                        if (s.SessionId == session.SessionId && s.State is SessionState.Failed or SessionState.Expired or SessionState.Disconnected)
                            ended.TrySetResult();
                    }
                    _signaling.SessionEnded += OnEnded;
                    _signaling.SessionStateChanged += OnState;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(approvalTimeout ?? TimeSpan.FromSeconds(60));
                        var winner = await Task.WhenAny(approved.Task, ended.Task, Task.Delay(Timeout.Infinite, timeout.Token));
                        if (winner != approved.Task) throw new ConnectionRefusedException("The other device did not accept the connection.");
                        auth = approved.Task.Result;
                    }
                    finally
                    {
                        _signaling.SessionEnded -= OnEnded;
                        _signaling.SessionStateChanged -= OnState;
                    }
                }
            }

            var sessionId = SessionId.Parse(auth.SessionId);
            var granted = (Permission)auth.GrantedPermissions;
            var endpoint = await OpenEndpointAsync(sessionId, auth.SessionToken, initiator: true, ct);

            var viewer = new ViewerSession(sessionId, endpoint, _options, granted, renderer, CloseOnServerAsync);
            Track(viewer);
            viewer.Start();
            await _signaling.ReportConnectionModeAsync(sessionId, ConnectionMode.Relay, ct);
            return viewer;
        }
        catch (Microsoft.AspNetCore.SignalR.HubException)
        {
            // The server's wording is deliberately vague (it must not reveal whether a device exists).
            throw new ConnectionRefusedException("The connection was refused.");
        }
        finally { _signaling.Approved -= OnApproved; }
    }

    // ── Hosting ──────────────────────────────────────────────────────────

    private async Task ListenForRequestsAsync()
    {
        await foreach (var request in _signaling.IncomingSessionRequestsAsync(_cts.Token))
        {
            // Each request is answered independently so one slow prompt does not block the next.
            var r = request;
            Spawn("approval", () => AnswerAsync(r));
        }
    }

    private async Task AnswerAsync(SessionInfo request)
    {
        // If the requester gives up (or disconnects) while the person is still deciding, the question is withdrawn.
        using var withdrawn = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        void OnEnded(SessionId id, string _) { if (id == request.SessionId) withdrawn.Cancel(); }
        void OnState(SessionInfo s)
        {
            if (s.SessionId == request.SessionId && s.State is SessionState.Failed or SessionState.Expired or SessionState.Disconnected)
                withdrawn.Cancel();
        }
        _signaling.SessionEnded += OnEnded;
        _signaling.SessionStateChanged += OnState;
        Interlocked.Increment(ref _approvalsOpen); // input from connected viewers is held while this question is open
        try
        {
            var decision = ApprovalDecision.Deny; // secure default: no handler, no access
            if (_approval is not null)
            {
                try { decision = await _approval.DecideAsync(new ApprovalRequest(request.SessionId, request.RequesterDeviceId, request.RequesterShortId), withdrawn.Token); }
                catch (OperationCanceledException) { return; } // withdrawn or shutting down: nothing to answer
                catch (Exception ex) { Error?.Invoke("approval", ex); }
            }
            if (withdrawn.IsCancellationRequested) return;
            await RespondAsync(request, decision);
        }
        finally
        {
            Interlocked.Decrement(ref _approvalsOpen);
            _signaling.SessionEnded -= OnEnded;
            _signaling.SessionStateChanged -= OnState;
        }
    }

    private async Task RespondAsync(SessionInfo request, ApprovalDecision decision)
    {
        // Never grant more than a single dialog could have meant: nothing at all is not a valid grant.
        var accept = decision.Accept && decision.Granted != Permission.None;
        if (accept) _requesters[request.SessionId] = (request.RequesterDeviceId, request.RequesterShortId);
        try
        {
            await _signaling.RespondToSessionAsync(request.SessionId, accept, accept ? decision.Granted : Permission.None, _cts.Token);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException)
        {
            // The request was withdrawn or timed out on the server in the moment between asking and answering.
            _requesters.TryRemove(request.SessionId, out _);
        }
    }

    private async Task StartHostAsync(AuthResultDto auth)
    {
        var sessionId = SessionId.Parse(auth.SessionId);
        var granted = (Permission)auth.GrantedPermissions;
        var endpoint = await OpenEndpointAsync(sessionId, auth.SessionToken, initiator: false, _cts.Token);

        DeviceId? peer = null;
        string? peerShort = null;
        if (_requesters.TryRemove(sessionId, out var requester)) (peer, peerShort) = (requester.Id, requester.ShortId);
        var host = new HostSession(sessionId, endpoint, _options, granted, peer, peerShort, () => Volatile.Read(ref _approvalsOpen) > 0, CloseOnServerAsync);
        Track(host);
        host.Start(_host);
        HostSessionStarted?.Invoke(host);
    }

    /// <summary>
    /// Opens the encrypted connection after checking the peer's key against the one pinned here. If the key changed,
    /// the session is closed on the server too, so the other side is not left waiting, and the error says why.
    /// </summary>
    private async Task<SessionEndpoint> OpenEndpointAsync(SessionId sessionId, string relayToken, bool initiator, CancellationToken ct)
    {
        try { return await SessionEndpoint.OpenAsync(_options, _signaling, _keys!, DeviceId, _pins, sessionId, relayToken, initiator, ct); }
        catch (PeerKeyChangedException)
        {
            try { await _signaling.CloseSessionAsync(sessionId, "security key changed", CancellationToken.None); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// Forgets what this machine knows about a device, after the person decided its new key is legitimate (for
    /// example it was reinstalled). The next connection pins the key it presents then.
    /// </summary>
    public bool ForgetPeer(DeviceId peer) => _pins.Forget(peer);

    // ── Changing this device's key ───────────────────────────────────────

    /// <summary>
    /// Moves this device to a new key pair (after a suspected leak, or as housekeeping). Both keys sign the change, so
    /// devices that know the old key can follow to the new one by themselves; nobody has to be told, and the server
    /// cannot forge it. Not possible while a session is open (its keys come from the old one).
    /// </summary>
    public async Task RotateKeyAsync(CancellationToken ct = default)
    {
        if (_keys is null || _identity is null || _keyStore is null) throw new InvalidOperationException("Call StartAsync first.");
        if (!_sessions.IsEmpty) throw new InvalidOperationException("Close the open sessions before changing the key.");

        var next = DeviceKeyGenerator.Generate();
        await _keyStore.StagePendingAsync(_identity.DeviceId, next, ct);   // saved first: a crash from here on is recoverable
        try { await _signaling.RotateDeviceKeyAsync(_keys, next, ct); }
        catch (Microsoft.AspNetCore.SignalR.HubException)
        {
            await _keyStore.DiscardPendingAsync(_identity.DeviceId, ct);   // the server said no: nothing changed
            throw new InvalidOperationException("The key could not be changed.");
        }
        // Any other failure (the connection died mid-call) leaves the staged key; the next start settles which one the server has.

        await _keyStore.CommitPendingAsync(_identity.DeviceId, ct);
        _keys = next;
    }

    /// <summary>
    /// Starts with the staged key if one was left behind (a change that was interrupted): if the server knows it, it
    /// becomes the key; if not, the current key is used and the staged one thrown away.
    /// </summary>
    private async Task RegisterWithPendingKeyFirstAsync(CancellationToken ct)
    {
        var id = _identity!.DeviceId;
        var pending = await _keyStore!.LoadPendingAsync(id, ct);
        if (pending is not null)
        {
            try
            {
                await _signaling.RegisterDeviceAsync(_identity.ToDeviceInfo(), new DeviceKeyPairSigner(pending), ct);
                await _keyStore.CommitPendingAsync(id, ct);
                _keys = pending;
                return;
            }
            catch (Microsoft.AspNetCore.SignalR.HubException) { await _keyStore.DiscardPendingAsync(id, ct); }
        }
        await _signaling.RegisterDeviceAsync(_identity.ToDeviceInfo(), new DeviceKeyPairSigner(_keys!), ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private void Track(SessionBase session)
    {
        session.SignalingGeneration = Volatile.Read(ref _signalingGeneration);
        _sessions[session.SessionId] = session;
        session.Ended += reason => _sessions.TryRemove(session.SessionId, out _);
        session.TransportLost += () => Spawn("reconnect", () => RecoverAsync(session));
    }

    // ── Reconnecting ─────────────────────────────────────────────────────

    /// <summary>
    /// What the server tells us about a session we are in: the other side dropped (it will come back and tell us), or
    /// it came back and the session has a new key epoch that this side must rebuild its connection for.
    /// </summary>
    private void OnSessionStateChanged(SessionInfo info)
    {
        if (!_sessions.TryGetValue(info.SessionId, out var session)) return;
        if (info.State == SessionState.Paused)
        {
            session.PeerPaused = true;
        }
        else if (info.State == SessionState.Connected && info.Epoch > session.Epoch)
        {
            session.NoteEpoch(info.Epoch);
            session.BeginRecovery(); // no-op if already rebuilding; that loop picks the new epoch up
        }
    }

    /// <summary>
    /// Brings a session's connection back after it dropped, or ends the session if it cannot. Three ways in:
    /// the other side already resumed (follow its new epoch), the other side is down (wait for it), or it is us who
    /// dropped / nothing is heard (resume with our recovery token, which also starts a new epoch).
    /// </summary>
    private async Task RecoverAsync(SessionBase session)
    {
        var lostAt = DateTimeOffset.UtcNow;
        var grace = session is ViewerSession ? _options.ReconnectGrace : _options.ReconnectGrace * 2;
        var backoff = TimeSpan.FromSeconds(1);
        var refusals = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime, _cts.Token);
        limit.CancelAfter(_options.ReconnectTimeout);
        var ct = limit.Token;

        void Backoff() => backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(10).Ticks));

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (session.TargetEpoch > session.Epoch)
                {
                    try
                    {
                        await session.ReattachAsync(session.TargetEpoch, ct);
                        session.SignalingGeneration = Volatile.Read(ref _signalingGeneration);
                        if (session.TargetEpoch <= session.Epoch) return; // back, and nothing newer announced meanwhile
                        session.BeginRecovery();                          // a newer epoch arrived while rebuilding: go again
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(backoff, ct);
                        Backoff();
                    }
                    continue;
                }

                var weDropped = session.SignalingGeneration != Volatile.Read(ref _signalingGeneration);
                if (!_signaling.IsConnected) { await Task.Delay(300, ct); continue; }

                // If the other side is the one that dropped, it resumes and tells us; resuming from here as well would
                // only start a second epoch. (If we lost the server ourselves we may have missed that notice: resume.)
                if (!weDropped && session.PeerPaused) { await session.WaitForTargetEpochAsync(TimeSpan.FromMilliseconds(500), ct); continue; }

                // Nobody has said anything: give the other side a moment to do it, then do it ourselves.
                if (!weDropped && DateTimeOffset.UtcNow - lostAt < grace)
                {
                    await session.WaitForTargetEpochAsync(TimeSpan.FromMilliseconds(200), ct);
                    continue;
                }

                try
                {
                    var resumed = await _signaling.ResumeSessionAsync(session.SessionId, ct);
                    session.NoteEpoch(resumed.Epoch);
                    refusals = 0;
                }
                catch (Microsoft.AspNetCore.SignalR.HubException) when (++refusals >= 3)
                {
                    // The server will not take this session back (it expired, was closed, or does not know it any more).
                    await session.EndAsync("could not reconnect", notifyServer: false);
                    return;
                }
                catch (InvalidOperationException)
                {
                    await session.EndAsync("could not reconnect", notifyServer: false); // no recovery token: nothing to resume with
                    return;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    await Task.Delay(backoff, ct);
                    Backoff();
                }
            }
        }
        catch (OperationCanceledException) when (!session.IsEnded && !_cts.IsCancellationRequested)
        {
            await session.EndAsync("could not reconnect", notifyServer: true); // out of time
        }
        catch (OperationCanceledException) { }
    }

    private void OnSessionEnded(SessionId id, string reason)
    {
        // The peer or the server ended it: tear down locally without telling the server again.
        if (_sessions.TryGetValue(id, out var s)) _ = s.EndAsync(reason, notifyServer: false);
    }

    private Task CloseOnServerAsync(SessionId id, string reason) => _signaling.CloseSessionAsync(id, reason);

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(_cts.Token))
            try { await _signaling.HeartbeatAsync(_cts.Token); } catch (Exception) when (!_cts.IsCancellationRequested) { }
    }

    private void Spawn(string name, Func<Task> work) => _background.Add(Task.Run(async () =>
    {
        try { await work(); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { Error?.Invoke(name, ex); }
    }));

    private void EnsureStarted()
    {
        if (_identity is null) throw new InvalidOperationException("Call StartAsync first.");
    }

    /// <summary>
    /// Asks for the number this device had last time (kept in its data folder) so people who know it can still
    /// reach it; remembers whatever the server answered.
    /// </summary>
    private async Task<string> ClaimShortIdAsync(CancellationToken ct)
    {
        var file = Path.Combine(_options.DataDirectory ?? DeviceIdentityService.GetDefaultDirectory(), "short.id");
        string? previous = null;
        try { if (File.Exists(file)) previous = (await File.ReadAllTextAsync(file, ct)).Trim(); }
        catch (IOException) { }

        var granted = await _signaling.ClaimShortIdAsync(previous, ct);
        if (granted != previous)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, granted, ct);
            }
            catch (IOException) { /* only convenience: without it the device just gets a new number next time */ }
        }
        return granted;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _sessions.Values.ToArray()) await s.DisposeAsync();
        await _signaling.DisposeAsync();
        try { await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _cts.Dispose();
    }
}

/// <summary>The other device (or the server) said no, or never answered.</summary>
public sealed class ConnectionRefusedException(string message) : Exception(message);
