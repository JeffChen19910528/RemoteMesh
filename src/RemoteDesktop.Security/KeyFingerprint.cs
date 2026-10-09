using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Security;

/// <summary>
/// What two people read to each other to be sure no one sits between their computers. Plain SHA-256 over the public
/// keys (an established hash, nothing invented): the only design decision is how to show it.
/// </summary>
public static class KeyFingerprint
{
    private const string PairContext = "RemoteMesh pair code v1";

    /// <summary>The key's SHA-256, for storing next to a device ID and spotting a changed key later.</summary>
    public static byte[] Of(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return SHA256.HashData(publicKey);
    }

    /// <summary>
    /// The security code of a connection: six groups of five digits that depend on both keys and not on who is
    /// asking, so both screens show the same number. If it differs, someone is in the middle.
    /// </summary>
    public static string SecurityCode(byte[] keyA, byte[] keyB)
    {
        ArgumentNullException.ThrowIfNull(keyA);
        ArgumentNullException.ThrowIfNull(keyB);
        var (first, second) = keyA.AsSpan().SequenceCompareTo(keyB) <= 0 ? (keyA, keyB) : (keyB, keyA);

        var context = Encoding.ASCII.GetBytes(PairContext);
        // Length-prefixed, so ("ab","c") and ("a","bc") cannot give the same input.
        var input = new byte[context.Length + 8 + first.Length + second.Length];
        var o = 0;
        context.CopyTo(input, o); o += context.Length;
        BitConverter.TryWriteBytes(input.AsSpan(o), first.Length); o += 4;
        first.CopyTo(input, o); o += first.Length;
        BitConverter.TryWriteBytes(input.AsSpan(o), second.Length); o += 4;
        second.CopyTo(input, o);

        var hash = SHA256.HashData(input);
        var groups = new string[6];
        for (var i = 0; i < groups.Length; i++)
        {
            // Five bytes (40 bits) per group, reduced to five digits; the modulo bias is far below one part in a million.
            var chunk = (ulong)hash[i * 5] << 32 | (ulong)hash[i * 5 + 1] << 24 | (ulong)hash[i * 5 + 2] << 16
                        | (ulong)hash[i * 5 + 3] << 8 | hash[i * 5 + 4];
            groups[i] = (chunk % 100_000).ToString("D5");
        }
        return string.Join(' ', groups);
    }
}
