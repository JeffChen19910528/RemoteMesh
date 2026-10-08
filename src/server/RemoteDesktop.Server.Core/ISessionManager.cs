using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public interface ISessionManager
{
    Task<SessionInfo> CreateAsync(DeviceId requester, DeviceId target, CancellationToken ct = default);
    Task<SessionInfo?> GetAsync(SessionId sessionId, CancellationToken ct = default);
    Task UpdateStateAsync(SessionId sessionId, SessionState newState, CancellationToken ct = default);
    Task TerminateAsync(SessionId sessionId, string? reason = null, CancellationToken ct = default);
    Task<IReadOnlyList<SessionInfo>> GetActiveSessionsAsync(DeviceId deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionInfo>> GetSessionsByStateAsync(SessionState state, CancellationToken ct = default);

    /// <summary>Sessions that are neither finished nor past their expiry (Maximum Sessions accounting).</summary>
    Task<int> CountActiveAsync(CancellationToken ct = default);

    /// <summary>Frees finished sessions created before <paramref name="createdBefore"/>; returns how many were removed.</summary>
    Task<int> PurgeFinishedAsync(DateTimeOffset createdBefore, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-set on the session state. Authorization decisions (accept/reject/expire/connect) use this so
    /// two racing requests cannot both succeed and a closed or expired session can never be revived.
    /// </summary>
    Task<bool> TryTransitionAsync(SessionId sessionId, SessionState expected, SessionState next,
        CancellationToken ct = default);

    // Atomic recovery transitions: pause/resume/expire race with each other, so each is decided under one lock.
    Task<bool> TryPauseAsync(SessionId sessionId, DateTimeOffset now, CancellationToken ct = default);
    Task<ResumeResult> TryResumeAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window, CancellationToken ct = default);
    Task<bool> TryExpireIfPausedAsync(SessionId sessionId, DateTimeOffset now, TimeSpan window, CancellationToken ct = default);
}

public enum ResumeResult
{
    Resumed,
    AlreadyConnected,
    NotResumable,    // wrong state (never connected, closed, failed, ...)
    WindowExpired    // was paused too long; the session has been moved to Expired
}
