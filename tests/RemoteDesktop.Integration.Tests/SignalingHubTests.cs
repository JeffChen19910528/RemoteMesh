using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;

namespace RemoteDesktop.Integration.Tests;

public sealed class SignalingHubTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;

    public SignalingHubTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _alice = BuildConnection();
        _bob = BuildConnection();
        await _alice.StartAsync();
        await _bob.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
    }

    private HubConnection BuildConnection() =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/signaling", o =>
                { o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler(); o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; })
            .Build();

    private static DeviceInfoDto MakeDevice(string name) => TestAuth.MakeDevice(name);

    [Fact]
    public async Task RegisterDevice_Succeeds()
    {
        await TestAuth.RegisterAsync(_alice, MakeDevice("Alice"));
        // No exception = success
    }

    [Fact]
    public async Task RequestSession_ReturnsSession()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var session = await _alice.InvokeAsync<SessionDto>("RequestSession", bobDto.DeviceId);

        Assert.False(string.IsNullOrEmpty(session.SessionId));
        Assert.Equal(aliceDto.DeviceId, session.RequesterDeviceId);
        Assert.Equal(bobDto.DeviceId, session.TargetDeviceId);
    }

    [Fact]
    public async Task RequestSession_NotifiesTarget()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var incomingTcs = new TaskCompletionSource<SessionDto>();
        _bob.On<SessionDto>("IncomingSessionRequest", dto => incomingTcs.TrySetResult(dto));

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        await _alice.InvokeAsync("RequestSession", bobDto.DeviceId);

        var incoming = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(aliceDto.DeviceId, incoming.RequesterDeviceId);
    }

    [Fact]
    public async Task RespondToSession_Accept_NotifiesRequester()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        // Bob waits for incoming
        var incomingTcs = new TaskCompletionSource<SessionDto>();
        _bob.On<SessionDto>("IncomingSessionRequest", dto => incomingTcs.TrySetResult(dto));

        // Alice waits for state update
        var stateTcs = new TaskCompletionSource<SessionDto>();
        _alice.On<SessionDto>("SessionStateChanged", dto => stateTcs.TrySetResult(dto));

        var session = await _alice.InvokeAsync<SessionDto>("RequestSession", bobDto.DeviceId);
        var incoming = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _bob.InvokeAsync("RespondToSession", incoming.SessionId, true, (long)Permission.ViewOnly);

        var update = await stateTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(nameof(SessionState.Authorizing), update.State);
    }

    [Fact]
    public async Task RespondToSession_Reject_NotifiesRequester()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var incomingTcs = new TaskCompletionSource<SessionDto>();
        _bob.On<SessionDto>("IncomingSessionRequest", dto => incomingTcs.TrySetResult(dto));

        var stateTcs = new TaskCompletionSource<SessionDto>();
        _alice.On<SessionDto>("SessionStateChanged", dto => stateTcs.TrySetResult(dto));

        await _alice.InvokeAsync("RequestSession", bobDto.DeviceId);
        var incoming = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _bob.InvokeAsync("RespondToSession", incoming.SessionId, false, 0L);

        var update = await stateTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(nameof(SessionState.Failed), update.State);
    }

    [Fact]
    public async Task Heartbeat_DoesNotThrow()
    {
        await TestAuth.RegisterAsync(_alice, MakeDevice("Alice"));
        await _alice.InvokeAsync("Heartbeat");
        // No exception = success
    }

    [Fact]
    public async Task RequestSession_UnregisteredDevice_Throws()
    {
        // Alice not registered — should get HubException
        await Assert.ThrowsAsync<HubException>(
            () => _alice.InvokeAsync("RequestSession", DeviceId.New().ToString()));
    }
}
