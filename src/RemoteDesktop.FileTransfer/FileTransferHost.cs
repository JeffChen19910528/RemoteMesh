using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.FileTransfer;

public sealed class FileTransferHostOptions
{
    public long MaxFileSize { get; init; } = 1L * 1024 * 1024 * 1024;
    public int MaxChunkSize { get; init; } = FileMessage.MaxChunkSize;
    public int MaxActiveTransfers { get; init; } = 4;
    /// <summary>Outgoing (download) rate cap; 0 = unlimited.</summary>
    public long MaxBytesPerSecond { get; init; }
}

/// <summary>
/// Host side of the File channel. Files live only inside <c>rootDirectory</c>; remote names are validated
/// as plain file names. Uploads go to <c>.partial/&lt;sha256&gt;.part</c> and are published only after the
/// SHA-256 matches, so a failed or interrupted transfer never leaves a corrupt final file and can be resumed.
/// Permission and session state are re-checked on every message. File content is never logged.
/// </summary>
public sealed class FileTransferHost
{
    private sealed class Upload
    {
        public required FileStream Stream { get; init; }
        public required string PartialPath { get; init; }
        public required string FinalName { get; init; }
        public required long Total { get; init; }
        public required byte[] Sha256 { get; init; }
        public long Written { get; set; }
    }

    private readonly IChannel _channel;
    private readonly SessionInfo _session;
    private readonly string _root;
    private readonly string _partialDir;
    private readonly FileTransferHostOptions _opt;
    private readonly Action<FileMessageType, FileTransferResult>? _onDecision;
    private readonly ByteRateThrottle _throttle;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Upload> _uploads = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _downloads = new();
    private readonly HashSet<string> _activeShas = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public FileTransferHost(IChannel channel, SessionInfo session, string rootDirectory,
        FileTransferHostOptions? options = null, Action<FileMessageType, FileTransferResult>? onDecision = null,
        ByteRateThrottle? throttle = null)
    {
        if (channel.Type != ChannelType.File)
            throw new ArgumentException("File transfer must use the File channel.", nameof(channel));
        _channel = channel;
        _session = session;
        _root = Path.GetFullPath(rootDirectory);
        _partialDir = Path.Combine(_root, ".partial");
        _opt = options ?? new FileTransferHostOptions();
        _onDecision = onDecision;
        _throttle = throttle ?? new ByteRateThrottle(_opt.MaxBytesPerSecond);
        Directory.CreateDirectory(_partialDir);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await foreach (var raw in _channel.ReceiveAsync(ct))
                await HandleAsync(raw, ct);
        }
        catch (OperationCanceledException) { }
        finally { Shutdown(); }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        if (!FileMessage.TryParse(raw.Span, out var msg) || msg is null)
        {
            await SendAsync(FileMessage.Reject(Guid.Empty, FileTransferResult.Malformed), ct);
            _onDecision?.Invoke(FileMessageType.Reject, FileTransferResult.Malformed);
            return;
        }

        FileTransferResult result;
        switch (msg.Type)
        {
            case FileMessageType.UploadStart: result = await OnUploadStartAsync(msg, ct); break;
            case FileMessageType.Chunk: result = await OnUploadChunkAsync(msg, ct); break;
            case FileMessageType.End: result = await OnUploadEndAsync(msg, ct); break;
            case FileMessageType.Cancel: result = OnCancel(msg); break;
            case FileMessageType.DownloadRequest: result = await OnDownloadRequestAsync(msg, ct); break;
            default: result = FileTransferResult.Malformed; break;
        }

