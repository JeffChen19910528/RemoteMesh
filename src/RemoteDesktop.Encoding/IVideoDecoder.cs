using RemoteDesktop.Capture;

namespace RemoteDesktop.Encoding;

public interface IVideoDecoder : IAsyncDisposable
{
    CodecType Codec { get; }
    Task<CaptureFrame> DecodeAsync(ReadOnlyMemory<byte> encodedData, CancellationToken ct = default);
}
