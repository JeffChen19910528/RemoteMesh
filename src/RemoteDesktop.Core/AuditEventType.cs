namespace RemoteDesktop.Core;

public enum AuditEventType
{
    RemoteSessionRequested,
    RemoteSessionApproved,
    RemoteSessionRejected,
    RemoteSessionStarted,
    RemoteSessionTerminated,
    AuthenticationSuccess,
    AuthenticationFailure,
    FileTransferStarted,
    FileTransferCompleted,
    FileTransferCancelled,
    PermissionGranted,
    PermissionRevoked,
    RelaySessionStarted,
    RelaySessionEnded,
    RateLimitTriggered,
    DeviceRegistered,
    TrustedDeviceAdded,
    TrustedDeviceRemoved,
    RemoteSessionPaused,
    RemoteSessionResumed,
    RemoteSessionExpired
}
