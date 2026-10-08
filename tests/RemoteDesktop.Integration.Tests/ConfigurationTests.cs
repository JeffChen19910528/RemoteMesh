using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Relay;

namespace RemoteDesktop.Integration.Tests;

/// <summary>The documented configuration is the real configuration: it binds, validates and contains no secrets.</summary>
public sealed class ConfigurationTests
{
    private static WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private static void AssertSameValues(object expected, object actual)
    {
        foreach (var p in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.True(Equals(p.GetValue(expected), p.GetValue(actual)),
                $"{expected.GetType().Name}.{p.Name}: appsettings.json says {p.GetValue(actual)}, code default is {p.GetValue(expected)}");
    }

    [Fact]
    public async Task AppSettingsDefaults_MatchTheCodeDefaults_SoTheDocumentedValuesAreTrue()
    {
        await using var f = With();

        AssertSameValues(new SessionSecurityOptions(), f.Services.GetRequiredService<SessionSecurityOptions>());
        AssertSameValues(new SessionRecoveryOptions(), f.Services.GetRequiredService<SessionRecoveryOptions>());
        AssertSameValues(new RelayOptions(), f.Services.GetRequiredService<RelayOptions>());
    }

    [Fact]
    public async Task Settings_AreAppliedFromConfiguration_IncludingTimeSpans()
    {
        await using var f = With(
            ("RemoteMesh:Session:MaxTotalActiveSessions", "42"),
            ("RemoteMesh:Session:ApprovalTimeout", "00:00:45"),
            ("RemoteMesh:Recovery:Window", "00:03:00"),
            ("RemoteMesh:Relay:MaxConcurrentPairings", "7"),
            ("RemoteMesh:Relay:MaxBytesPerSecond", "0"));

        var session = f.Services.GetRequiredService<SessionSecurityOptions>();
        var relay = f.Services.GetRequiredService<RelayOptions>();

        Assert.Equal(42, session.MaxTotalActiveSessions);
        Assert.Equal(TimeSpan.FromSeconds(45), session.ApprovalTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), f.Services.GetRequiredService<SessionRecoveryOptions>().Window);
        Assert.Equal(7, relay.MaxConcurrentPairings);
        Assert.Equal(0, relay.MaxBytesPerSecond);
        // Untouched settings keep their defaults.
        Assert.Equal(new SessionSecurityOptions().MaxPendingRequestsPerTarget, session.MaxPendingRequestsPerTarget);
    }

    [Theory]
    [InlineData("RemoteMesh:Session:ApprovalTimeout", "00:00:00")]
    [InlineData("RemoteMesh:Session:ApprovalTimeout", "-00:01:00")]
    [InlineData("RemoteMesh:Session:MaxTotalActiveSessions", "0")]
    [InlineData("RemoteMesh:Session:MaxActiveSessionsPerDevice", "-3")]
    [InlineData("RemoteMesh:Session:RelayTokenLifetime", "1.00:00:00")]   // tokens must be short-lived
    [InlineData("RemoteMesh:Recovery:Window", "00:00:00")]
    [InlineData("RemoteMesh:Recovery:MaxResumeAttemptsPerMinute", "0")]
    [InlineData("RemoteMesh:Relay:MaxMessageBytes", "10")]
    [InlineData("RemoteMesh:Relay:MaxBytesPerSecond", "-1")]
    [InlineData("RemoteMesh:Relay:MaxConcurrentPairings", "0")]
    public void InvalidSettings_StopTheServerAtStartup(string key, string value)
    {
        using var f = With((key, value));

        var ex = Assert.ThrowsAny<Exception>(() => f.Services);
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        Assert.IsType<InvalidOperationException>(root);
        Assert.Contains("Invalid configuration", root.Message);
    }

