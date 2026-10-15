using System.Text;

namespace RemoteDesktop.Clipboard;

public enum ClipboardMessageType : byte
{
    ReadRequest = 0,   // viewer asks for host clipboard
    Data = 1,          // host returns clipboard content
    WriteRequest = 2,  // viewer pushes content to host clipboard
    Ack = 3,           // write succeeded
    Denied = 4         // request refused; payload = 1 byte ClipboardResult
}

public enum ClipboardFormat : byte { Text = 0, Image = 1 }

public enum ClipboardResult : byte
{
    Ok = 0,
    PolicyDenied = 1,
    PermissionDenied = 2,
    SessionNotActive = 3,
    TooLarge = 4,
    Empty = 5,
    RateLimited = 6,
    Malformed = 7
}

public static class ClipboardLimits
{
    public const int MaxTextBytes = 1 * 1024 * 1024;
    public const int MaxImageBytes = 10 * 1024 * 1024;

    public static int MaxFor(ClipboardFormat f) => f == ClipboardFormat.Text ? MaxTextBytes : MaxImageBytes;
}

/// <summary>Wire message: [type:1][format:1][payload]. Parsed from untrusted input; never throws.</summary>
public sealed record ClipboardMessage(ClipboardMessageType Type, ClipboardFormat Format, byte[] Payload)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public byte[] ToBytes()
    {
        var b = new byte[2 + Payload.Length];
        b[0] = (byte)Type;
        b[1] = (byte)Format;
        Payload.CopyTo(b, 2);
        return b;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out ClipboardMessage? message)
    {
        message = null;
        if (data.Length < 2) return false;
        if (!Enum.IsDefined((ClipboardMessageType)data[0]) || !Enum.IsDefined((ClipboardFormat)data[1]))
            return false;

        var payload = data[2..];
        // Cap before copying so an oversized message cannot force a large allocation.
        if (payload.Length > ClipboardLimits.MaxFor((ClipboardFormat)data[1])) return false;

        message = new ClipboardMessage((ClipboardMessageType)data[0], (ClipboardFormat)data[1], payload.ToArray());
        return true;
    }

    public static ClipboardMessage Denied(ClipboardFormat f, ClipboardResult reason) =>
        new(ClipboardMessageType.Denied, f, [(byte)reason]);

    public static byte[] EncodeText(string text) => StrictUtf8.GetBytes(text);

    public static bool TryDecodeText(byte[] payload, out string text)
    {
        try { text = StrictUtf8.GetString(payload); return true; }
        catch (DecoderFallbackException) { text = string.Empty; return false; }
    }
}
