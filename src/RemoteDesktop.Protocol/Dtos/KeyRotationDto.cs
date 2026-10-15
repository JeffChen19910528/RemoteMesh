using RemoteDesktop.Core;

namespace RemoteDesktop.Protocol.Dtos;

public sealed class KeyRotationDto
{
    public int Sequence { get; set; }
    public string OldKeyBase64 { get; set; } = "";
    public string NewKeyBase64 { get; set; } = "";
    public string OldKeySignatureBase64 { get; set; } = "";
    public string NewKeySignatureBase64 { get; set; } = "";

    public static KeyRotationDto From(KeyRotation r) => new()
    {
        Sequence = r.Sequence,
        OldKeyBase64 = Convert.ToBase64String(r.OldKey),
        NewKeyBase64 = Convert.ToBase64String(r.NewKey),
        OldKeySignatureBase64 = Convert.ToBase64String(r.OldKeySignature),
        NewKeySignatureBase64 = Convert.ToBase64String(r.NewKeySignature)
    };
}
