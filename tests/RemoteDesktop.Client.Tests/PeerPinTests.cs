using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Capture;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Input;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client.Tests;

public sealed class KeyFingerprintTests
{
    private static byte[] Key(byte seed) => Enumerable.Range(0, 65).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public void BothSidesSeeTheSameCode_WhoeverAsks()
    {
        Assert.Equal(KeyFingerprint.SecurityCode(Key(1), Key(2)), KeyFingerprint.SecurityCode(Key(2), Key(1)));
    }

    [Fact]
    public void TheCode_IsSixGroupsOfFiveDigits()
    {
        Assert.Matches(@"^\d{5}( \d{5}){5}$", KeyFingerprint.SecurityCode(Key(1), Key(2)));
    }

    [Fact]
    public void ADifferentKeyOnEitherSide_GivesADifferentCode()
    {
        var code = KeyFingerprint.SecurityCode(Key(1), Key(2));
        Assert.NotEqual(code, KeyFingerprint.SecurityCode(Key(1), Key(3)));
        Assert.NotEqual(code, KeyFingerprint.SecurityCode(Key(9), Key(2)));
        var oneBitOff = Key(2);
        oneBitOff[40] ^= 1;
        Assert.NotEqual(code, KeyFingerprint.SecurityCode(Key(1), oneBitOff));
    }

    [Fact]
    public void MovingBytesBetweenTheTwoKeys_DoesNotKeepTheCode()
    {
        // ("ab","c") versus ("a","bc"): the lengths are part of what is hashed.
        Assert.NotEqual(
            KeyFingerprint.SecurityCode([1, 2], [3]),
            KeyFingerprint.SecurityCode([1], [2, 3]));
    }

    [Fact]
    public void FingerprintOfAKey_IsItsSha256_AndKeysDoNotCollide()
    {
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(Key(5)), KeyFingerprint.Of(Key(5)));
        Assert.NotEqual(KeyFingerprint.Of(Key(5)), KeyFingerprint.Of(Key(6)));
    }

    [Fact]
    public void CodesLookEvenlySpread_NotConcentratedOnFewDigits()
    {
        var digits = new int[10];
        for (var i = 0; i < 400; i++)
            foreach (var c in KeyFingerprint.SecurityCode(Key((byte)i), Key((byte)(i + 77))).Where(char.IsDigit))
                digits[c - '0']++;
        var total = digits.Sum();
        Assert.All(digits, d => Assert.InRange(d / (double)total, 0.07, 0.13)); // about 10% each
    }
}

public sealed class PeerPinStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-pins-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "known-peers.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Key(byte b) => Enumerable.Repeat(b, 65).ToArray();

    [Fact]
    public void AFirstContactIsPinned_TheSameKeyIsKnown_AnotherKeyIsRefused()
    {
        var store = new PeerPinStore();
        var peer = DeviceId.New();
        Assert.Equal(PeerPinResult.NewPeer, store.Check(peer, Key(1)));
        Assert.Equal(PeerPinResult.Known, store.Check(peer, Key(1)));
        Assert.Equal(PeerPinResult.KeyChanged, store.Check(peer, Key(2)));
        Assert.Equal(PeerPinResult.KeyChanged, store.Check(peer, Key(2)));   // and it stays refused
        Assert.Equal(PeerPinResult.Known, store.Check(peer, Key(1)));        // the original key is still the one
    }

    [Fact]
    public void PinsLastAcrossRestarts()
    {
        var peer = DeviceId.New();
        new PeerPinStore(File_).Check(peer, Key(1));

        var after = new PeerPinStore(File_);
        Assert.Equal(PeerPinResult.Known, after.Check(peer, Key(1)));
        Assert.Equal(PeerPinResult.KeyChanged, after.Check(peer, Key(2)));
    }

    [Fact]
    public void ThePinFile_HoldsOnlyAHashOfTheKey()
    {
        var peer = DeviceId.New();
        var key = Key(7);
        new PeerPinStore(File_).Check(peer, key);
        var text = File.ReadAllText(File_);
        Assert.DoesNotContain(Convert.ToBase64String(key), text);
        Assert.DoesNotContain(Convert.ToHexString(key), text);
        Assert.Contains(Convert.ToHexString(KeyFingerprint.Of(key)), text);
    }

    [Fact]
    public void Verification_IsRememberedForThatKeyOnly()
    {
        var store = new PeerPinStore(File_);
        var peer = DeviceId.New();
        store.Check(peer, Key(1));
        Assert.False(store.IsVerified(peer, Key(1)));
        Assert.False(store.MarkVerified(peer, Key(2)));      // not the pinned key: no
        Assert.False(store.MarkVerified(DeviceId.New(), Key(1)));
        Assert.True(store.MarkVerified(peer, Key(1)));

        var after = new PeerPinStore(File_);
        Assert.True(after.IsVerified(peer, Key(1)));
        Assert.False(after.IsVerified(peer, Key(2)));
    }

    [Fact]
    public void ForgettingAPeer_LetsItsNewKeyBePinned()
    {
        var store = new PeerPinStore(File_);
        var peer = DeviceId.New();
        store.Check(peer, Key(1));
        Assert.Equal(PeerPinResult.KeyChanged, store.Check(peer, Key(2)));
        Assert.True(store.Forget(peer));
        Assert.False(store.Forget(peer));
        Assert.Equal(PeerPinResult.NewPeer, store.Check(peer, Key(2)));
        Assert.False(store.IsVerified(peer, Key(2)));        // the old verification does not carry over
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"x\":{\"KeyHash\":\"short\"}}")]
    public void ADamagedFile_StartsEmpty_InsteadOfCrashing(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);
        var store = new PeerPinStore(File_);
        Assert.Equal(0, store.Count);
        Assert.Equal(PeerPinResult.NewPeer, store.Check(DeviceId.New(), Key(1)));
    }

    [Fact]
    public void ManyPeersAtOnce_AreAllPinnedOnce()
    {
        var store = new PeerPinStore(File_);
        var peers = Enumerable.Range(0, 200).Select(_ => DeviceId.New()).ToArray();
        Parallel.ForEach(peers, p => Assert.Equal(PeerPinResult.NewPeer, store.Check(p, Key(3))));
        Assert.Equal(200, store.Count);
        Assert.Equal(200, new PeerPinStore(File_).Count);
    }

    [Fact]
    public void TheStoreCannotGrowWithoutLimit_AndKeepsVerifiedPeers()
    {
        var store = new PeerPinStore();
        var kept = DeviceId.New();
        store.Check(kept, Key(1));
        store.MarkVerified(kept, Key(1));
        for (var i = 0; i < PeerPinStore.MaxEntries + 50; i++) store.Check(DeviceId.New(), Key(2));
        Assert.True(store.Count <= PeerPinStore.MaxEntries);
        Assert.True(store.IsVerified(kept, Key(1)));
    }
}

