using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Core;

namespace RemoteDesktop.Integration.Tests;

public sealed class RelayServiceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RelayServiceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>A real session in EstablishingTransport with a relay token per participant.</summary>
    private sealed record RelaySession(SessionId Id, DeviceId Requester, DeviceId Target, string RequesterToken, string TargetToken);

    private async Task<RelaySession> NewSessionAsync(SessionState state = SessionState.EstablishingTransport)
    {
        var sessions = _factory.Services.GetRequiredService<ISessionManager>();
        var tokens = _factory.Services.GetRequiredService<ISessionTokenService>();
        var requester = DeviceId.New();
        var target = DeviceId.New();
        var s = await sessions.CreateAsync(requester, target);
        await sessions.UpdateStateAsync(s.SessionId, state);
        return new RelaySession(s.SessionId, requester, target,
            tokens.Issue(s.SessionId, requester), tokens.Issue(s.SessionId, target));
    }

    private async Task<WebSocket> ConnectAsync(SessionId session, DeviceId device, string token, CancellationToken ct)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
        return await client.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={session}&deviceId={device}"), ct);
    }

    [Fact]
    public async Task Relay_RequesterAndTarget_PairSuccessfully()
    {
        var s = await NewSessionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsA = await ConnectAsync(s.Id, s.Requester, s.RequesterToken, cts.Token);
        var wsB = await ConnectAsync(s.Id, s.Target, s.TargetToken, cts.Token);

        Assert.Equal(WebSocketState.Open, wsA.State);
        Assert.Equal(WebSocketState.Open, wsB.State);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Relay_SendFromRequester_ReceivedByTarget()
    {
        var s = await NewSessionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsA = await ConnectAsync(s.Id, s.Requester, s.RequesterToken, cts.Token);
        var wsB = await ConnectAsync(s.Id, s.Target, s.TargetToken, cts.Token);

        var payload = new byte[] { 2, 0x10, 0x20, 0x30 };
        await wsA.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);

        var buf = new byte[256];
        var result = await wsB.ReceiveAsync(buf, cts.Token);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payload, buf[..result.Count]);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Relay_SendFromTarget_ReceivedByRequester()
    {
        var s = await NewSessionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsA = await ConnectAsync(s.Id, s.Requester, s.RequesterToken, cts.Token);
        var wsB = await ConnectAsync(s.Id, s.Target, s.TargetToken, cts.Token);

        var payload = new byte[] { 0, 0xAB, 0xCD };
        await wsB.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);

        var buf = new byte[256];
        var result = await wsA.ReceiveAsync(buf, cts.Token);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payload, buf[..result.Count]);

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Relay_InvalidToken_ClosesWithPolicyViolation()
    {
        var s = await NewSessionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ws = await ConnectAsync(s.Id, s.Requester, "invalid-token", cts.Token);

        var result = await ws.ReceiveAsync(new byte[256], cts.Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
    }

    [Fact]
    public async Task Relay_ReportConnectionMode_TransitionsSessionToConnected()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        try
        {
            await connection.StartAsync();
            var sessionMgr = _factory.Services.GetRequiredService<ISessionManager>();

            var device = TestAuth.MakeDevice("TestDevice");
            await TestAuth.RegisterAsync(connection, device);
            var deviceId = DeviceId.Parse(device.DeviceId);

            // An approved session in the transport-establishing phase.
            var session = await sessionMgr.CreateAsync(deviceId, DeviceId.New());
            await sessionMgr.UpdateStateAsync(session.SessionId, SessionState.EstablishingTransport);

            await connection.InvokeAsync("ReportConnectionMode", session.SessionId.ToString(), "Relay");

            var updated = await sessionMgr.GetAsync(session.SessionId);
            Assert.Equal(SessionState.Connected, updated!.State);
            Assert.Equal(ConnectionMode.Relay, updated.ConnectionMode);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
