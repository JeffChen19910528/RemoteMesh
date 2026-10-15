using RemoteDesktop.Core;

namespace RemoteDesktop.Protocol;

public abstract class MessageBase
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public abstract MessageType Type { get; }
    public string ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public SessionId? SessionId { get; init; }
}
