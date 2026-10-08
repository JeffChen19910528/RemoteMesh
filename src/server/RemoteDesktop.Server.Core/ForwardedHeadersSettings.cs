using System.Net;

namespace RemoteDesktop.Server.Core;

/// <summary>
/// Trust in a reverse proxy's X-Forwarded-* headers. Off by default: honouring them from anyone would let a client
/// forge its address and walk around every per-address rate limit and lockout. When on, only the listed
/// proxies are believed.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; init; }

    /// <summary>Exact proxy addresses, e.g. "10.0.0.5".</summary>
    public string[] KnownProxies { get; init; } = [];

    /// <summary>Proxy networks in CIDR form, e.g. "10.0.0.0/24".</summary>
    public string[] KnownNetworks { get; init; } = [];

    public void Validate()
    {
        if (!Enabled) return;
        var errors = new List<string>();
        if (KnownProxies.Length == 0 && KnownNetworks.Length == 0)
            errors.Add("ForwardedHeaders:Enabled requires at least one entry in KnownProxies or KnownNetworks (trusting every sender would let clients forge their address).");
        foreach (var p in KnownProxies)
            if (!IPAddress.TryParse(p, out _)) errors.Add($"ForwardedHeaders:KnownProxies entry '{p}' is not an IP address.");
        foreach (var n in KnownNetworks)
            if (!TryParseNetwork(n, out _, out _)) errors.Add($"ForwardedHeaders:KnownNetworks entry '{n}' is not CIDR (address/prefix).");
        OptionsValidation.ThrowIfAny(errors);
    }

    public static bool TryParseNetwork(string text, out IPAddress address, out int prefix)
    {
        address = IPAddress.None;
        prefix = 0;
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var p)) return false;
        var max = a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (p < 0 || p > max) return false;
        address = a;
        prefix = p;
        return true;
    }
}
