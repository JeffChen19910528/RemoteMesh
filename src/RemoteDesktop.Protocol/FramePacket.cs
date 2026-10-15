using System.Buffers.Binary;

namespace RemoteDesktop.Protocol;

/// <summary>Wire format for one chunk of an encoded video frame on the Desktop channel.</summary>
public sealed class FramePacket
{
    public const int HeaderSize = 8 + 1 + 1 + 2 + 2 + 2 + 2;
    public const int MaxChunksPerFrame = 4096;

    public required long FrameNumber { get; init; }
    public required byte Codec { get; init; }
    public required bool IsKeyframe { get; init; }
    public required ushort ChunkIndex { get; init; }
    public required ushort ChunkCount { get; init; }
    public required ushort Width { get; init; }
    public required ushort Height { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public byte[] ToBytes()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, FrameNumber);
        buf[8] = Codec;
        buf[9] = (byte)(IsKeyframe ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), ChunkIndex);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(12), ChunkCount);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(14), Width);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16), Height);
        Payload.Span.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>Parses untrusted bytes; returns false for any malformed packet.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> data, out FramePacket? packet)
    {
        packet = null;
        if (data.Length < HeaderSize) return false;
        var s = data.Span;
        var index = BinaryPrimitives.ReadUInt16BigEndian(s[10..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(s[12..]);
        if (count == 0 || count > MaxChunksPerFrame || index >= count) return false;

        packet = new FramePacket
        {
            FrameNumber = BinaryPrimitives.ReadInt64BigEndian(s),
            Codec = s[8],
            IsKeyframe = s[9] == 1,
            ChunkIndex = index,
            ChunkCount = count,
            Width = BinaryPrimitives.ReadUInt16BigEndian(s[14..]),
            Height = BinaryPrimitives.ReadUInt16BigEndian(s[16..]),
            Payload = data[HeaderSize..]
        };
        return true;
    }
}