    [Fact]
    public void Validation_ReportsEveryProblemAtOnce()
    {
        var bad = new SessionSecurityOptions { MaxTotalActiveSessions = 0, ApprovalTimeout = TimeSpan.Zero, MaxDeviceNameLength = -1 };

        var ex = Assert.Throws<InvalidOperationException>(bad.Validate);

        Assert.Contains("MaxTotalActiveSessions", ex.Message);
        Assert.Contains("ApprovalTimeout", ex.Message);
        Assert.Contains("MaxDeviceNameLength", ex.Message);
    }

    [Fact]
    public void ShippedDefaults_Validate()
    {
        new SessionSecurityOptions().Validate();
        new SessionRecoveryOptions().Validate();
        new RelayOptions().Validate();
    }

    [Fact]
    public void ShippedConfigFiles_ContainNoSecrets()
    {
        var dir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var files = Directory.GetFiles(dir, "appsettings*.json");
        Assert.NotEmpty(files);

        var secretish = new Regex("\"[^\"]*(password|secret|connectionstring|apikey|privatekey|token)[^\"]*\"\\s*:", RegexOptions.IgnoreCase);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // RelayTokenLifetime / RelayTokenRequestsPerMinute are limits, not credentials.
            var offenders = secretish.Matches(text).Select(m => m.Value)
                .Where(v => !v.Contains("RelayToken", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(offenders.Count == 0, $"{Path.GetFileName(file)} has credential-like keys: {string.Join(", ", offenders)}");
        }
    }

    [Fact]
    public async Task InsecureTransportSwitch_IsOffByDefault()
    {
        await using var f = With();
        var config = f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        Assert.Equal("False", config["Security:AllowInsecureTransport"], ignoreCase: true);
    }
}

/// <summary>Per-address limits must see the real client behind a proxy — but only when the proxy is trusted.</summary>
public sealed class ForwardedHeadersTests
{
    /// <summary>Plays the network hop: the TCP peer the server sees is <paramref name="proxyAddress"/>.</summary>
    private sealed class PeerFilter(string proxyAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(proxyAddress);
                return nxt(ctx);
            });
            next(app);
        };
    }

    private static WebApplicationFactory<Program> Create(string peer, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("RemoteMesh:Relay:UnauthorizedAttemptsPerAddress", "3");
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new PeerFilter(peer)));
        });

    private static async Task BadRelayAttemptsAsync(WebApplicationFactory<Program> f, string forwardedFor, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = f.Server.CreateWebSocketClient();
            c.ConfigureRequest = r =>
            {
                r.Headers["X-Relay-Token"] = "bad-" + i;
                r.Headers["X-Forwarded-For"] = forwardedFor;
                r.Headers["X-Forwarded-Proto"] = "https";
            };
            var ws = await c.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={Guid.NewGuid()}&deviceId={Guid.NewGuid()}"), CancellationToken.None);
            await ws.ReceiveAsync(new byte[8], CancellationToken.None);
        }
    }

    private static bool Locked(WebApplicationFactory<Program> f, string ip) =>
        f.Services.GetRequiredService<IRateLimiter>().IsLockedOut($"relayfail:{ip}", 3, TimeSpan.FromMinutes(10));

    [Fact]
    public async Task TrustedProxy_LimitsApplyToTheRealClient()
    {
        await using var f = Create("10.0.0.5",
            ("RemoteMesh:ForwardedHeaders:Enabled", "true"),
            ("RemoteMesh:ForwardedHeaders:KnownProxies:0", "10.0.0.5"));

        await BadRelayAttemptsAsync(f, "203.0.113.9", 3);

        Assert.True(Locked(f, "203.0.113.9"));   // the client behind the proxy is locked out…
        Assert.False(Locked(f, "10.0.0.5"));     // …not everyone sharing the proxy
    }

    [Fact]
    public async Task ProxyNetworks_AreSupported()
    {
        await using var f = Create("10.0.0.77",
            ("RemoteMesh:ForwardedHeaders:Enabled", "true"),
            ("RemoteMesh:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/24"));

        await BadRelayAttemptsAsync(f, "198.51.100.4", 3);

        Assert.True(Locked(f, "198.51.100.4"));
    }

    [Fact]
    public async Task UntrustedSender_CannotForgeItsAddress()
    {
        await using var f = Create("192.0.2.1", // not a listed proxy
            ("RemoteMesh:ForwardedHeaders:Enabled", "true"),
            ("RemoteMesh:ForwardedHeaders:KnownProxies:0", "10.0.0.5"));

        await BadRelayAttemptsAsync(f, "203.0.113.9", 3);

        Assert.True(Locked(f, "192.0.2.1"));     // counted against who it really is
        Assert.False(Locked(f, "203.0.113.9"));  // the forged address is ignored
    }

    [Fact]
    public async Task ByDefault_ForwardedHeadersAreIgnored()
    {
        await using var f = Create("10.0.0.5");

        await BadRelayAttemptsAsync(f, "203.0.113.9", 3);

        Assert.True(Locked(f, "10.0.0.5"));
        Assert.False(Locked(f, "203.0.113.9"));
    }

    [Fact]
    public void Enabled_WithoutAnyKnownProxy_StopsTheServer()
    {
        using var f = Create("10.0.0.5", ("RemoteMesh:ForwardedHeaders:Enabled", "true"));

        var ex = Assert.ThrowsAny<Exception>(() => f.Services);
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        Assert.Contains("KnownProxies", root.Message);
    }

    [Theory]
    [InlineData("RemoteMesh:ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("RemoteMesh:ForwardedHeaders:KnownNetworks:0", "10.0.0.0")]
    [InlineData("RemoteMesh:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99")]
    public void MalformedProxyEntries_StopTheServer(string key, string value)
    {
        using var f = Create("10.0.0.5", ("RemoteMesh:ForwardedHeaders:Enabled", "true"), (key, value));

        Assert.ThrowsAny<Exception>(() => f.Services);
    }
}

/// <summary>Outside development everything except /health must arrive over TLS.</summary>
public sealed class TransportSecurityTests
{
    private static WebApplicationFactory<Program> Create(string environment, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private static Task<HttpResponseMessage> Negotiate(HttpClient c, bool forwardedHttps = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/hubs/signaling/negotiate?negotiateVersion=1");
        if (forwardedHttps) req.Headers.Add("X-Forwarded-Proto", "https");
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Production_RefusesSignalingOverPlainHttp_ButKeepsHealthOpenForProbes()
    {
        await using var f = Create("Production");
        using var c = f.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Negotiate(c)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.GetAsync("/hubs/signaling")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.GetAsync("/ws/relay")).StatusCode);
    }

    [Fact]
    public async Task Production_AcceptsTls_AsSeenThroughATrustedProxy()
    {
        await using var f = Create("Production",
            ("RemoteMesh:ForwardedHeaders:Enabled", "true"),
            ("RemoteMesh:ForwardedHeaders:KnownProxies:0", "127.0.0.1"));
        // TestServer reports no peer address, so pretend the proxy is the TCP peer.
        await using var withPeer = f.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IStartupFilter>(new ProxyPeer())));
        using var c = withPeer.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await Negotiate(c, forwardedHttps: true)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Negotiate(c, forwardedHttps: false)).StatusCode);
    }

    private sealed class ProxyPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) => { ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback; return nxt(ctx); });
            next(app);
        };
    }

    [Fact]
    public async Task UntrustedClient_CannotClaimHttpsByForgingTheHeader()
    {
        await using var f = Create("Production"); // forwarded headers not enabled
        using var c = f.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Negotiate(c, forwardedHttps: true)).StatusCode);
    }

    [Fact]
    public async Task ExplicitOptOut_AllowsPlainHttp()
    {
        await using var f = Create("Production", ("Security:AllowInsecureTransport", "true"));
        using var c = f.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await Negotiate(c)).StatusCode);
    }

    [Fact]
    public async Task Development_AllowsPlainHttp()
    {
        await using var f = Create("Development");
        using var c = f.CreateClient();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await Negotiate(c)).StatusCode);
    }
}
