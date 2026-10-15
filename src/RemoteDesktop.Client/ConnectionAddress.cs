using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>What a person types to reach another device: a short ID (nine digits) or the full device ID.</summary>
public static class ConnectionAddress
{
    /// <summary>
    /// True when <paramref name="text"/> is either; <paramref name="normalized"/> is then the plain nine digits or the
    /// canonical full ID, with any spaces and dashes a short ID was typed with removed.
    /// </summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (ShortId.TryNormalize(text, out var digits)) { normalized = digits; return true; }
        if (DeviceId.TryParse(text?.Trim() ?? string.Empty, out var id)) { normalized = id.ToString(); return true; }
        return false;
    }
}
