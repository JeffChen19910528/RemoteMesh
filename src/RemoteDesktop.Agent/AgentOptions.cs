using RemoteDesktop.Core;

namespace RemoteDesktop.Agent;

/// <summary>Bound from the <c>Agent</c> section of appsettings.json / environment (<c>Agent__ServerUrl</c>, ...).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Base address of the RemoteMesh server, for example <c>https://relay.example.com</c>. Required.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Where this device keeps its identity and key. Empty = the per-user default.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>The only folder remote file transfers can reach. Empty = file transfer is off.</summary>
    public string? FileRoot { get; set; }

    /// <summary>Let a connected viewer use this machine's clipboard (still limited by the rights granted per session).</summary>
    public bool AllowClipboard { get; set; }

    /// <summary>The most bandwidth the screen picture may use while somebody watches (kilobits per second, 500-100000).</summary>
    public int VideoBudgetKbps { get; set; } = 6000;

    /// <summary>
    /// Devices that may connect without anyone at this machine approving. Everyone else is declined — an
    /// unattended agent has nobody to ask. Each entry lists exactly the rights it gets.
    /// </summary>
    public List<TrustedPeer> TrustedPeers { get; set; } = new();

    /// <summary>Returns the problems with these settings (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            problems.Add("Agent:ServerUrl must be an absolute http(s) URL.");
        else if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            problems.Add("Agent:ServerUrl must use https (plain http is only allowed for localhost).");

        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > 64) problems.Add("Agent:DeviceName must be 1-64 characters.");
        if (FileRoot is { Length: > 0 } && !Path.IsPathRooted(FileRoot)) problems.Add("Agent:FileRoot must be an absolute path.");
        if (VideoBudgetKbps is < 500 or > 100_000) problems.Add("Agent:VideoBudgetKbps must be between 500 and 100000.");

        foreach (var peer in TrustedPeers)
        {
            if (!DeviceId.TryParse(peer.DeviceId ?? string.Empty, out _)) problems.Add($"Agent:TrustedPeers has an invalid device ID '{peer.DeviceId}'.");
            if (!Enum.TryParse<Permission>(peer.Permissions, ignoreCase: true, out var p) || p == Permission.None)
                problems.Add($"Agent:TrustedPeers entry '{peer.DeviceId}' needs Permissions such as \"ViewOnly\" or \"ViewScreen, ControlMouse\".");
            else if ((p & (Permission.RemoteRestart | Permission.RemoteShutdown)) != 0)
                problems.Add($"Agent:TrustedPeers entry '{peer.DeviceId}' may not include restart/shutdown.");
        }
        return problems;
    }
}

public sealed class TrustedPeer
{
    public string? DeviceId { get; set; }

    /// <summary>A <see cref="Permission"/> list such as <c>ViewOnly</c> or <c>ViewScreen, ControlMouse</c>.</summary>
    public string? Permissions { get; set; }
}
