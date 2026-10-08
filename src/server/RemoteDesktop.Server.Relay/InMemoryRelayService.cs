using System.Collections.Concurrent;
using System.Net.WebSockets;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Relay;

public sealed class RelayOptions
{
    public TimeSpan PeerWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A relay with no traffic for this long is a zombie and is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
    /// <summary>Per-direction cap (Maximum Bandwidth); 0 = unlimited.</summary>
    public long MaxBytesPerSecond { get; init; } = 25L * 1024 * 1024;

    /// <summary>Relay connection attempts per client address per minute. Raise for sites where many users share one address (NAT).</summary>
    public int ConnectionsPerMinutePerAddress { get; init; } = 60;

    /// <summary>Unauthorized relay attempts per address per 10 minutes before that address is locked out.</summary>
    public int UnauthorizedAttemptsPerAddress { get; init; } = 10;

    /// <summary>Maximum Sessions on the relay: concurrent paired or waiting sessions.</summary>
    public int MaxConcurrentPairings { get; init; } = 10_000;

    /// <summary>Throws with every problem found, so a bad configuration fails at startup instead of misbehaving later.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (PeerWaitTimeout <= TimeSpan.Zero) errors.Add("Relay:PeerWaitTimeout must be positive.");
        if (IdleTimeout <= TimeSpan.Zero) errors.Add("Relay:IdleTimeout must be positive.");
        if (MaxMessageBytes < 1024) errors.Add("Relay:MaxMessageBytes must be at least 1024.");
        if (MaxBytesPerSecond < 0) errors.Add("Relay:MaxBytesPerSecond must not be negative (0 = unlimited).");
        if (ConnectionsPerMinutePerAddress < 1) errors.Add("Relay:ConnectionsPerMinutePerAddress must be at least 1.");
        if (UnauthorizedAttemptsPerAddress < 1) errors.Add("Relay:UnauthorizedAttemptsPerAddress must be at least 1.");
        if (MaxConcurrentPairings < 1) errors.Add("Relay:MaxConcurrentPairings must be at least 1.");
        OptionsValidation.ThrowIfAny(errors);
    }
}

/// <summary>
/// Forwards opaque frames between exactly two authorized peers of one session. It never decrypts anything
/// (the payload is end-to-end encrypted) and never logs payloads. Authorization: the session must exist and be
/// establishing/connected, the device must be its requester or target (each role may attach once), and the
/// token must be valid for that exact (session, device) and is consumed on use.
/// </summary>
public sealed class InMemoryRelayService : IRelayService
{
    private enum Role { Requester, Target }

    private readonly ISessionTokenService _tokens;
    private readonly ISessionManager _sessions;
    private readonly IAuditLog _audit;
    private readonly RelayOptions _opt;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<SessionId, Pairing> _pairings = new();

    public InMemoryRelayService(ISessionTokenService tokens, ISessionManager sessions, IAuditLog audit,
        RelayOptions? options = null, TimeProvider? clock = null)
    {
        _tokens = tokens;
        _sessions = sessions;
        _audit = audit;
        _opt = options ?? new RelayOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public int ActivePairings => _pairings.Count;

    public async Task<RelayOutcome> HandleConnectionAsync(WebSocket socket, SessionId sessionId,
        DeviceId deviceId, string sessionToken, CancellationToken ct = default)
    {
        // At capacity: refuse before looking at anything else, and before a token is used up.
        if (_pairings.Count >= _opt.MaxConcurrentPairings && !_pairings.ContainsKey(sessionId))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.InternalServerError, "Server busy");
            return RelayOutcome.Busy;
        }

        var session = await _sessions.GetAsync(sessionId, ct);
        var role = RoleOf(session, deviceId);