/// <summary>Two real clients against the real server, with a server that turns hostile after the first meeting.</summary>
public sealed partial class PeerKeyEndToEndTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-pinE2E-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(90));

    public Task InitializeAsync() { Directory.CreateDirectory(_dir); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        foreach (var d in Enumerable.Reverse(_disposables)) { try { await d.DisposeAsync(); } catch (Exception) { } }
        await _factory.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Sink : IFrameRenderer
    {
        public Task RenderAsync(CaptureFrame frame, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Nobody : IInputController
    {
        public Task InjectAsync(InputEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Allow : IApprovalHandler
    {
        public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new ApprovalDecision(true, Permission.ViewScreen));
    }

    private ClientOptions Options(string name) => new()
    {
        ServerUri = new Uri("http://localhost/"),
        DeviceName = name,
        DataDirectory = Path.Combine(_dir, name),
        KeepAliveInterval = TimeSpan.FromMilliseconds(100),
        KeepAliveTimeout = TimeSpan.FromSeconds(10),
        ConfigureHub = o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        },
        RelayConnector = async (uri, token, ct) =>
        {
            var ws = _factory.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await ws.ConnectAsync(uri, ct);
        }
    };

    private async Task<RemoteMeshClient> StartAsync(string name, bool host = false)
    {
        var services = host ? new HostServices { CaptureFactory = () => new SyntheticScreenCapture(64, 48, fps: 10), Input = new Nobody() } : null;
        var client = new RemoteMeshClient(Options(name), services, host ? new Allow() : null);
        _disposables.Add(client);
        await client.StartAsync(_cts.Token);
        return client;
    }

    private static async Task Eventually(Func<bool> cond, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(20);
        Assert.True(cond(), $"not reached in time: {what}");
    }

    [Fact]
    public async Task BothSidesShowTheSameSecurityCode_AndTheSecondMeetingIsNotNew()
    {
        var hostSessions = new ConcurrentQueue<HostSession>();
        var host = await StartAsync("host", host: true);
        host.HostSessionStarted += hostSessions.Enqueue;
        var viewerClient = await StartAsync("viewer");

        await using var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        await Eventually(() => !hostSessions.IsEmpty, "host session");
        hostSessions.TryPeek(out var hostSide);

        Assert.Matches(@"^\d{5}( \d{5}){5}$", first.SecurityCode);
        Assert.Equal(first.SecurityCode, hostSide!.SecurityCode);       // what the two people read to each other
        Assert.Equal(host.DeviceId, first.PeerDeviceId);
        Assert.Equal(viewerClient.DeviceId, hostSide.PeerDeviceId);
        Assert.True(first.PeerIsNew);
        Assert.False(first.PeerVerified);

        Assert.True(first.MarkPeerVerified());                          // the person compared and they matched
        await first.DisposeAsync();

        await using var second = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        Assert.False(second.PeerIsNew);
        Assert.True(second.PeerVerified);                               // remembered
        Assert.Equal(first.SecurityCode, second.SecurityCode);
    }

    [Fact]
    public async Task AServerThatHandsOutAnotherKeyLater_IsCaught_AndNothingIsConnected()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        await using (var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token)) { }

        // The server (the part that can lie) now claims the host has a different key.
        var registry = _factory.Services.GetRequiredService<IDeviceRegistry>();
        var info = await registry.FindAsync(host.DeviceId);
        var impostor = DeviceKeyGenerator.Generate().PublicKey;
        await registry.RegisterAsync(info!, impostor);

        await Assert.ThrowsAsync<PeerKeyChangedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token));

        // Having decided the new key is legitimate, the person clears the old pin (and only then).
        Assert.True(viewerClient.ForgetPeer(host.DeviceId));
        Assert.False(viewerClient.ForgetPeer(host.DeviceId));
    }

    [Fact]
    public async Task TheHostRefusesToo_WhenTheViewersKeyIsNotTheOneItKnows()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        var errors = new ConcurrentQueue<Exception>();
        host.Error += (_, e) => errors.Enqueue(e);
        await using (var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token)) { }

        var registry = _factory.Services.GetRequiredService<IDeviceRegistry>();
        await registry.RegisterAsync((await registry.FindAsync(viewerClient.DeviceId))!, DeviceKeyGenerator.Generate().PublicKey);

        await Assert.ThrowsAnyAsync<Exception>(() => viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token));
        await Eventually(() => errors.Any(e => e is PeerKeyChangedException), "the host noticed the changed key");
    }
}
