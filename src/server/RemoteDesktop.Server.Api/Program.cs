using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Identity;
using RemoteDesktop.Server.Infrastructure;
using RemoteDesktop.Server.Presence;
using RemoteDesktop.Server.Relay;
using RemoteDesktop.Server.Security;
using RemoteDesktop.Server.Session;
using RemoteDesktop.Server.Signaling;

var builder = WebApplication.CreateBuilder(args);

// Run as a proper service when installed as one (no effect when started from a console).
builder.Host.UseWindowsService(o => o.ServiceName = "RemoteMeshServer");
builder.Host.UseSystemd();

builder.Services.AddOpenApi();

// Core server services (all singletons — to be replaced by DB-backed implementations)
builder.Services.AddSingleton<IDeviceRegistry, InMemoryDeviceRegistry>();
builder.Services.AddSingleton<ISessionManager, InMemorySessionManager>();
builder.Services.AddSingleton<IPresenceService, InMemoryPresenceService>();
builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();

// Authentication & Authorization
builder.Services.AddSingleton<IOtpService, OtpService>();
builder.Services.AddSingleton<ISessionTokenService, SessionTokenService>();
builder.Services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();
builder.Services.AddSingleton<ITrustedDeviceStore, InMemoryTrustedDeviceStore>();

// Configuration: limits come from appsettings / environment (RemoteMesh__Session__MaxTotalActiveSessions, ...).
// Defaults are secure; a bad value stops the server at startup instead of misbehaving later. No secrets live here.
var settings = builder.Configuration.GetSection("RemoteMesh");
var sessionSecurity = settings.GetSection("Session").Get<SessionSecurityOptions>() ?? new SessionSecurityOptions();
var recovery = settings.GetSection("Recovery").Get<SessionRecoveryOptions>() ?? new SessionRecoveryOptions();
var relayConfig = settings.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
sessionSecurity.Validate();
recovery.Validate();
relayConfig.Validate();
builder.Services.AddSingleton(sessionSecurity);
builder.Services.AddSingleton(recovery);
builder.Services.AddSingleton(relayConfig);

var forwarded = settings.GetSection("ForwardedHeaders").Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
forwarded.Validate();
if (forwarded.Enabled)
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var p in forwarded.KnownProxies) o.KnownProxies.Add(System.Net.IPAddress.Parse(p));
        foreach (var n in forwarded.KnownNetworks)
        {
            ForwardedHeadersSettings.TryParseNetwork(n, out var address, out var prefix);
            o.KnownIPNetworks.Add(new System.Net.IPNetwork(address, prefix));
        }
    });
}

// Device authentication (proof of private-key possession on registration)
builder.Services.AddSingleton<IDeviceAuthenticator, DeviceAuthenticator>();

// Relay
builder.Services.AddSingleton<IRelayService, InMemoryRelayService>();

// Signaling (SignalR hub + connection tracker)
builder.Services.AddSignaling();

var app = builder.Build();

// Behind a trusted proxy the real client address and scheme come from X-Forwarded-*; this must run first so the
// rate limits, lockouts and the TLS check below see the client, not the proxy.
if (forwarded.Enabled) app.UseForwardedHeaders();

// Transport security: everything except local development must be TLS (wss for the relay). HSTS tells browsers
// and well-behaved clients never to downgrade.
var requireTls = !app.Environment.IsDevelopment()
                 && !app.Configuration.GetValue("Security:AllowInsecureTransport", false);
if (requireTls)
{
    app.UseHsts();
    app.UseHttpsRedirection(); // redirects when the server knows its HTTPS port

    // Everything else must arrive over TLS: the signaling hub carries session and recovery tokens, so plain
    // HTTP/WebSocket is refused (not merely discouraged). /health stays open for load-balancer probes.
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("TLS required.");
            return;
        }
        await next(context);
    });
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapSignalingHub();

// WebSocket relay endpoint: both session peers connect here; relay forwards end-to-end encrypted bytes.
app.Map("/ws/relay", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (requireTls && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Throttle by client address before doing any work: bad guesses and connection floods are cheap to refuse.
    var limiter = context.RequestServices.GetRequiredService<IRateLimiter>();
    var relayOptions = context.RequestServices.GetRequiredService<RelayOptions>(); // the live (possibly overridden) options
    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var failKey = $"relayfail:{ip}";
    if (limiter.IsLockedOut(failKey, relayOptions.UnauthorizedAttemptsPerAddress, TimeSpan.FromMinutes(10))
        || !limiter.TryConsume($"relayconn:{ip}", relayOptions.ConnectionsPerMinutePerAddress, TimeSpan.FromMinutes(1)))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    var q = context.Request.Query;
    // The token travels in a header (URLs end up in proxy and access logs); the query form is a fallback.
    var token = context.Request.Headers["X-Relay-Token"].FirstOrDefault()
                ?? (q.TryGetValue("token", out var tokenVal) ? tokenVal.ToString() : null);

    if (!q.TryGetValue("sessionId", out var sidVal)
        || !q.TryGetValue("deviceId", out var didVal)
        || string.IsNullOrEmpty(token) || token.Length > 128
        || !SessionId.TryParse(sidVal.ToString(), out var sessionId)
        || !DeviceId.TryParse(didVal.ToString(), out var deviceId))
    {
        limiter.TryConsume(failKey, relayOptions.UnauthorizedAttemptsPerAddress, TimeSpan.FromMinutes(10));
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var relay = context.RequestServices.GetRequiredService<IRelayService>();
    var ws = await context.WebSockets.AcceptWebSocketAsync();
    var outcome = await relay.HandleConnectionAsync(ws, sessionId, deviceId, token, context.RequestAborted);
    if (outcome == RelayOutcome.Unauthorized)
        limiter.TryConsume(failKey, relayOptions.UnauthorizedAttemptsPerAddress, TimeSpan.FromMinutes(10));
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }))
   .WithName("HealthCheck");

app.Run();

// Expose Program for WebApplicationFactory in integration tests
public partial class Program { }
