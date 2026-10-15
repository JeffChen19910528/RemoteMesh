namespace RemoteDesktop.FileTransfer;

public enum TransferState { Pending, Transferring, Paused, Completed, Cancelled, Failed }

public sealed class TransferProgress
{
    public required Guid TransferId { get; init; }
    public required string FileName { get; init; }
    public required long TotalBytes { get; init; }
    public long TransferredBytes { get; set; }
    public TransferState State { get; set; } = TransferState.Pending;
    public double ProgressPercent => TotalBytes == 0 ? 0 : (double)TransferredBytes / TotalBytes * 100;
}