        // Every failure below is the same close: the caller learns nothing about which check failed.
        if (session is null || role is null
            || session.State is not (SessionState.EstablishingTransport or SessionState.Connected)
            || session.IsExpired
            || string.IsNullOrEmpty(sessionToken)
            || !_tokens.TryConsume(sessionToken, sessionId, deviceId))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Unauthorized");
            return RelayOutcome.Unauthorized;
        }

        // A pairing that was shut down (peer left, session closed) is replaced, so a legitimate reconnect with a
        // fresh token starts a clean relay instead of bouncing off the dead one.
        Pairing pairing;
        while (true)
        {
            pairing = _pairings.GetOrAdd(sessionId, _ => new Pairing());
            if (!pairing.Cancellation.IsCancellationRequested) break;
            _pairings.TryRemove(new KeyValuePair<SessionId, Pairing>(sessionId, pairing));
        }
        if (!pairing.TryAttach(role.Value, socket))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Unauthorized");
            return RelayOutcome.Unauthorized;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pairing.Cancellation.Token);
        var outcome = RelayOutcome.Completed;
        try
        {
            var peer = await pairing.WaitForPeerAsync(role.Value, _opt.PeerWaitTimeout, linked.Token);
            if (peer is null)
            {
                outcome = pairing.Cancellation.IsCancellationRequested ? RelayOutcome.Closed : RelayOutcome.PeerTimeout;
                await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure,
                    outcome == RelayOutcome.Closed ? "Session closed" : "Peer timeout");
                return outcome;
            }

            if (role == Role.Requester) // one audit record per relay, not one per side
                await WriteAuditAsync(AuditEventType.RelaySessionStarted, session, null);

            await ForwardAsync(socket, peer, linked.Token);
            if (pairing.Cancellation.IsCancellationRequested) outcome = RelayOutcome.Closed;
        }
        catch (OperationCanceledException) { outcome = RelayOutcome.Closed; }
        finally
        {
            // One side leaving ends the relay for both; nothing lingers.
            pairing.Cancellation.Cancel();
            pairing.Detach(role.Value, socket);
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "Relay closed");
            if (pairing.IsEmpty) _pairings.TryRemove(new KeyValuePair<SessionId, Pairing>(sessionId, pairing));
            if (role == Role.Requester) await WriteAuditAsync(AuditEventType.RelaySessionEnded, session, null);
        }
        return outcome;
    }

    public void CloseSession(SessionId sessionId)
    {
        if (_pairings.TryGetValue(sessionId, out var p)) p.Cancellation.Cancel();
    }

    private static Role? RoleOf(SessionInfo? s, DeviceId d) =>
        s is null ? null
        : d == s.RequesterDeviceId ? Role.Requester
        : d == s.TargetDeviceId ? Role.Target
        : null;

    private async Task ForwardAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        long messageBytes = 0;
        var window = _clock.GetUtcNow();
        long windowBytes = 0;

        while (from.State == WebSocketState.Open && to.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_opt.IdleTimeout);

            ValueWebSocketReceiveResult r;
            try { r = await from.ReceiveAsync(buf.AsMemory(), idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await CloseQuietlyAsync(from, WebSocketCloseStatus.NormalClosure, "Idle timeout");
                return;
            }

            if (r.MessageType == WebSocketMessageType.Close) return;
            if (r.MessageType != WebSocketMessageType.Binary)
            {
                await CloseQuietlyAsync(from, WebSocketCloseStatus.InvalidMessageType, "Binary only");
                return;
            }

            messageBytes += r.Count;
            if (messageBytes > _opt.MaxMessageBytes)
            {
                await CloseQuietlyAsync(from, WebSocketCloseStatus.MessageTooBig, "Message too large");
                return;
            }

            // Backpressure rather than loss: exceeding the cap just makes the sender wait.
            if (_opt.MaxBytesPerSecond > 0)
            {
                var now = _clock.GetUtcNow();
                if (now - window >= TimeSpan.FromSeconds(1)) { window = now; windowBytes = 0; }
                windowBytes += r.Count;
                if (windowBytes > _opt.MaxBytesPerSecond)
                {
                    var wait = window + TimeSpan.FromSeconds(1) - now;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    window = _clock.GetUtcNow();
                    windowBytes = r.Count;
                }
            }

            await to.SendAsync(buf.AsMemory(0, r.Count), WebSocketMessageType.Binary, r.EndOfMessage, ct);
            if (r.EndOfMessage) messageBytes = 0;
        }
    }

    private async Task WriteAuditAsync(AuditEventType type, SessionInfo session, string? reason)
    {
        try
        {
            await _audit.WriteAsync(new AuditEvent
            {
                ActorDeviceId = session.RequesterDeviceId,
                TargetDeviceId = session.TargetDeviceId,
                SessionId = session.SessionId,
                EventType = type,
                Success = true,
                Reason = reason
            });
        }
        catch (Exception) { /* auditing must not tear down the relay path */ }
    }

    private static async Task CloseQuietlyAsync(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try { await ws.CloseOutputAsync(status, reason, CancellationToken.None); }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>The two role slots of one session's relay.</summary>
    private sealed class Pairing
    {
        private readonly object _lock = new();
        private WebSocket? _requester;
        private WebSocket? _target;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancellation { get; } = new();

        public bool IsEmpty
        {
            get { lock (_lock) return _requester is null && _target is null; }
        }

        public bool TryAttach(Role role, WebSocket ws)
        {
            lock (_lock)
            {
                ref var slot = ref (role == Role.Requester ? ref _requester : ref _target);
                // A role can be taken once; only a dead socket may be replaced (reconnect after a drop).
                if (slot is { State: WebSocketState.Open }) return false;
                if (Cancellation.IsCancellationRequested) return false;
                slot = ws;
                if (_requester is not null && _target is not null) _both.TrySetResult();
                return true;
            }
        }

        public void Detach(Role role, WebSocket ws)
        {
            lock (_lock)
            {
                if (role == Role.Requester && ReferenceEquals(_requester, ws)) _requester = null;
                if (role == Role.Target && ReferenceEquals(_target, ws)) _target = null;
            }
        }

        public async Task<WebSocket?> WaitForPeerAsync(Role me, TimeSpan timeout, CancellationToken ct)
        {
            try { await _both.Task.WaitAsync(timeout, ct); }
            catch (TimeoutException) { return null; }
            catch (OperationCanceledException) when (Cancellation.IsCancellationRequested) { return null; }
            lock (_lock) return me == Role.Requester ? _target : _requester;
        }
    }
}
