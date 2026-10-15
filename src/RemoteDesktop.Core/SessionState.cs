namespace RemoteDesktop.Core;

public enum SessionState
{
    Created,
    Negotiating,
    Authenticating,
    Authorizing,
    EstablishingTransport,
    Connected,
    Paused,
    Disconnecting,
    Disconnected,
    Failed,
    Expired
}
