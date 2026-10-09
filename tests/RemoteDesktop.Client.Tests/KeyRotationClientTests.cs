using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Client.Tests;

// A device changes its key: the clients that knew the old one follow by checking the device's own signatures.
public sealed partial class PeerKeyEndToEndTests
{
    /// <summary>The other side of a closed session notices a moment later; the key can change once it has.</summary>
    private async Task RotateOnceIdleAsync(RemoteMeshClient client)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { await client.RotateKeyAsync(_cts.Token); return; }
            catch (InvalidOperationException) when (!limit.IsCancellationRequested) { await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task ADeviceThatChangedItsKey_IsFollowed_AndKeepsItsVerifiedStatus_WithANewSecurityCode()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        string oldCode;
        await using (var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token))
        {
            oldCode = first.SecurityCode;
            Assert.True(first.MarkPeerVerified());
        }

        await RotateOnceIdleAsync(host);                          // signed by the old and the new key

        await using var after = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        Assert.True(after.PeerKeyRotated);                        // told, not alarmed
        Assert.False(after.PeerIsNew);
        Assert.True(after.PeerVerified);                          // the same device, proven by its own signatures
        Assert.NotEqual(oldCode, after.SecurityCode);             // the code follows the new key

        await after.DisposeAsync();
        await using var third = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);
        Assert.False(third.PeerKeyRotated);                       // the pin moved; nothing special the next time
    }

    [Fact]
    public async Task TheNewKeySurvivesARestartOfTheDevice()
    {
        var host = await StartAsync("host", host: true);
        var id = host.DeviceId;
        await RotateOnceIdleAsync(host);
        await host.DisposeAsync();

        var again = await StartAsync("host", host: true);        // same data directory: must register with the NEW key
        Assert.Equal(id, again.DeviceId);
        Assert.True(again.IsConnected);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "host"), "*.next"));   // nothing left half-done
    }

    [Fact]
    public async Task AServerThatInventsAHistory_DoesNotGetItsKeyAccepted()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        await using (var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token)) { }

        // The server swaps the key AND writes a "rotation" that nobody signed.
        var registry = _factory.Services.GetRequiredService<IDeviceRegistry>();
        var id = host.DeviceId;
        var oldKey = (await registry.GetPublicKeyAsync(id))!;
        var impostor = DeviceKeyGenerator.Generate();
        Assert.True(await registry.TryRotateKeyAsync(new KeyRotation
        {
            DeviceId = id, Sequence = 0, OldKey = oldKey, NewKey = impostor.PublicKey,
            OldKeySignature = new byte[64], NewKeySignature = new byte[64]
        }));

        await Assert.ThrowsAsync<PeerKeyChangedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task AHistoryWhereOnlyTheImpostorSigned_IsNotAcceptedEither()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        await using (var first = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token)) { }

        // A real signature by the impostor's key, but the "old key" signature comes from someone else.
        var registry = _factory.Services.GetRequiredService<IDeviceRegistry>();
        var id = host.DeviceId;
        var oldKey = (await registry.GetPublicKeyAsync(id))!;
        var impostor = DeviceKeyGenerator.Generate();
        var statement = KeyRotationProof.BuildPayload(id.ToString(), oldKey, impostor.PublicKey);
        await registry.TryRotateKeyAsync(new KeyRotation
        {
            DeviceId = id, Sequence = 0, OldKey = oldKey, NewKey = impostor.PublicKey,
            OldKeySignature = DeviceKeyGenerator.Sign(DeviceKeyGenerator.Generate(), statement),
            NewKeySignature = DeviceKeyGenerator.Sign(impostor, statement)
        });

        await Assert.ThrowsAsync<PeerKeyChangedException>(() =>
            viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token));
    }

    [Fact]
    public async Task TheKeyCannotBeChangedWhileASessionIsOpen()
    {
        var host = await StartAsync("host", host: true);
        var viewerClient = await StartAsync("viewer");
        await using var open = await viewerClient.ConnectAsync(host.DeviceId, new Sink(), cancellationToken: _cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewerClient.RotateKeyAsync(_cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RotateKeyAsync(_cts.Token));
        Assert.False(open.Completion.IsCompleted);                // and nothing broke
    }

    [Fact]
    public async Task AChangeLeftHalfDone_IsSettledAtTheNextStart_WhicheverWayTheServerWent()
    {
        var dir = Path.Combine(_dir, "halfdone");
        var store = new OsKeyStore(dir);
        var first = await StartAsync("halfdone");
        var id = first.DeviceId;
        await first.DisposeAsync();

        // Case 1: a staged key the server never heard of is thrown away; the device keeps working with its own key.
        await store.StagePendingAsync(id, DeviceKeyGenerator.Generate());
        var second = await StartAsync("halfdone");
        Assert.True(second.IsConnected);
        Assert.Null(await store.LoadPendingAsync(id));
        var original = await store.LoadOrCreateAsync(id);
        var registry = _factory.Services.GetRequiredService<IDeviceRegistry>();
        Assert.Equal(original.PublicKey, await registry.GetPublicKeyAsync(id));
        await second.DisposeAsync();

        // Case 2: the server did take the new key, but the device never got to commit it.
        var next = DeviceKeyGenerator.Generate();
        var statement = KeyRotationProof.BuildPayload(id.ToString(), original.PublicKey, next.PublicKey);
        Assert.True(await registry.TryRotateKeyAsync(new KeyRotation
        {
            DeviceId = id, Sequence = 0, OldKey = original.PublicKey, NewKey = next.PublicKey,
            OldKeySignature = DeviceKeyGenerator.Sign(original, statement), NewKeySignature = DeviceKeyGenerator.Sign(next, statement)
        }));
        await store.StagePendingAsync(id, next);

        var third = await StartAsync("halfdone");
        Assert.True(third.IsConnected);
        Assert.Null(await store.LoadPendingAsync(id));
        Assert.Equal(next.PublicKey, (await store.LoadOrCreateAsync(id)).PublicKey);   // committed
    }
}
