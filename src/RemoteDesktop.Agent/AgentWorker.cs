using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Core;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Agent;

public sealed class AgentWorker : BackgroundService
{
    private readonly ISignalingClient _signaling;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(ISignalingClient signaling, ILogger<AgentWorker> logger)
    {
        _signaling = signaling;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RemoteDesktop Agent starting");
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Agent terminated unexpectedly");
            throw;
        }
        finally
        {
            _logger.LogInformation("RemoteDesktop Agent stopped");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Not implemented yet: connect to signaling, register device, handle incoming sessions
        await Task.Delay(Timeout.Infinite, ct);
    }
}
