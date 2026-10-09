using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteDesktop.Client;
using RemoteDesktop.Platform;

namespace RemoteDesktop.Agent;

/// <summary>
/// Keeps this machine reachable: it registers with the server and lets the devices on the configured list
/// connect. Run it inside the logged-in user's session — a Windows service lives in session 0 and cannot see
/// the user's screen.
/// </summary>
public sealed class AgentWorker(IOptions<AgentOptions> options, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            foreach (var p in problems) logger.LogCritical("Invalid configuration: {Problem}", p);
            throw new InvalidOperationException("The agent configuration is invalid; see the log.");
        }

        var client = new RemoteMeshClient(
            new ClientOptions
            {
                ServerUri = new Uri(settings.ServerUrl),
                DeviceName = settings.DeviceName,
                DataDirectory = string.IsNullOrWhiteSpace(settings.DataDirectory) ? null : settings.DataDirectory,
                JpegCodec = PlatformHost.CreateJpegCodec(),
                VideoBudgetKbps = settings.VideoBudgetKbps
            },
            BuildHostServices(settings),
            new ConfigApprovalHandler(settings.TrustedPeers));

        // Log events, never contents: no tokens, keys, clipboard text or file names.
        client.HostSessionStarted += s =>
        {
            logger.LogInformation("Session {SessionId} started with rights {Rights}", s.SessionId, s.Granted);
            s.Ended += reason => logger.LogInformation("Session {SessionId} ended: {Reason}", s.SessionId, reason);
        };
        client.Error += (what, ex) => logger.LogWarning("{What} failed: {Error}", what, ex.GetType().Name);

        await using (client)
        {
            await client.StartAsync(stoppingToken);
            logger.LogInformation("Agent ready. Device ID: {DeviceId} (server {Server})", client.DeviceId, settings.ServerUrl);
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
        }
        logger.LogInformation("Agent stopped");
    }

    private HostServices BuildHostServices(AgentOptions settings)
    {
        var fileRoot = string.IsNullOrWhiteSpace(settings.FileRoot) ? null : settings.FileRoot;
        if (fileRoot is not null) Directory.CreateDirectory(fileRoot);

        var setup = PlatformHost.CreateHostServices(settings.AllowClipboard, fileRoot);
        foreach (var notice in setup.Notices) logger.LogWarning("{Notice}", notice);
        return setup.Services;
    }
}
