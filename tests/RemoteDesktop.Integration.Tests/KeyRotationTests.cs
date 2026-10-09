using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Integration.Tests;

/// <summary>A device moving to a new key: both keys must sign, the server only relays the proof.</summary>
public sealed class KeyRotationTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly List<HubConnection> _hubs = new();
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var h in _hubs) await h.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private sealed record Actor(HubConnection Conn, DeviceInfoDto Dto, DeviceKeyPair Keys, ConcurrentQueue<SessionDto> Incoming);

    private async Task<Actor> NewActorAsync(string name, DeviceInfoDto? existing = null, DeviceKeyPair? keys = null)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        _hubs.Add(conn);
        var dto = existing ?? TestAuth.MakeDevice(name);
        var k = keys ?? TestAuth.KeysOf(dto.DeviceId);
        var actor = new Actor(conn, dto, k, new());
        conn.On<SessionDto>("IncomingSessionRequest", actor.Incoming.Enqueue);
        await conn.StartAsync(_cts.Token);
        await TestAuth.RegisterAsync(conn, dto, k);
        return actor;
    }

    private static (string NewKey, string OldSig, string NewSig, DeviceKeyPair Next) Proof(Actor a, DeviceKeyPair? next = null)
    {
        next ??= DeviceKeyGenerator.Generate();
        var payload = KeyRotationProof.BuildPayload(a.Dto.DeviceId, a.Keys.PublicKey, next.PublicKey);
        return (Convert.ToBase64String(next.PublicKey),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(a.Keys, payload)),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(next, payload)), next);
    }

    private Task Rotate(Actor a, (string NewKey, string OldSig, string NewSig, DeviceKeyPair Next) p) =>
        a.Conn.InvokeAsync("RotateDeviceKey", p.NewKey, p.OldSig, p.NewSig, _cts.Token);

    private IDeviceRegistry Registry => _factory.Services.GetRequiredService<IDeviceRegistry>();

    [Fact]
    public async Task ADeviceSignedByBothKeys_MovesToTheNewKey_AndTheChangeIsRecorded()
    {
        var alice = await NewActorAsync("alice");
        var p = Proof(alice);

        await Rotate(alice, p);

        var id = DeviceId.Parse(alice.Dto.DeviceId);
        Assert.Equal(p.Next.PublicKey, await Registry.GetPublicKeyAsync(id));
        var history = await Registry.GetRotationsAsync(id);
        var step = Assert.Single(history);
        Assert.Equal(0, step.Sequence);
        Assert.Equal(alice.Keys.PublicKey, step.OldKey);
        Assert.Equal(p.Next.PublicKey, step.NewKey);
    }

    [Fact]
    public async Task AfterTheChange_OnlyTheNewKeyRegisters_TheOldOneIsRefused()
    {
        var alice = await NewActorAsync("alice");
        var p = Proof(alice);
        await Rotate(alice, p);

        await Assert.ThrowsAsync<HubException>(() => NewActorAsync("again", alice.Dto, alice.Keys)); // the old key: no longer
        var newDto = new DeviceInfoDto
        {
            DeviceId = alice.Dto.DeviceId, DeviceName = "alice", Platform = "Windows",
            PublicKeyBase64 = p.NewKey
        };
        await NewActorAsync("again", newDto, p.Next);                                                  // the new key: yes
    }

    [Fact]
    public async Task ChainsOfChangesAreKeptInOrder()
    {
        var alice = await NewActorAsync("alice");
        var first = Proof(alice);
        await Rotate(alice, first);

        // the same device, now holding the second key
        var dto2 = new DeviceInfoDto { DeviceId = alice.Dto.DeviceId, DeviceName = "alice", Platform = "Windows", PublicKeyBase64 = first.NewKey };
        var again = await NewActorAsync("alice", dto2, first.Next);
        var second = Proof(again);
        await Rotate(again, second);

        var history = await Registry.GetRotationsAsync(DeviceId.Parse(alice.Dto.DeviceId));
        Assert.Equal([0, 1], history.Select(h => h.Sequence));
        Assert.Equal(history[0].NewKey, history[1].OldKey);
    }

    [Fact]
    public async Task WithoutTheOldKeysSignature_NothingChanges()
    {
        var alice = await NewActorAsync("alice");
        var stranger = DeviceKeyGenerator.Generate();
        var next = DeviceKeyGenerator.Generate();
        var payload = KeyRotationProof.BuildPayload(alice.Dto.DeviceId, alice.Keys.PublicKey, next.PublicKey);

        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync("RotateDeviceKey",
            Convert.ToBase64String(next.PublicKey),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(stranger, payload)),   // not the old key
            Convert.ToBase64String(DeviceKeyGenerator.Sign(next, payload)), _cts.Token));

        Assert.Equal(alice.Keys.PublicKey, await Registry.GetPublicKeyAsync(DeviceId.Parse(alice.Dto.DeviceId)));
        Assert.Empty(await Registry.GetRotationsAsync(DeviceId.Parse(alice.Dto.DeviceId)));
    }

    [Fact]
    public async Task WithoutTheNewKeysSignature_NothingChanges()
    {
        var alice = await NewActorAsync("alice");
        var next = DeviceKeyGenerator.Generate();
        var payload = KeyRotationProof.BuildPayload(alice.Dto.DeviceId, alice.Keys.PublicKey, next.PublicKey);

        // Only the old key agrees: a key the new owner never held (or someone else's key) cannot be slipped in.
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync("RotateDeviceKey",
            Convert.ToBase64String(next.PublicKey),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(alice.Keys, payload)),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(alice.Keys, payload)), _cts.Token));

        Assert.Equal(alice.Keys.PublicKey, await Registry.GetPublicKeyAsync(DeviceId.Parse(alice.Dto.DeviceId)));
    }

    [Fact]
    public async Task ASignatureForAnotherDevice_OrAnotherPair_CannotBeReused()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var p = Proof(alice);

        // bob presents alice's valid statement as his own
        await Assert.ThrowsAsync<HubException>(() => Rotate(bob, p));
        // alice presents a statement made for another new key
        var other = Proof(alice);
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync("RotateDeviceKey", other.NewKey, p.OldSig, p.NewSig, _cts.Token));

        Assert.Equal(alice.Keys.PublicKey, await Registry.GetPublicKeyAsync(DeviceId.Parse(alice.Dto.DeviceId)));
        Assert.Equal(bob.Keys.PublicKey, await Registry.GetPublicKeyAsync(DeviceId.Parse(bob.Dto.DeviceId)));
    }

    [Fact]
    public async Task ToTheSameKey_ToGarbage_AndWithoutRegistering_AreAllRefused()
    {
        var alice = await NewActorAsync("alice");
        var same = Proof(alice, alice.Keys);
        await Assert.ThrowsAsync<HubException>(() => Rotate(alice, same));

        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync("RotateDeviceKey",
            Convert.ToBase64String(new byte[] { 1, 2, 3 }), Convert.ToBase64String(new byte[64]), Convert.ToBase64String(new byte[64]), _cts.Token));
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync("RotateDeviceKey", "###", "###", "###", _cts.Token));

        var anon = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        _hubs.Add(anon);
        await anon.StartAsync(_cts.Token);
        var p = Proof(alice);
        await Assert.ThrowsAsync<HubException>(() => anon.InvokeAsync("RotateDeviceKey", p.NewKey, p.OldSig, p.NewSig, _cts.Token));
    }

    [Fact]
    public async Task WhileASessionIsOpen_TheKeyCannotChange()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);

        await Assert.ThrowsAsync<HubException>(() => Rotate(alice, Proof(alice)));
        Assert.Equal(alice.Keys.PublicKey, await Registry.GetPublicKeyAsync(DeviceId.Parse(alice.Dto.DeviceId)));

        await alice.Conn.InvokeAsync("CloseSession", s.SessionId, "done", _cts.Token);
        await Rotate(alice, Proof(alice));   // free again
    }

    [Fact]
    public async Task ChangingTheKeyOverAndOver_IsRateLimited()
    {
        var alice = await NewActorAsync("alice");
        var current = alice;
        var refused = 0;
        for (var i = 0; i < 5; i++)
        {
            var p = Proof(current);
            try
            {
                await Rotate(current, p);
                var dto = new DeviceInfoDto { DeviceId = alice.Dto.DeviceId, DeviceName = "alice", Platform = "Windows", PublicKeyBase64 = p.NewKey };
                current = await NewActorAsync("alice", dto, p.Next);
            }
            catch (HubException) { refused++; }
        }
        Assert.True(refused >= 2); // 3 per hour
    }

    [Fact]
    public async Task TwoRotationsAtOnce_OnlyOneWins()
    {
        var alice = await NewActorAsync("alice");
        // a second connection of the same device (the same old key)
        var alice2 = await NewActorAsync("alice", alice.Dto, alice.Keys);
        var a = Proof(alice);
        var b = Proof(alice2);

        var results = await Task.WhenAll(
            Rotate(alice, a).ContinueWith(t => !t.IsFaulted),
            Rotate(alice2, b).ContinueWith(t => !t.IsFaulted));

        Assert.Single(results, r => r);
        var id = DeviceId.Parse(alice.Dto.DeviceId);
        Assert.Single(await Registry.GetRotationsAsync(id));
    }

    [Fact]
    public async Task APeerGetsTheKeyHistory_OnlyWhileTheyShareASession()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var mallory = await NewActorAsync("mallory");
        var p = Proof(bob);
        await Rotate(bob, p);

        // Bob (new key) is registered again, then alice asks for a session with him.
        var bobNew = await NewActorAsync("bob", new DeviceInfoDto { DeviceId = bob.Dto.DeviceId, DeviceName = "bob", Platform = "Windows", PublicKeyBase64 = p.NewKey }, p.Next);
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync<List<KeyRotationDto>>("GetPeerKeyRotations", s.SessionId, _cts.Token)); // not approved yet
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (bobNew.Incoming.IsEmpty && !wait.IsCancellationRequested) await Task.Delay(10);
        await bobNew.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewScreen, _cts.Token);

        var history = await alice.Conn.InvokeAsync<List<KeyRotationDto>>("GetPeerKeyRotations", s.SessionId, _cts.Token);
        var step = Assert.Single(history);
        Assert.Equal(Convert.ToBase64String(bob.Keys.PublicKey), step.OldKeyBase64);
        Assert.Equal(p.NewKey, step.NewKeyBase64);

        var outsider = await Assert.ThrowsAsync<HubException>(() => mallory.Conn.InvokeAsync<List<KeyRotationDto>>("GetPeerKeyRotations", s.SessionId, _cts.Token));
        var unknown = await Assert.ThrowsAsync<HubException>(() => mallory.Conn.InvokeAsync<List<KeyRotationDto>>("GetPeerKeyRotations", SessionId.New().ToString(), _cts.Token));
        Assert.Equal(unknown.Message, outsider.Message);
    }
}
