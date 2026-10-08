namespace RemoteDesktop.Protocol;

public enum MessageType
{
    // Signaling
    Hello,
    HelloAck,
    SessionRequest,
    SessionResponse,
    SessionClose,

    // NAT / ICE
    IceCandidate,
    IceDone,

    // Auth
    AuthChallenge,
    AuthResponse,
    AuthResult,

    // Control
    Heartbeat,
    HeartbeatAck,
    Disconnect,

    // Desktop
    VideoFrame,
    VideoConfig,

    // Input
    InputEvent,

    // Clipboard
    ClipboardData,

    // File
    FileTransferRequest,
    FileTransferAccept,
    FileTransferReject,
    FileChunk,
    FileTransferComplete,
    FileTransferCancel,

    // Relay
    RelayBind,
    RelayForward,

    // Diagnostics
    DiagnosticsReport
}
