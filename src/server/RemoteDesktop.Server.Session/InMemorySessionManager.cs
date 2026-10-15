using System.Collections.Concurrent;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Server.Session;

public sealed class InMemorySessionManager : ISessionManager
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(8);

    private readonly ConcurrentDictionary<SessionId, SessionInfo> _sessions = new();
    private readonly TimeProvider _clock;

    public InMemorySessionManager(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task<SessionInfo> CreateAsync(DeviceId requester, DeviceId target, CancellationToken ct = default)
    {
        var session = new SessionInfo
        {
            SessionId = SessionId.New(),
            RequesterDeviceId = requester,
            TargetDeviceId = target,
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().Add(DefaultExpiry),
            State = SessionState.Created
        };
        _sessions[session.SessionId] = session;
        return Task.FromResult(session);
    }

    public Task<SessionInfo?> GetAsync(SessionId sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task UpdateStateAsync(SessionId sessionId, SessionState newState, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.State = newState;
        return Task.CompletedTask;
    }

    public Task TerminateAsync(SessionId sessionId, string? reason = null, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.State = SessionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionInfo>> GetActiveSessionsAsync(DeviceId deviceId, CancellationToken ct = default)
    {
        var active = _sessions.Values
            .Where(s => (s.RequesterDeviceId == deviceId || s.TargetDeviceId == deviceId)
                        && s.State != SessionState.Disconnected
                        && s.State != SessionState.Failed
                        && s.State != SessionState.Expired
                        && !s.IsExpired)
            .ToList();

        return Task.FromResult<IReadOnlyList<SessionInfo>>(active);
    }

    public Task<IReadOnlyList<SessionInfo>> GetSessionsByStateAsync(SessionState state, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionInfo>>(_sessions.Values.Where(s => s.State == state).ToList());

    private static bool IsFinished(SessionState s) =>
        s is SessionState.Disconnected or SessionState.Failed or SessionState.Expired;

    public Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return Task.FromResult(_sessions.Values.Count(s => !IsFinished(s.State) && s.ExpiresAt > now));
    }

    public Task<int> PurgeFinishedAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var (id, s) in _sessions)
            if (IsFinished(s.State) && s.CreatedAt < createdBefore && _sessions.TryRemove(id, out _)) removed++;
        return Task.FromResult(removed);
    }

    private readonly object _recoveryLock = new();

    public Task<bool> TryTransitionAsync(SessionId sessionId, SessionState expected, SessionState next,
        CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.State != expected)
                return Task.FromResult(false);
            s.State = next;
            if (next != SessionState.Paused) s.PausedAt = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryPauseAsync(SessionId sessionId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.State != SessionState.Connected)
                return Task.FromResult(false);
            s.State = SessionState.Paused;
            s.PausedAt = now;
            return Task.FromResult(true);
        }
    }

    public Task<ResumeResult> TryResumeAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window,
        CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.IsExpired && s.State != SessionState.Paused)
                return Task.FromResult(ResumeResult.NotResumable);
            // A Connected session can be resumed too: the device lost its relay connection but the server never saw
            // it leave (or it came back before the server noticed). It gets a new epoch, hence a new key, either way.
            if (s.State == SessionState.Connected)
            {
                s.Epoch++;
                return Task.FromResult(ResumeResult.Resumed);
            }
            if (s.State != SessionState.Paused) return Task.FromResult(ResumeResult.NotResumable);

            if (s.IsExpired || now - (s.PausedAt ?? s.CreatedAt) > window)
            {
                s.State = SessionState.Expired;
                s.PausedAt = null;
                return Task.FromResult(ResumeResult.WindowExpired);
            }
            s.State = SessionState.Connected;
            s.PausedAt = null;
            s.Epoch++; // a new connection epoch: both sides derive a fresh session key
            return Task.FromResult(ResumeResult.Resumed);
        }
    }

    public Task<bool> TryExpireIfPausedAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window,
        CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.State != SessionState.Paused)
                return Task.FromResult(false);
            if (!s.IsExpired && now - (s.PausedAt ?? s.CreatedAt) <= window)
                return Task.FromResult(false);
            s.State = SessionState.Expired;
            s.PausedAt = null;
            return Task.FromResult(true);
        }
    }

    public Task SaveAsync(SessionId sessionId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Puts a session back after a restart (see the persistent manager).</summary>
    public void Restore(SessionInfo session) => _sessions[session.SessionId] = session;

    public int Count => _sessions.Count;
}
