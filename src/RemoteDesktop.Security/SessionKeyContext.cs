using System.Buffers.Binary;

namespace RemoteDesktop.Security;

public static class SessionKeyContext
{
    /// <summary>Key-derivation context for a connection epoch (the server increments it on every session resume).</summary>
    public static byte[] ForEpoch(int epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        if (epoch == 0) return []; // epoch 0 keeps the original derivation, so existing sessions are unaffected
        var context = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(context, epoch);
        return context;
    }
}
