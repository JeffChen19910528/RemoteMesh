using System.Security.Cryptography;

namespace RemoteDesktop.Security;

public static class DeviceKeyGenerator
{
    public static DeviceKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceKeyPair
        {
            PublicKey = ecdsa.ExportSubjectPublicKeyInfo(),
            PrivateKey = ecdsa.ExportECPrivateKey(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static ECDsa LoadSigningKey(DeviceKeyPair keyPair)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(keyPair.PrivateKey, out _);
        return ecdsa;
    }

    public static ECDsa LoadVerifyingKey(byte[] subjectPublicKeyInfoDer)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfoDer, out _);
        return ecdsa;
    }

    public static byte[] Sign(DeviceKeyPair keyPair, byte[] data)
    {
        using var ecdsa = LoadSigningKey(keyPair);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    public static bool Verify(byte[] subjectPublicKeyInfoDer, byte[] data, byte[] signature)
    {
        using var ecdsa = LoadVerifyingKey(subjectPublicKeyInfoDer);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}
