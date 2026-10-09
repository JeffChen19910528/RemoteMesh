using RemoteDesktop.Client;
using RemoteDesktop.Core;

namespace RemoteDesktop.Agent;

/// <summary>
/// Approval for a machine nobody is sitting at: a device on the configured list gets exactly the rights listed
/// for it, anyone else is declined. There is deliberately no "accept everything" mode.
/// </summary>
public sealed class ConfigApprovalHandler : IApprovalHandler
{
    private readonly Dictionary<DeviceId, Permission> _trusted = new();

    public ConfigApprovalHandler(IEnumerable<TrustedPeer> peers)
    {
        foreach (var peer in peers)
        {
            if (DeviceId.TryParse(peer.DeviceId ?? string.Empty, out var id)
                && Enum.TryParse<Permission>(peer.Permissions, ignoreCase: true, out var permissions))
            {
                // Restart/shutdown are never grantable from a standing list.
                _trusted[id] = permissions & Permission.FullControl;
            }
        }
    }

    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct) =>
        Task.FromResult(_trusted.TryGetValue(request.RequesterDeviceId, out var granted) && granted != Permission.None
            ? new ApprovalDecision(true, granted)
            : ApprovalDecision.Deny);
}
