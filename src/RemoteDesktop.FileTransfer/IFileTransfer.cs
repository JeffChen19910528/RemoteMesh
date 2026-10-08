namespace RemoteDesktop.FileTransfer;

public interface IFileTransfer
{
    Task<TransferProgress> SendFileAsync(string localPath, string remoteDestination, CancellationToken ct = default);
    Task<TransferProgress> ReceiveFileAsync(Guid transferId, string localDestination, CancellationToken ct = default);
    Task CancelAsync(Guid transferId, CancellationToken ct = default);
    IAsyncEnumerable<TransferProgress> ProgressUpdatesAsync(CancellationToken ct = default);
}
