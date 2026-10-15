namespace RemoteDesktop.Protocol;

/// <summary>
/// The short, speakable address of a device: nine digits, never starting with 0 (for example <c>482 913 057</c>).
/// It is only a pointer to a device; the device's real identity is still its key-backed full ID.
/// </summary>
public static class ShortId
{
    public const int Length = 9;

    /// <summary>
    /// True when <paramref name="text"/> is a short ID, with or without the spaces or dashes people type around it.
    /// <paramref name="digits"/> is the plain nine digits.
    /// </summary>
    public static bool TryNormalize(string? text, out string digits)
    {
        digits = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24) return false;

        Span<char> buffer = stackalloc char[Length];
        var n = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or ' ') continue;      // separators are allowed anywhere
            if (!char.IsAsciiDigit(c) || n == Length) return false;
            buffer[n++] = c;
        }
        if (n != Length || buffer[0] == '0') return false;
        digits = new string(buffer);
        return true;
    }

    /// <summary>Groups the digits for display: <c>482 913 057</c>.</summary>
    public static string Format(string digits) =>
        digits.Length == Length ? $"{digits[..3]} {digits[3..6]} {digits[6..]}" : digits;
}
