namespace RemoteDesktop.FileTransfer;

public sealed class FileChunk
{
    public required Guid TransferId { get; init; }
    public required long SequenceNumber { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
    public bool IsLast { get; init; }
}
