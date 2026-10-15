using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteDesktop.Integration.Tests;

public sealed class AuthFlowTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private HubConnection _alice = null!;
    private HubConnection _bob = null!;

    public AuthFlowTests(WebApplicationFactory<Program> factory)
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

    // ── OTP Flow ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateOtp_Returns6DigitCode()
    {
        await TestAuth.RegisterAsync(_bob, MakeDevice("Bob"));

        var code = await _bob.InvokeAsync<string>("GenerateOtp");

        Assert.Equal(6, code.Length);
        Assert.True(int.TryParse(code, out var n) && n is >= 100_000 and <= 999_999);
    }

    [Fact]
    public async Task RequestSessionWithOtp_ValidCode_ReturnsAuthResult()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var code = await _bob.InvokeAsync<string>("GenerateOtp");

        var authResult = await _alice.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bobDto.DeviceId, code);

        Assert.False(string.IsNullOrEmpty(authResult.SessionToken));
        Assert.Equal(nameof(SessionState.EstablishingTransport), authResult.State);
    }

    [Fact]
    public async Task RequestSessionWithOtp_ValidCode_NotifiesTarget()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var establishedTcs = new TaskCompletionSource<AuthResultDto>();
        _bob.On<AuthResultDto>("SessionEstablished", dto => establishedTcs.TrySetResult(dto));

        var code = await _bob.InvokeAsync<string>("GenerateOtp");
        await _alice.InvokeAsync("RequestSessionWithOtp", bobDto.DeviceId, code);

        var established = await establishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(nameof(SessionState.EstablishingTransport), established.State);
    }

    [Fact]
    public async Task RequestSessionWithOtp_InvalidCode_Throws()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        await _bob.InvokeAsync<string>("GenerateOtp");

        await Assert.ThrowsAsync<HubException>(
            () => _alice.InvokeAsync("RequestSessionWithOtp", bobDto.DeviceId, "000000"));
    }

    // ── Approval Flow with permissions and AuthSuccess ────────────────────

    [Fact]
    public async Task RespondToSession_Accept_SendsAuthSuccess()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var incomingTcs = new TaskCompletionSource<SessionDto>();
        _bob.On<SessionDto>("IncomingSessionRequest", dto => incomingTcs.TrySetResult(dto));

        var authTcs = new TaskCompletionSource<AuthResultDto>();
        _alice.On<AuthResultDto>("AuthSuccess", dto => authTcs.TrySetResult(dto));

        await _alice.InvokeAsync("RequestSession", bobDto.DeviceId);
        var incoming = await incomingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var permissions = (long)(Permission.ViewScreen | Permission.ControlMouse);
        await _bob.InvokeAsync("RespondToSession", incoming.SessionId, true, permissions);

        var auth = await authTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrEmpty(auth.SessionToken));
        Assert.Equal(nameof(SessionState.EstablishingTransport), auth.State);
        Assert.Equal(permissions, auth.GrantedPermissions);
    }

    // ── Trusted Device Flow ───────────────────────────────────────────────

    [Fact]
    public async Task RequestSession_TrustedDevice_AutoApproves()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        // Bob trusts Alice
        var permissions = (long)Permission.ViewScreen;
        await _bob.InvokeAsync("AddTrustedDevice", aliceDto.DeviceId, "Alice", permissions);

        var establishedTcs = new TaskCompletionSource<AuthResultDto>();
        _bob.On<AuthResultDto>("SessionEstablished", dto => establishedTcs.TrySetResult(dto));

        var session = await _alice.InvokeAsync<SessionDto>("RequestSession", bobDto.DeviceId);

        // Session should already be EstablishingTransport with a token
        Assert.Equal(nameof(SessionState.EstablishingTransport), session.State);
        Assert.NotNull(session.Auth);
        Assert.False(string.IsNullOrEmpty(session.Auth!.SessionToken));

        // Bob should receive SessionEstablished
        var established = await establishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(nameof(SessionState.EstablishingTransport), established.State);
    }

    [Fact]
    public async Task AddRemoveTrustedDevice_RoundTrip()
    {
        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var peerId = DeviceId.New().ToString();
        await _alice.InvokeAsync("AddTrustedDevice", peerId, "Peer", (long)Permission.ViewOnly);

        var list = await _alice.InvokeAsync<List<TrustedDeviceDto>>("GetTrustedDevices");
        Assert.Contains(list, d => d.DeviceId == peerId);

        await _alice.InvokeAsync("RemoveTrustedDevice", peerId);

        var listAfter = await _alice.InvokeAsync<List<TrustedDeviceDto>>("GetTrustedDevices");
        Assert.DoesNotContain(listAfter, d => d.DeviceId == peerId);
    }

    [Fact]
    public async Task CloseSession_RevokesToken()
    {
        var bobDto = MakeDevice("Bob");
        await TestAuth.RegisterAsync(_bob, bobDto);

        var aliceDto = MakeDevice("Alice");
        await TestAuth.RegisterAsync(_alice, aliceDto);

        var code = await _bob.InvokeAsync<string>("GenerateOtp");
        var authResult = await _alice.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", bobDto.DeviceId, code);

        // Validate token via the service directly
        var tokenSvc = _factory.Services.GetRequiredService<ISessionTokenService>();
        Assert.True(tokenSvc.Validate(authResult.SessionToken,
            Core.SessionId.Parse(authResult.SessionId),
            Core.DeviceId.Parse(aliceDto.DeviceId)));

        await _alice.InvokeAsync("CloseSession", authResult.SessionId, "");

        Assert.False(tokenSvc.Validate(authResult.SessionToken,
            Core.SessionId.Parse(authResult.SessionId),
            Core.DeviceId.Parse(aliceDto.DeviceId)));
    }
}
