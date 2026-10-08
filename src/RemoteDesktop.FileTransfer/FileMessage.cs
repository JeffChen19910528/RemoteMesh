using System.Buffers.Binary;
using System.Text;

namespace RemoteDesktop.FileTransfer;

public enum FileMessageType : byte
{
    UploadStart = 1,      // viewer → host: total(8) sha256(32) name(utf8)
    UploadAccept = 2,     // host → viewer: resumeOffset(8)
    Chunk = 3,            // either way:    offset(8) data
    ChunkAck = 4,         // host → viewer: bytesWritten(8)
    End = 5,              // either way:    (empty)
    Complete = 6,         // host → viewer: (empty)
    Reject = 7,           // host → viewer: result(1)
    Cancel = 8,           // viewer → host: (empty)
    DownloadRequest = 9,  // viewer → host: offset(8) name(utf8)
    DownloadInfo = 10     // host → viewer: total(8) sha256(32)
}

public enum FileTransferResult : byte
{
    Ok = 0,
    SessionNotActive = 1,
    PermissionDenied = 2,
    InvalidName = 3,
    TooLarge = 4,
    Exists = 5,
    NotFound = 6,
    BadOffset = 7,
    ChecksumMismatch = 8,
    Busy = 9,
    Cancelled = 10,
    Malformed = 11,
    SizeMismatch = 12,
    OutOfOrder = 13,
    Timeout = 14,
    ConnectionClosed = 15,
    IoError = 16
}

/// <summary>Wire message on the File channel: [type:1][transferId:16][body]. Parsed from untrusted input.</summary>
public sealed record FileMessage(FileMessageType Type, Guid TransferId, byte[] Body)
{
    public const int HeaderSize = 17;
    public const int MaxChunkSize = 64 * 1024;
    public const int MaxNameBytes = 1020;
    public const int Sha256Size = 32;
    public const int MaxMessageSize = HeaderSize + 8 + MaxChunkSize;

    public byte[] ToBytes()
    {
        var b = new byte[HeaderSize + Body.Length];
        b[0] = (byte)Type;
        TransferId.TryWriteBytes(b.AsSpan(1));
        Body.CopyTo(b, HeaderSize);
        return b;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out FileMessage? message)
    {
        message = null;
        if (data.Length < HeaderSize || data.Length > MaxMessageSize) return false;
        if (!Enum.IsDefined((FileMessageType)data[0])) return false;
        message = new FileMessage((FileMessageType)data[0], new Guid(data.Slice(1, 16)), data[HeaderSize..].ToArray());
        return true;
    }

    // ── Builders ─────────────────────────────────────────────────────────

    public static FileMessage Empty(FileMessageType type, Guid id) => new(type, id, []);

    public static FileMessage WithOffset(FileMessageType type, Guid id, long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return new(type, id, b);
    }

    public static FileMessage Chunk(Guid id, long offset, ReadOnlySpan<byte> data)
    {
        var b = new byte[8 + data.Length];
        BinaryPrimitives.WriteInt64BigEndian(b, offset);
        data.CopyTo(b.AsSpan(8));
        return new(FileMessageType.Chunk, id, b);
    }

    public static FileMessage Reject(Guid id, FileTransferResult reason) =>
        new(FileMessageType.Reject, id, [(byte)reason]);

    public static FileMessage UploadStart(Guid id, long total, byte[] sha256, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var b = new byte[8 + Sha256Size + nameBytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(b, total);
        sha256.CopyTo(b, 8);
        nameBytes.CopyTo(b, 8 + Sha256Size);
        return new(FileMessageType.UploadStart, id, b);
    }

    public static FileMessage DownloadRequest(Guid id, long offset, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var b = new byte[8 + nameBytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(b, offset);
        nameBytes.CopyTo(b, 8);
        return new(FileMessageType.DownloadRequest, id, b);
    }

    public static FileMessage DownloadInfo(Guid id, long total, byte[] sha256)
    {
        var b = new byte[8 + Sha256Size];
        BinaryPrimitives.WriteInt64BigEndian(b, total);
        sha256.CopyTo(b, 8);
        return new(FileMessageType.DownloadInfo, id, b);
    }

    // ── Accessors (all return false on malformed bodies) ─────────────────

    public bool TryGetOffset(out long value)
    {
        value = 0;
        if (Body.Length < 8) return false;
        value = BinaryPrimitives.ReadInt64BigEndian(Body);
        return true;
    }

    public bool TryGetChunk(out long offset, out ReadOnlyMemory<byte> data)
    {
        data = default;
        if (!TryGetOffset(out offset)) return false;
        data = Body.AsMemory(8);
        return data.Length <= MaxChunkSize;
    }

    public bool TryGetNameAfterOffset(out long offset, out string name)
    {
        name = string.Empty;
        if (!TryGetOffset(out offset)) return false;
        return TryDecodeName(Body.AsSpan(8), out name);
    }

    public bool TryGetUploadStart(out long total, out byte[] sha256, out string name)
    {
        sha256 = [];
        name = string.Empty;
        if (!TryGetOffset(out total) || Body.Length < 8 + Sha256Size) return false;
        sha256 = Body.AsSpan(8, Sha256Size).ToArray();
        return TryDecodeName(Body.AsSpan(8 + Sha256Size), out name);
    }

    public bool TryGetInfo(out long total, out byte[] sha256)
    {
        sha256 = [];
        if (!TryGetOffset(out total) || Body.Length != 8 + Sha256Size) return false;
        sha256 = Body.AsSpan(8, Sha256Size).ToArray();
        return true;
    }

    public FileTransferResult RejectReason =>
        Body.Length == 1 && Enum.IsDefined((FileTransferResult)Body[0])
            ? (FileTransferResult)Body[0]
            : FileTransferResult.Malformed;

    private static bool TryDecodeName(ReadOnlySpan<byte> bytes, out string name)
    {
        name = string.Empty;
        if (bytes.Length == 0 || bytes.Length > MaxNameBytes) return false;
        try
        {
            name = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
