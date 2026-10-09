namespace RemoteDesktop.Core;

public sealed class SessionInfo
{
    public SessionId SessionId { get; init; }
    public DeviceId RequesterDeviceId { get; init; }
    public DeviceId TargetDeviceId { get; init; }

    /// <summary>The requester's short ID, so the person being asked sees the number the requester knows.</summary>
    public string? RequesterShortId { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public SessionState State { get; set; } = SessionState.Created;
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Unknown;
    /// <summary>Set while State is Paused (a peer dropped); the session can be resumed until the recovery window ends.</summary>
    public DateTimeOffset? PausedAt { get; set; }
    public PermissionSet Permissions { get; init; } = PermissionSet.ViewOnly();

    /// <summary>Connection epoch: 0 at first, incremented on every resume. Session keys are derived per epoch.</summary>
    public int Epoch { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
