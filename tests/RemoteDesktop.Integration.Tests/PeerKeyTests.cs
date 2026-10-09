using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;

namespace RemoteDesktop.Integration.Tests;

/// <summary>
/// GetPeerPublicKey hands a participant the other side's pinned key (needed for the end-to-end key agreement).
/// It must not become a way to look up arbitrary devices' keys or to probe sessions.
/// </summary>
public sealed class PeerKeyTests : IAsyncLifetime
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

    private sealed record Actor(HubConnection Conn, DeviceInfoDto Dto, ConcurrentQueue<SessionDto> Incoming,
        ConcurrentQueue<AuthResultDto> Authorized);

    private async Task<Actor> NewActorAsync(string name)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        _hubs.Add(conn);
        var actor = new Actor(conn, TestAuth.MakeDevice(name), new(), new());
        conn.On<SessionDto>("IncomingSessionRequest", actor.Incoming.Enqueue);
        conn.On<AuthResultDto>("SessionAuthorized", actor.Authorized.Enqueue);
        await conn.StartAsync(_cts.Token);
        await TestAuth.RegisterAsync(conn, actor.Dto);
        return actor;
    }

    private static async Task Eventually(Func<bool> cond)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.True(cond(), "condition not reached in time");
    }

    [Fact]
    public async Task Participants_GetEachOthersPinnedKey_OnlyAfterApproval()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);

        // Still waiting for the target's decision: nothing is handed out yet.
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync<string>("GetPeerPublicKey", s.SessionId, _cts.Token));
        await Eventually(() => !bob.Incoming.IsEmpty);
        await Assert.ThrowsAsync<HubException>(() => bob.Conn.InvokeAsync<string>("GetPeerPublicKey", s.SessionId, _cts.Token));

        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewScreen, _cts.Token);
        await Eventually(() => !bob.Authorized.IsEmpty);

        Assert.Equal(bob.Dto.PublicKeyBase64, await alice.Conn.InvokeAsync<string>("GetPeerPublicKey", s.SessionId, _cts.Token));
        Assert.Equal(alice.Dto.PublicKeyBase64, await bob.Conn.InvokeAsync<string>("GetPeerPublicKey", s.SessionId, _cts.Token));
    }

    [Fact]
    public async Task PeerIdentity_NamesTheOtherDeviceAndItsKey_ToParticipantsOnly()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var mallory = await NewActorAsync("mallory");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", s.SessionId, _cts.Token)); // not approved yet
        await Eventually(() => !bob.Incoming.IsEmpty);
        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewScreen, _cts.Token);

        var seenByAlice = await alice.Conn.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", s.SessionId, _cts.Token);
        var seenByBob = await bob.Conn.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", s.SessionId, _cts.Token);
        Assert.Equal(bob.Dto.DeviceId, seenByAlice.DeviceId);
        Assert.Equal(bob.Dto.PublicKeyBase64, seenByAlice.PublicKeyBase64);
        Assert.Equal(alice.Dto.DeviceId, seenByBob.DeviceId);
        Assert.Equal(alice.Dto.PublicKeyBase64, seenByBob.PublicKeyBase64);

        var outsider = await Assert.ThrowsAsync<HubException>(() => mallory.Conn.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", s.SessionId, _cts.Token));
        var unknown = await Assert.ThrowsAsync<HubException>(() => mallory.Conn.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", SessionId.New().ToString(), _cts.Token));
        Assert.Equal(unknown.Message, outsider.Message); // no existence oracle here either
    }

    [Fact]
    public async Task Outsider_And_UnknownSession_AreIndistinguishable()
    {
        var alice = await NewActorAsync("alice");
        var bob = await NewActorAsync("bob");
        var mallory = await NewActorAsync("mallory");
        var s = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId, _cts.Token);
        await Eventually(() => !bob.Incoming.IsEmpty);
        await bob.Conn.InvokeAsync("RespondToSession", s.SessionId, true, (long)Permission.ViewScreen, _cts.Token);

        var outsider = await Assert.ThrowsAsync<HubException>(() =>
            mallory.Conn.InvokeAsync<string>("GetPeerPublicKey", s.SessionId, _cts.Token));
        var unknown = await Assert.ThrowsAsync<HubException>(() =>
            mallory.Conn.InvokeAsync<string>("GetPeerPublicKey", SessionId.New().ToString(), _cts.Token));
        Assert.Equal(unknown.Message, outsider.Message); // no existence oracle
    }

    [Fact]
    public async Task UnregisteredConnection_IsRefused_AndGarbageIdIsRejected()
    {
        var anon = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        _hubs.Add(anon);
        await anon.StartAsync(_cts.Token);
        await Assert.ThrowsAsync<HubException>(() => anon.InvokeAsync<string>("GetPeerPublicKey", SessionId.New().ToString(), _cts.Token));

        var alice = await NewActorAsync("alice");
        await Assert.ThrowsAsync<HubException>(() => alice.Conn.InvokeAsync<string>("GetPeerPublicKey", "not-a-session", _cts.Token));
    }
}
