using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.FileTransfer.Tests;

public sealed class FileTransferTests : IAsyncLifetime
{
    private sealed class SyncProgress(Action<TransferProgress> onReport) : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = new();
        public void Report(TransferProgress value)
        {
            Reports.Add(value);
            onReport(value);
        }
    }

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-ft-" + Guid.NewGuid().ToString("N"));
    private string HostRoot => Path.Combine(_dir, "host");
    private string Local => Path.Combine(_dir, "local");

    private SessionInfo _session = null!;
    private FileTransferClient _client = null!;
    private LoopbackChannel _viewerCh = null!;
    private LoopbackChannel _hostCh = null!;
    private Task _hostLoop = Task.CompletedTask;
    private Task _clientLoop = Task.CompletedTask;
    private readonly List<(FileMessageType, FileTransferResult)> _decisions = new();

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(HostRoot);
        Directory.CreateDirectory(Local);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_hostCh is not null) await _hostCh.DisposeAsync();
        if (_viewerCh is not null) await _viewerCh.DisposeAsync();
        await _hostLoop;
        await _clientLoop;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SessionInfo Session(Permission p, SessionState state = SessionState.Connected) => new()
    {
        SessionId = SessionId.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        State = state,
        Permissions = new PermissionSet(p)
    };

    private void Start(Permission perms = Permission.FullControl, FileTransferHostOptions? hostOpt = null,
        FileTransferClientOptions? clientOpt = null, SessionState state = SessionState.Connected,
        bool startClient = true, ByteRateThrottle? throttle = null)
    {
        _session = Session(perms, state);
        _viewerCh = new LoopbackChannel(ChannelType.File);
        _hostCh = new LoopbackChannel(ChannelType.File);
        _viewerCh.PeerWriter = _hostCh.Writer;
        _hostCh.PeerWriter = _viewerCh.Writer;

        var host = new FileTransferHost(_hostCh, _session, HostRoot, hostOpt,
            (t, r) => { lock (_decisions) _decisions.Add((t, r)); }, throttle);
        _client = new FileTransferClient(_viewerCh, clientOpt);
        _hostLoop = host.RunAsync(_cts.Token);
        if (startClient) _clientLoop = _client.RunAsync(_cts.Token);
    }

    private string MakeFile(string dir, string name, int size)
    {
        var path = Path.Combine(dir, name);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Hex(byte[] sha) => Convert.ToHexString(sha);
    private static byte[] Sha(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private async Task WaitUntil(Func<bool> cond)
    {
        while (!cond() && !_cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond());
    }

    // ── Upload ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(70_000)]        // spans two chunks
    [InlineData(1_500_000)]     // spans many chunks and the ack window
    public async Task Upload_RoundTrips_WithMatchingContent(int size)
    {
        Start();
        var src = MakeFile(Local, "src.bin", size);
        var progress = new SyncProgress(_ => { });

        var outcome = await _client.UploadAsync(src, "out.bin", progress, _cts.Token);

        Assert.True(outcome.Ok, outcome.Result.ToString());
        Assert.Equal(TransferState.Completed, outcome.Progress.State);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(HostRoot, "out.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(HostRoot, ".partial")));
        Assert.Equal(size, progress.Reports.Last().TransferredBytes);
        Assert.Equal(TransferState.Completed, progress.Reports.Last().State);
    }

    [Fact]
    public async Task Upload_ReportsMonotonicProgress()
    {
        Start(clientOpt: new FileTransferClientOptions { ChunkSize = 16 * 1024, Window = 4 });
        var src = MakeFile(Local, "src.bin", 500_000);
        var progress = new SyncProgress(_ => { });

        Assert.True((await _client.UploadAsync(src, "out.bin", progress, _cts.Token)).Ok);

        var bytes = progress.Reports.Select(r => r.TransferredBytes).ToList();
        Assert.True(bytes.Count > 5);
        Assert.Equal(bytes.OrderBy(b => b), bytes);
    }

    [Fact]
    public async Task Upload_WithoutFileUploadPermission_Denied_NothingWritten()
    {
        Start(Permission.ViewScreen | Permission.FileDownload);
        var src = MakeFile(Local, "src.bin", 100);

        var outcome = await _client.UploadAsync(src, "out.bin", ct: _cts.Token);

        Assert.Equal(FileTransferResult.PermissionDenied, outcome.Result);
        Assert.Equal(TransferState.Failed, outcome.Progress.State);
        Assert.False(File.Exists(Path.Combine(HostRoot, "out.bin")));
    }

    [Theory]
    [InlineData(SessionState.Authorizing)]
    [InlineData(SessionState.Disconnecting)]
    [InlineData(SessionState.Failed)]
    public async Task NonConnectedSession_Denied(SessionState state)
    {
        Start(state: state);
        var src = MakeFile(Local, "src.bin", 10);
        Assert.Equal(FileTransferResult.SessionNotActive,
            (await _client.UploadAsync(src, "out.bin", ct: _cts.Token)).Result);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("..")]
    [InlineData(".partial")]
    [InlineData(".hidden")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad|name")]
    public async Task Upload_UnsafeNames_Rejected(string name)
    {
        Start();
        var src = MakeFile(Local, "src.bin", 10);

        var outcome = await _client.UploadAsync(src, name, ct: _cts.Token);

        Assert.Equal(FileTransferResult.InvalidName, outcome.Result);
        Assert.Empty(Directory.GetFiles(HostRoot));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public async Task Upload_ExistingFile_NotOverwritten()
    {
        Start();
        File.WriteAllText(Path.Combine(HostRoot, "out.bin"), "original");
        var src = MakeFile(Local, "src.bin", 10);

        Assert.Equal(FileTransferResult.Exists, (await _client.UploadAsync(src, "out.bin", ct: _cts.Token)).Result);
        Assert.Equal("original", File.ReadAllText(Path.Combine(HostRoot, "out.bin")));
    }

    [Fact]
    public async Task Upload_TooLarge_Rejected()
    {
        Start(hostOpt: new FileTransferHostOptions { MaxFileSize = 100 });
        var src = MakeFile(Local, "src.bin", 101);
        Assert.Equal(FileTransferResult.TooLarge, (await _client.UploadAsync(src, "out.bin", ct: _cts.Token)).Result);
    }

    [Fact]
    public async Task Upload_MissingLocalFile_ReportsNotFound()
    {
        Start();
        var outcome = await _client.UploadAsync(Path.Combine(Local, "nope.bin"), "out.bin", ct: _cts.Token);
        Assert.Equal(FileTransferResult.NotFound, outcome.Result);
    }

    [Fact]
    public async Task Upload_Cancel_DiscardsPartialAndPublishesNothing()
    {
        Start(clientOpt: new FileTransferClientOptions { ChunkSize = 16 * 1024, Window = 2 });
        var src = MakeFile(Local, "src.bin", 4_000_000);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var progress = new SyncProgress(p =>
        {
            if (p.TransferredBytes > 100_000) cancel.Cancel();
        });

        var outcome = await _client.UploadAsync(src, "out.bin", progress, cancel.Token);

        Assert.Equal(FileTransferResult.Cancelled, outcome.Result);
        Assert.Equal(TransferState.Cancelled, outcome.Progress.State);
        Assert.False(File.Exists(Path.Combine(HostRoot, "out.bin")));
        await WaitUntil(() => Directory.GetFiles(Path.Combine(HostRoot, ".partial")).Length == 0);
    }

    [Fact]
    public async Task Upload_ResumesFromExistingPartial()
    {
        Start();
        var src = MakeFile(Local, "src.bin", 300_000);
        var all = File.ReadAllBytes(src);
        var partial = Path.Combine(HostRoot, ".partial", Hex(SHA256.HashData(all)) + ".part");
        File.WriteAllBytes(partial, all[..120_000]); // earlier interrupted attempt

        var progress = new SyncProgress(_ => { });
        var outcome = await _client.UploadAsync(src, "out.bin", progress, _cts.Token);

        Assert.True(outcome.Ok);
        Assert.Equal(120_000, progress.Reports.First().TransferredBytes); // started from the resume offset
        Assert.Equal(all, File.ReadAllBytes(Path.Combine(HostRoot, "out.bin")));
    }

    [Fact]
    public async Task Upload_CorruptPartial_DetectedByChecksum_ThenRetrySucceeds()
    {
        Start();
        var src = MakeFile(Local, "src.bin", 200_000);
        var all = File.ReadAllBytes(src);
        var partial = Path.Combine(HostRoot, ".partial", Hex(SHA256.HashData(all)) + ".part");
        var poisoned = all[..50_000];
        poisoned[10] ^= 0xFF;
        File.WriteAllBytes(partial, poisoned);

        var first = await _client.UploadAsync(src, "out.bin", ct: _cts.Token);
        Assert.Equal(FileTransferResult.ChecksumMismatch, first.Result);
        Assert.False(File.Exists(Path.Combine(HostRoot, "out.bin")));
        Assert.False(File.Exists(partial));

        var second = await _client.UploadAsync(src, "out.bin", ct: _cts.Token);
        Assert.True(second.Ok);
        Assert.Equal(all, File.ReadAllBytes(Path.Combine(HostRoot, "out.bin")));
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    [InlineData(1_500_000)]
    public async Task Download_RoundTrips_WithMatchingContent(int size)
    {
        Start();
        var hostFile = MakeFile(HostRoot, "doc.bin", size);
        var dest = Path.Combine(Local, "got.bin");
        var progress = new SyncProgress(_ => { });

        var outcome = await _client.DownloadAsync("doc.bin", dest, progress, _cts.Token);

        Assert.True(outcome.Ok, outcome.Result.ToString());
        Assert.Equal(File.ReadAllBytes(hostFile), File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Equal(size, progress.Reports.Last().TransferredBytes);
    }

    [Fact]
    public async Task Download_WithoutFileDownloadPermission_Denied()
    {
        Start(Permission.ViewScreen | Permission.FileUpload);
        MakeFile(HostRoot, "doc.bin", 10);
        var dest = Path.Combine(Local, "got.bin");

        Assert.Equal(FileTransferResult.PermissionDenied,
            (await _client.DownloadAsync("doc.bin", dest, ct: _cts.Token)).Result);
        Assert.False(File.Exists(dest));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData(".partial")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("sub/doc.bin")]
    public async Task Download_UnsafeNames_Rejected_NoEscapeFromRoot(string name)
    {
        Start();
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "top secret");
        var dest = Path.Combine(Local, "got.bin");

        var outcome = await _client.DownloadAsync(name, dest, ct: _cts.Token);

        Assert.Equal(FileTransferResult.InvalidName, outcome.Result);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_Missing_ReportsNotFound()
    {
        Start();
        Assert.Equal(FileTransferResult.NotFound,
            (await _client.DownloadAsync("nope.bin", Path.Combine(Local, "x"), ct: _cts.Token)).Result);
    }

    [Fact]
    public async Task Download_ResumesFromExistingPartial()
    {
        Start();
        var hostFile = MakeFile(HostRoot, "doc.bin", 400_000);
        var all = File.ReadAllBytes(hostFile);
        var dest = Path.Combine(Local, "got.bin");
        File.WriteAllBytes(dest + ".part", all[..150_000]);

        var progress = new SyncProgress(_ => { });
        var outcome = await _client.DownloadAsync("doc.bin", dest, progress, _cts.Token);

        Assert.True(outcome.Ok);
        Assert.Equal(150_000, progress.Reports.First().TransferredBytes);
        Assert.Equal(all, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task Download_CorruptPartial_DetectedByChecksum()
    {
        Start();
        var hostFile = MakeFile(HostRoot, "doc.bin", 100_000);
        var all = File.ReadAllBytes(hostFile);
        var dest = Path.Combine(Local, "got.bin");
        var bad = all[..30_000];
        bad[5] ^= 0xFF;
        File.WriteAllBytes(dest + ".part", bad);

        var outcome = await _client.DownloadAsync("doc.bin", dest, ct: _cts.Token);

        Assert.Equal(FileTransferResult.ChecksumMismatch, outcome.Result);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.True((await _client.DownloadAsync("doc.bin", dest, ct: _cts.Token)).Ok);
    }

    [Fact]
    public async Task Download_PartialLargerThanRemote_RestartsFromZero()
    {
        Start();
        var hostFile = MakeFile(HostRoot, "doc.bin", 10_000);
        var dest = Path.Combine(Local, "got.bin");
        File.WriteAllBytes(dest + ".part", new byte[50_000]);

        Assert.True((await _client.DownloadAsync("doc.bin", dest, ct: _cts.Token)).Ok);
        Assert.Equal(File.ReadAllBytes(hostFile), File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task Download_Cancel_DiscardsPartial()
    {
        Start(hostOpt: new FileTransferHostOptions { MaxChunkSize = 8 * 1024 });
        MakeFile(HostRoot, "doc.bin", 3_000_000);
        var dest = Path.Combine(Local, "got.bin");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var progress = new SyncProgress(p =>
        {
            if (p.TransferredBytes > 50_000) cancel.Cancel();
        });

        var outcome = await _client.DownloadAsync("doc.bin", dest, progress, cancel.Token);

        Assert.Equal(FileTransferResult.Cancelled, outcome.Result);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Download_TooLarge_Rejected()
    {
        Start(hostOpt: new FileTransferHostOptions { MaxFileSize = 50 });
        MakeFile(HostRoot, "doc.bin", 51);
        Assert.Equal(FileTransferResult.TooLarge,
            (await _client.DownloadAsync("doc.bin", Path.Combine(Local, "x"), ct: _cts.Token)).Result);
    }

    // ── Raw protocol abuse (host must stay safe) ─────────────────────────

    private async Task<FileMessage> RawReadAsync(IAsyncEnumerator<ReadOnlyMemory<byte>> e)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        t.CancelAfter(Wait);
        Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(t.Token));
        Assert.True(FileMessage.TryParse(e.Current.Span, out var m));
        return m!;
    }

    private Task Raw(FileMessage m) => _viewerCh.SendAsync(m.ToBytes());

    [Fact]
    public async Task Host_RejectsOutOfOrderChunk_AndKeepsNoFinalFile()
    {
        Start(startClient: false);
        var data = new byte[1000];
        var id = Guid.NewGuid();
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        await Raw(FileMessage.UploadStart(id, data.Length, SHA256.HashData(data), "x.bin"));
        Assert.Equal(FileMessageType.UploadAccept, (await RawReadAsync(rx)).Type);

        await Raw(FileMessage.Chunk(id, 500, data.AsSpan(0, 100))); // skips ahead
        var reply = await RawReadAsync(rx);
        Assert.Equal(FileMessageType.Reject, reply.Type);
        Assert.Equal(FileTransferResult.OutOfOrder, reply.RejectReason);
        Assert.False(File.Exists(Path.Combine(HostRoot, "x.bin")));
    }

    [Fact]
    public async Task Host_RejectsChunkBeyondDeclaredSize()
    {
        Start(startClient: false);
        var id = Guid.NewGuid();
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        await Raw(FileMessage.UploadStart(id, 10, new byte[32], "x.bin"));
        await RawReadAsync(rx);
        await Raw(FileMessage.Chunk(id, 0, new byte[11]));

        var reply = await RawReadAsync(rx);
        Assert.Equal(FileTransferResult.SizeMismatch, reply.RejectReason);
    }

    [Fact]
    public async Task Host_RejectsEarlyEnd_AndTamperedContent()
    {
        Start(startClient: false);
        var good = new byte[100];
        var id = Guid.NewGuid();
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        // End before all bytes arrived.
        await Raw(FileMessage.UploadStart(id, 100, SHA256.HashData(good), "a.bin"));
        await RawReadAsync(rx);
        await Raw(FileMessage.Empty(FileMessageType.End, id));
        Assert.Equal(FileTransferResult.SizeMismatch, (await RawReadAsync(rx)).RejectReason);

        // Full size but content differs from the announced hash.
        var id2 = Guid.NewGuid();
        var tampered = new byte[100];
        tampered[0] = 1;
        await Raw(FileMessage.UploadStart(id2, 100, SHA256.HashData(good), "b.bin"));
        var accept = await RawReadAsync(rx);
        Assert.True(accept.TryGetOffset(out var offset));
        await Raw(FileMessage.Chunk(id2, offset, tampered));
        Assert.Equal(FileMessageType.ChunkAck, (await RawReadAsync(rx)).Type);
        await Raw(FileMessage.Empty(FileMessageType.End, id2));

        Assert.Equal(FileTransferResult.ChecksumMismatch, (await RawReadAsync(rx)).RejectReason);
        Assert.False(File.Exists(Path.Combine(HostRoot, "b.bin")));
    }

    [Fact]
    public async Task Host_PermissionRevokedMidTransfer_StopsAcceptingData()
    {
        Start(startClient: false);
        var data = new byte[1000];
        var id = Guid.NewGuid();
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        await Raw(FileMessage.UploadStart(id, data.Length, SHA256.HashData(data), "x.bin"));
        await RawReadAsync(rx);
        _session.Permissions.Revoke(Permission.FileUpload);
        await Raw(FileMessage.Chunk(id, 0, data));

        Assert.Equal(FileTransferResult.PermissionDenied, (await RawReadAsync(rx)).RejectReason);
        Assert.False(File.Exists(Path.Combine(HostRoot, "x.bin")));
    }

    [Fact]
    public async Task Host_RepliesMalformed_ToGarbage_AndKeepsServing()
    {
        Start(startClient: false);
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        await _viewerCh.SendAsync(new byte[] { 1, 2, 3 });
        await _viewerCh.SendAsync(new byte[FileMessage.MaxMessageSize + 1]);
        Assert.Equal(FileTransferResult.Malformed, (await RawReadAsync(rx)).RejectReason);
        Assert.Equal(FileTransferResult.Malformed, (await RawReadAsync(rx)).RejectReason);

        var id = Guid.NewGuid();
        await Raw(FileMessage.UploadStart(id, 0, SHA256.HashData([]), "ok.bin"));
        Assert.Equal(FileMessageType.UploadAccept, (await RawReadAsync(rx)).Type);
    }

    [Fact]
    public async Task Host_RejectsInvalidUtf8Name_AndShortUploadStart()
    {
        Start(startClient: false);
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        var body = new byte[8 + 32 + 2];
        body[8 + 32] = 0xFF;
        body[8 + 32 + 1] = 0xFE;
        await Raw(new FileMessage(FileMessageType.UploadStart, Guid.NewGuid(), body));
        Assert.Equal(FileTransferResult.Malformed, (await RawReadAsync(rx)).RejectReason);

        await Raw(new FileMessage(FileMessageType.UploadStart, Guid.NewGuid(), new byte[5]));
        Assert.Equal(FileTransferResult.Malformed, (await RawReadAsync(rx)).RejectReason);
    }

    [Fact]
    public async Task Host_LimitsConcurrentTransfers_AndDuplicateIds()
    {
        Start(startClient: false, hostOpt: new FileTransferHostOptions { MaxActiveTransfers = 2 });
        await using var rx = _viewerCh.ReceiveAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);

        byte[] Hash(int n) => SHA256.HashData(new byte[n]);
        var a = Guid.NewGuid();
        await Raw(FileMessage.UploadStart(a, 1, Hash(1), "a.bin"));
        Assert.Equal(FileMessageType.UploadAccept, (await RawReadAsync(rx)).Type);
        await Raw(FileMessage.UploadStart(Guid.NewGuid(), 2, Hash(2), "b.bin"));
        Assert.Equal(FileMessageType.UploadAccept, (await RawReadAsync(rx)).Type);

        await Raw(FileMessage.UploadStart(Guid.NewGuid(), 3, Hash(3), "c.bin"));
        Assert.Equal(FileTransferResult.Busy, (await RawReadAsync(rx)).RejectReason);

        await Raw(FileMessage.UploadStart(a, 4, Hash(4), "d.bin")); // reused transfer id
        Assert.Equal(FileTransferResult.Busy, (await RawReadAsync(rx)).RejectReason);
    }

    // ── Unit: message format, names, throttle ────────────────────────────

    [Fact]
    public void Message_RoundTrips()
    {
        var id = Guid.NewGuid();
        var m = FileMessage.Chunk(id, 1234, [1, 2, 3]);
        Assert.True(FileMessage.TryParse(m.ToBytes(), out var r));
        Assert.Equal(id, r!.TransferId);
        Assert.True(r.TryGetChunk(out var off, out var data));
        Assert.Equal(1234, off);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void Message_RejectsTruncated(int len) => Assert.False(FileMessage.TryParse(new byte[len], out _));

    [Fact]
    public void Message_RejectsUnknownType()
    {
        var b = FileMessage.Empty(FileMessageType.End, Guid.NewGuid()).ToBytes();
        b[0] = 99;
        Assert.False(FileMessage.TryParse(b, out _));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("my file (1).txt", true)]
    [InlineData("日本語.txt", true)]
    [InlineData("COM1.txt", false)]
    [InlineData("lpt9", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SafeFileName_Cases(string? name, bool valid) => Assert.Equal(valid, SafeFileName.IsValid(name));

    [Fact]
    public void SafeFileName_RejectsOverlongAndControlChars()
    {
        Assert.False(SafeFileName.IsValid(new string('a', 256)));
        Assert.False(SafeFileName.IsValid("a\u0001b"));
    }

    [Fact]
    public async Task Throttle_DelaysWhenOverRate_AndUnlimitedWhenZero()
    {
        var now = DateTimeOffset.UtcNow;
        var delays = new List<TimeSpan>();
        var t = new ByteRateThrottle(100, () => now, (d, _) => { delays.Add(d); now += d; return Task.CompletedTask; });

        await t.WaitAsync(60);
        await t.WaitAsync(30);
        Assert.Empty(delays);
        await t.WaitAsync(30); // 120 > 100 within the window
        Assert.Single(delays);
        Assert.True(delays[0] > TimeSpan.Zero && delays[0] <= TimeSpan.FromSeconds(1));

        var unlimited = new ByteRateThrottle(0, () => now, (d, _) => { delays.Add(d); return Task.CompletedTask; });
        await unlimited.WaitAsync(int.MaxValue);
        Assert.Single(delays);
    }

    [Fact]
    public void ClientAndHost_RejectNonFileChannel()
    {
        var ch = new LoopbackChannel(ChannelType.Input);
        Assert.Throws<ArgumentException>(() => new FileTransferClient(ch));
        Assert.Throws<ArgumentException>(() => new FileTransferHost(ch, Session(Permission.FullControl), HostRoot));
    }
}