        if (result != FileTransferResult.Ok)
            await SendAsync(FileMessage.Reject(msg.TransferId, result), ct);
        _onDecision?.Invoke(msg.Type, result);
    }

    // ── Upload (viewer → host) ───────────────────────────────────────────

    private async Task<FileTransferResult> OnUploadStartAsync(FileMessage msg, CancellationToken ct)
    {
        var gate = Check(Permission.FileUpload);
        if (gate != FileTransferResult.Ok) return gate;
        if (!msg.TryGetUploadStart(out var total, out var sha, out var name)) return FileTransferResult.Malformed;
        if (!SafeFileName.IsValid(name)) return FileTransferResult.InvalidName;
        if (total < 0 || total > _opt.MaxFileSize) return FileTransferResult.TooLarge;
        if (File.Exists(Path.Combine(_root, name))) return FileTransferResult.Exists;

        var shaHex = Convert.ToHexString(sha);
        var partial = Path.Combine(_partialDir, shaHex + ".part");

        lock (_sync)
        {
            if (_uploads.ContainsKey(msg.TransferId) || _downloads.ContainsKey(msg.TransferId)
                || _uploads.Count + _downloads.Count >= _opt.MaxActiveTransfers
                || !_activeShas.Add(shaHex))
                return FileTransferResult.Busy;
        }

        FileStream stream;
        long offset;
        try
        {
            stream = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.Asynchronous);
            offset = stream.Length <= total ? stream.Length : 0;
            if (offset == 0) stream.SetLength(0);
            stream.Seek(offset, SeekOrigin.Begin);
        }
        catch (IOException)
        {
            lock (_sync) _activeShas.Remove(shaHex);
            return FileTransferResult.IoError;
        }

        lock (_sync)
            _uploads[msg.TransferId] = new Upload
            {
                Stream = stream, PartialPath = partial, FinalName = name, Total = total, Sha256 = sha, Written = offset
            };

        await SendAsync(FileMessage.WithOffset(FileMessageType.UploadAccept, msg.TransferId, offset), ct);
        return FileTransferResult.Ok;
    }

    private async Task<FileTransferResult> OnUploadChunkAsync(FileMessage msg, CancellationToken ct)
    {
        var up = GetUpload(msg.TransferId);
        if (up is null) return FileTransferResult.NotFound;

        var gate = Check(Permission.FileUpload);
        if (gate != FileTransferResult.Ok) { AbortUpload(msg.TransferId, deletePartial: false); return gate; }

        if (!msg.TryGetChunk(out var offset, out var data) || data.Length == 0 || data.Length > _opt.MaxChunkSize)
        {
            AbortUpload(msg.TransferId, deletePartial: false);
            return FileTransferResult.Malformed;
        }
        if (offset != up.Written)
        {
            AbortUpload(msg.TransferId, deletePartial: false);
            return FileTransferResult.OutOfOrder;
        }
        if (up.Written + data.Length > up.Total)
        {
            AbortUpload(msg.TransferId, deletePartial: false);
            return FileTransferResult.SizeMismatch;
        }

        try { await up.Stream.WriteAsync(data, ct); }
        catch (IOException) { AbortUpload(msg.TransferId, deletePartial: false); return FileTransferResult.IoError; }

        up.Written += data.Length;
        await SendAsync(FileMessage.WithOffset(FileMessageType.ChunkAck, msg.TransferId, up.Written), ct);
        return FileTransferResult.Ok;
    }

    private async Task<FileTransferResult> OnUploadEndAsync(FileMessage msg, CancellationToken ct)
    {
        var up = GetUpload(msg.TransferId);
        if (up is null) return FileTransferResult.NotFound;

        var gate = Check(Permission.FileUpload);
        if (gate != FileTransferResult.Ok) { AbortUpload(msg.TransferId, deletePartial: false); return gate; }

        if (up.Written != up.Total)
        {
            AbortUpload(msg.TransferId, deletePartial: false);
            return FileTransferResult.SizeMismatch;
        }

        await up.Stream.FlushAsync(ct);
        AbortUpload(msg.TransferId, deletePartial: false); // closes the stream, releases the slot

        byte[] actual;
        try
        {
            await using var read = new FileStream(up.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            actual = await SHA256.HashDataAsync(read, ct);
        }
        catch (IOException) { return FileTransferResult.IoError; }

        if (!CryptographicOperations.FixedTimeEquals(actual, up.Sha256))
        {
            TryDelete(up.PartialPath);
            return FileTransferResult.ChecksumMismatch;
        }

        var final = Path.Combine(_root, up.FinalName);
        try
        {
            if (File.Exists(final)) { TryDelete(up.PartialPath); return FileTransferResult.Exists; }
            File.Move(up.PartialPath, final);
        }
        catch (IOException) { return FileTransferResult.IoError; }

        await SendAsync(FileMessage.Empty(FileMessageType.Complete, msg.TransferId), ct);
        return FileTransferResult.Ok;
    }

    private FileTransferResult OnCancel(FileMessage msg)
    {
        // Explicit cancel discards the partial file; disconnects and revocations keep it for resume.
        if (GetUpload(msg.TransferId) is not null) AbortUpload(msg.TransferId, deletePartial: true);

        CancellationTokenSource? cts;
        lock (_sync) _downloads.TryGetValue(msg.TransferId, out cts);
        cts?.Cancel();
        return FileTransferResult.Ok;
    }

    // ── Download (host → viewer) ─────────────────────────────────────────

    private async Task<FileTransferResult> OnDownloadRequestAsync(FileMessage msg, CancellationToken ct)
    {
        var gate = Check(Permission.FileDownload);
        if (gate != FileTransferResult.Ok) return gate;
        if (!msg.TryGetNameAfterOffset(out var offset, out var name)) return FileTransferResult.Malformed;
        if (!SafeFileName.IsValid(name)) return FileTransferResult.InvalidName;

        var path = Path.Combine(_root, name);
        if (!File.Exists(path)) return FileTransferResult.NotFound;

        long total;
        byte[] sha;
        try
        {
            total = new FileInfo(path).Length;
            if (total > _opt.MaxFileSize) return FileTransferResult.TooLarge;
            if (offset < 0 || offset > total) return FileTransferResult.BadOffset;

            await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha = await SHA256.HashDataAsync(read, ct);
        }
        catch (IOException) { return FileTransferResult.IoError; }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_sync)
        {
            if (_uploads.ContainsKey(msg.TransferId) || _downloads.ContainsKey(msg.TransferId)
                || _uploads.Count + _downloads.Count >= _opt.MaxActiveTransfers)
            {
                cts.Dispose();
                return FileTransferResult.Busy;
            }
            _downloads[msg.TransferId] = cts;
        }

        await SendAsync(FileMessage.DownloadInfo(msg.TransferId, total, sha), ct);
        _ = Task.Run(() => SendFileAsync(msg.TransferId, path, offset, total, cts.Token), CancellationToken.None);
        return FileTransferResult.Ok;
    }

    private async Task SendFileAsync(Guid id, string path, long offset, long total, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[_opt.MaxChunkSize];
            var pos = offset;

            while (pos < total)
            {
                var gate = Check(Permission.FileDownload);
                if (gate != FileTransferResult.Ok) { await SendAsync(FileMessage.Reject(id, gate), ct); return; }

                var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, total - pos)), ct);
                if (n == 0) { await SendAsync(FileMessage.Reject(id, FileTransferResult.SizeMismatch), ct); return; }

                await _throttle.WaitAsync(n, ct);
                await SendAsync(FileMessage.Chunk(id, pos, buf.AsSpan(0, n)), ct);
                pos += n;
            }
            await SendAsync(FileMessage.Empty(FileMessageType.End, id), ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException)
        {
            try { await SendAsync(FileMessage.Reject(id, FileTransferResult.IoError), CancellationToken.None); }
            catch (Exception) { /* channel already gone */ }
        }
        finally
        {
            lock (_sync)
            {
                if (_downloads.Remove(id, out var cts)) cts.Dispose();
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private FileTransferResult Check(Permission required)
    {
        if (_session.State != SessionState.Connected || _session.IsExpired)
            return FileTransferResult.SessionNotActive;
        return _session.Permissions.Has(required) ? FileTransferResult.Ok : FileTransferResult.PermissionDenied;
    }

    private Upload? GetUpload(Guid id)
    {
        lock (_sync) return _uploads.GetValueOrDefault(id);
    }

    private void AbortUpload(Guid id, bool deletePartial)
    {
        Upload? up;
        lock (_sync)
        {
            if (!_uploads.Remove(id, out up)) return;
            _activeShas.Remove(Convert.ToHexString(up.Sha256));
        }
        up.Stream.Dispose();
        if (deletePartial) TryDelete(up.PartialPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task SendAsync(FileMessage m, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _channel.SendAsync(m.ToBytes(), ct); }
        finally { _sendLock.Release(); }
    }

    private void Shutdown()
    {
        List<Guid> ids;
        List<CancellationTokenSource> cts;
        lock (_sync)
        {
            ids = [.. _uploads.Keys];
            cts = [.. _downloads.Values];
        }
        foreach (var id in ids) AbortUpload(id, deletePartial: false); // keep partials for resume
        foreach (var c in cts) c.Cancel();
    }
}
