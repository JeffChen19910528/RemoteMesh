using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Signaling;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Integration.Tests;

public sealed class SignalingClientRecoveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Fails every request while Broken, simulating a network outage between client and server.</summary>
    private sealed class FlakyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public static volatile bool Broken;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Broken ? throw new HttpRequestException("simulated outage") : base.SendAsync(request, ct);
    }

    private readonly WebApplicationFactory<Program> _factory;

    public SignalingClientRecoveryTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private SignalingClient NewClient() => new(o =>
    {
        o.HttpMessageHandlerFactory = _ => new FlakyHandler(_factory.Server.CreateHandler());
        o.Transports = HttpTransportType.LongPolling;
    });

    private static async Task WaitUntil(Func<bool> cond, int seconds = 20)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!cond() && !cts.IsCancellationRequested) await Task.Delay(20);
        Assert.True(cond());
    }

    [Fact]
    public async Task Client_StoresRecoveryToken_AndResumesExplicitly()
    {
        FlakyHandler.Broken = false;
        var sessions = _factory.Services.GetRequiredService<ISessionManager>();
        await using var alice = NewClient();
        var aliceInfo = new DeviceInfo { DeviceId = DeviceId.New(), DeviceName = "A", Platform = "Test" };
        var aliceSigner = new DeviceKeyPairSigner(DeviceKeyGenerator.Generate());
        var bobDto = MakeBobDto();
        var bob = BuildRaw();
        try
        {
            await alice.ConnectAsync(new Uri("http://localhost/hubs/signaling"), aliceInfo.DeviceId);
            await alice.RegisterDeviceAsync(aliceInfo, aliceSigner);
            var session = await EstablishAsync(alice, bob, bobDto);

            await WaitUntil(() => alice.HasRecoveryToken(session.SessionId));
            var resumed = await alice.ResumeSessionAsync(session.SessionId); // already Connected: idempotent

            Assert.Equal(SessionState.Connected, resumed.State);
            Assert.Equal(SessionState.Connected, (await sessions.GetAsync(session.SessionId))!.State);
        }
        finally { await bob.DisposeAsync(); }
    }

    [Fact]
    public async Task Client_WithoutToken_CannotResume()
    {
        FlakyHandler.Broken = false;
        await using var alice = NewClient();
        await alice.ConnectAsync(new Uri("http://localhost/hubs/signaling"), DeviceId.New());

        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.ResumeSessionAsync(SessionId.New()));
    }

    [Fact]
    public async Task Client_AfterTransportOutage_ReconnectsReRegistersAndResumes()
    {
        FlakyHandler.Broken = false;
        var tracker = _factory.Services.GetRequiredService<IConnectionTracker>();
        await using var alice = NewClient();
        var aliceSigner = new DeviceKeyPairSigner(DeviceKeyGenerator.Generate());
        var aliceInfo = new DeviceInfo { DeviceId = DeviceId.New(), DeviceName = "A", Platform = "Test" };
        var bobDto = MakeBobDto();
        var bob = BuildRaw();
        var recovered = new TaskCompletionSource<(SessionId, bool)>();
        alice.SessionRecoveryAttempted += (id, ok) => recovered.TrySetResult((id, ok));
        try
        {
            await alice.ConnectAsync(new Uri("http://localhost/hubs/signaling"), aliceInfo.DeviceId);
            await alice.RegisterDeviceAsync(aliceInfo, aliceSigner);
            var session = await EstablishAsync(alice, bob, bobDto);
            await WaitUntil(() => alice.HasRecoveryToken(session.SessionId));
            var connectionBefore = tracker.GetConnectionId(aliceInfo.DeviceId);

            FlakyHandler.Broken = true;
            // The in-flight long poll is unaffected; the next send hits the outage and drops the connection.
            try { await alice.HeartbeatAsync(); } catch (Exception) { /* expected */ }
            await WaitUntil(() => !alice.IsConnected);   // the outage is noticed
            FlakyHandler.Broken = false;                 // network is back; the client's retry succeeds

            var (id, ok) = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(session.SessionId, id);
            Assert.True(ok);
            Assert.True(alice.IsConnected);
            Assert.NotEqual(connectionBefore, tracker.GetConnectionId(aliceInfo.DeviceId)); // re-registered on the new connection
        }
        finally
        {
            FlakyHandler.Broken = false;
            await bob.DisposeAsync();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private DeviceInfoDto MakeBobDto() => TestAuth.MakeDevice("Bob");

    private HubConnection BuildRaw() => new HubConnectionBuilder()
        .WithUrl("http://localhost/hubs/signaling", o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
        })
        .Build();

    /// <summary>Alice requests; Bob (raw connection) accepts and reports the transport, which issues recovery tokens.</summary>
    private static async Task<SessionInfo> EstablishAsync(SignalingClient alice, HubConnection bob, DeviceInfoDto bobDto)
    {
        var incoming = new TaskCompletionSource<SessionDto>();
        bob.On<SessionDto>("IncomingSessionRequest", d => incoming.TrySetResult(d));
        await bob.StartAsync();
        await TestAuth.RegisterAsync(bob, bobDto);

        var session = await alice.RequestSessionAsync(DeviceId.Parse(bobDto.DeviceId));
        var req = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await bob.InvokeAsync("RespondToSession", req.SessionId, true, (long)Permission.ViewOnly);
        await bob.InvokeAsync("ReportConnectionMode", req.SessionId, "Direct");
        return session;
    }
}
