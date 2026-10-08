using System.Security.Cryptography;
using System.Threading.Channels;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.FileTransfer;

public sealed record FileTransferOutcome(TransferProgress Progress, FileTransferResult Result)
{
    public bool Ok => Result == FileTransferResult.Ok;
}

public sealed class FileTransferClientOptions
{
    public int ChunkSize { get; init; } = FileMessage.MaxChunkSize;
    /// <summary>Max unacknowledged upload chunks (flow control).</summary>
    public int Window { get; init; } = 16;
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Viewer side. One transfer at a time. Files are streamed in chunks (never fully loaded in memory) and
/// verified end-to-end with SHA-256. Interrupted transfers resume from the partial data on the next call;
/// an explicit cancel discards the partial data.
/// </summary>
public sealed class FileTransferClient
{
    private sealed class Failure(FileTransferResult result) : Exception(result.ToString())
    {
        public FileTransferResult Result { get; } = result;
    }

    private readonly IChannel _channel;
    private readonly FileTransferClientOptions _opt;
    private readonly Channel<FileMessage> _inbox = Channel.CreateBounded<FileMessage>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly SemaphoreSlim _single = new(1, 1);

    public FileTransferClient(IChannel channel, FileTransferClientOptions? options = null)
    {
        if (channel.Type != ChannelType.File)
            throw new ArgumentException("File transfer must use the File channel.", nameof(channel));
        _channel = channel;
        _opt = options ?? new FileTransferClientOptions();
    }

