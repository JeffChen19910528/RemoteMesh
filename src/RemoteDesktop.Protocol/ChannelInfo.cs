namespace RemoteDesktop.Protocol;

public sealed class ChannelInfo
{
    public required Guid ChannelId { get; init; }
    public required ChannelType Type { get; init; }
    public int Priority { get; init; }
    public bool Reliable { get; init; } = true;
}