    /// <summary>Pumps host messages; run for the lifetime of the session.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
                if (FileMessage.TryParse(raw.Span, out var msg) && msg is not null)
                    await _inbox.Writer.WriteAsync(msg, ct);
        }
        catch (OperationCanceledException) { }
        finally { _inbox.Writer.TryComplete(); }
    }

    // ── Upload ───────────────────────────────────────────────────────────

    public async Task<FileTransferOutcome> UploadAsync(string localPath, string remoteName,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await _single.WaitAsync(ct);
        var id = Guid.NewGuid();
        var state = new TransferProgress { TransferId = id, FileName = remoteName, TotalBytes = 0 };
        try
        {
            long total;
            try { total = new FileInfo(localPath).Length; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new Failure(FileTransferResult.NotFound);
            }

            state = new TransferProgress { TransferId = id, FileName = remoteName, TotalBytes = total };
            await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha = await SHA256.HashDataAsync(fs, ct);
            fs.Position = 0;

            await SendAsync(FileMessage.UploadStart(id, total, sha, remoteName), ct);
            var accept = await ExpectAsync(id, FileMessageType.UploadAccept, ct);
            if (!accept.TryGetOffset(out var offset) || offset < 0 || offset > total)
                throw new Failure(FileTransferResult.BadOffset);

            fs.Position = offset;
            state.State = TransferState.Transferring;
            Report(progress, state, offset);

            var buf = new byte[_opt.ChunkSize];
            var pos = offset;
            var inflight = 0;
            while (pos < total)
            {
                var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, total - pos)), ct);
                if (n == 0) throw new Failure(FileTransferResult.SizeMismatch); // file changed underneath us
                await SendAsync(FileMessage.Chunk(id, pos, buf.AsSpan(0, n)), ct);
                pos += n;
                inflight++;

                while (inflight >= _opt.Window)
                {
                    await ReadAckAsync(id, progress, state, ct);
                    inflight--;
                }
            }
            while (inflight > 0)
            {
                await ReadAckAsync(id, progress, state, ct);
                inflight--;
            }

            await SendAsync(FileMessage.Empty(FileMessageType.End, id), ct);
            await ExpectAsync(id, FileMessageType.Complete, ct);

            state.State = TransferState.Completed;
            Report(progress, state, total);
            return new FileTransferOutcome(state, FileTransferResult.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TrySendCancelAsync(id);
            state.State = TransferState.Cancelled;
            return new FileTransferOutcome(state, FileTransferResult.Cancelled);
        }
        catch (Exception ex) when (TryMap(ex, out var result))
        {
            state.State = TransferState.Failed;
            return new FileTransferOutcome(state, result);
        }
        finally { _single.Release(); }
    }

    private async Task ReadAckAsync(Guid id, IProgress<TransferProgress>? progress, TransferProgress state,
        CancellationToken ct)
    {
        var ack = await ExpectAsync(id, FileMessageType.ChunkAck, ct);
        if (ack.TryGetOffset(out var written)) Report(progress, state, written);
    }

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<FileTransferOutcome> DownloadAsync(string remoteName, string localPath,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        await _single.WaitAsync(ct);
        var id = Guid.NewGuid();
        var state = new TransferProgress { TransferId = id, FileName = remoteName, TotalBytes = 0 };
        var partial = localPath + ".part";
        try
        {
            try { return await DownloadCoreAsync(id, remoteName, localPath, partial, progress, ct, allowRestart: true); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TrySendCancelAsync(id);
                TryDelete(partial);
                state.State = TransferState.Cancelled;
                return new FileTransferOutcome(state, FileTransferResult.Cancelled);
            }
        }
        catch (Exception ex) when (TryMap(ex, out var result))
        {
            state.State = TransferState.Failed;
            return new FileTransferOutcome(state, result);
        }
        finally { _single.Release(); }
    }

    private async Task<FileTransferOutcome> DownloadCoreAsync(Guid id, string remoteName, string localPath,
        string partial, IProgress<TransferProgress>? progress, CancellationToken ct, bool allowRestart)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        if (File.Exists(partial))
        {
            await using var existing = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buf = new byte[_opt.ChunkSize];
            int n;
            while ((n = await existing.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                offset += n;
            }
        }

        await SendAsync(FileMessage.DownloadRequest(id, offset, remoteName), ct);

        FileMessage info;
        try { info = await ExpectAsync(id, FileMessageType.DownloadInfo, ct); }
        catch (Failure f) when (f.Result == FileTransferResult.BadOffset && offset > 0 && allowRestart)
        {
            TryDelete(partial); // stale partial (remote file changed); restart from zero once
            return await DownloadCoreAsync(Guid.NewGuid(), remoteName, localPath, partial, progress, ct, false);
        }
        if (!info.TryGetInfo(out var total, out var expectedSha) || offset > total)
            throw new Failure(FileTransferResult.Malformed);

        var state = new TransferProgress
        {
            TransferId = id, FileName = remoteName, TotalBytes = total, State = TransferState.Transferring
        };
        Report(progress, state, offset);

        await using (var fs = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1,
                         FileOptions.Asynchronous))
        {
            fs.SetLength(offset);
            fs.Seek(offset, SeekOrigin.Begin);
            var pos = offset;

            while (true)
            {
                var m = await NextAsync(id, ct);
                if (m.Type == FileMessageType.Reject) throw new Failure(m.RejectReason);
                if (m.Type == FileMessageType.End) break;
                if (m.Type != FileMessageType.Chunk) continue;

                if (!m.TryGetChunk(out var chunkOffset, out var data) || chunkOffset != pos
                    || data.Length == 0 || pos + data.Length > total)
                    throw new Failure(FileTransferResult.OutOfOrder);

                await fs.WriteAsync(data, ct);
                hash.AppendData(data.Span);
                pos += data.Length;
                Report(progress, state, pos);
            }

            if (pos != total) throw new Failure(FileTransferResult.SizeMismatch);
        }

        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedSha))
        {
            TryDelete(partial);
            throw new Failure(FileTransferResult.ChecksumMismatch);
        }

        File.Move(partial, localPath, overwrite: true);
        state.State = TransferState.Completed;
        Report(progress, state, total);
        return new FileTransferOutcome(state, FileTransferResult.Ok);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private static void Report(IProgress<TransferProgress>? progress, TransferProgress state, long bytes)
    {
        state.TransferredBytes = bytes;
        progress?.Report(new TransferProgress
        {
            TransferId = state.TransferId, FileName = state.FileName, TotalBytes = state.TotalBytes,
            TransferredBytes = state.TransferredBytes, State = state.State
        });
    }

    private async Task<FileMessage> ExpectAsync(Guid id, FileMessageType type, CancellationToken ct)
    {
        while (true)
        {
            var m = await NextAsync(id, ct);
            if (m.Type == FileMessageType.Reject) throw new Failure(m.RejectReason);
            if (m.Type == type) return m;
        }
    }

    /// <summary>Next message for this transfer (or a transfer-less host error). Ignores stale messages.</summary>
    private async Task<FileMessage> NextAsync(Guid id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.ReplyTimeout);
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(timeout.Token))
                while (_inbox.Reader.TryRead(out var m))
                    if (m.TransferId == id || m.TransferId == Guid.Empty) return m;
            throw new Failure(FileTransferResult.ConnectionClosed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Failure(FileTransferResult.Timeout);
        }
    }

    private Task SendAsync(FileMessage m, CancellationToken ct) => _channel.SendAsync(m.ToBytes(), ct);

    private async Task TrySendCancelAsync(Guid id)
    {
        try { await _channel.SendAsync(FileMessage.Empty(FileMessageType.Cancel, id).ToBytes(), CancellationToken.None); }
        catch (Exception) { /* channel already closed */ }
    }

    private static bool TryMap(Exception ex, out FileTransferResult result)
    {
        switch (ex)
        {
            case Failure f: result = f.Result; return true;
            case FileNotFoundException or DirectoryNotFoundException: result = FileTransferResult.NotFound; return true;
            case IOException or UnauthorizedAccessException: result = FileTransferResult.IoError; return true;
            default: result = default; return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
