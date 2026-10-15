using System.Security.Cryptography;

namespace RemoteDesktop.Security;

/// <summary>Checks on device keys and signatures that never throw on hostile input.</summary>
public static class KeyChecks
{
    /// <summary>A well-formed ECDSA/ECDH P-256 public key (SubjectPublicKeyInfo) with nothing trailing.</summary>
    public static bool IsValidP256PublicKey(byte[] spki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length) return false; // trailing garbage
            var curve = key.ExportParameters(false).Curve;
            return curve.Oid?.Value == ECCurve.NamedCurves.nistP256.Oid.Value
                || curve.Oid?.FriendlyName is "nistP256" or "ECDSA_P256";
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>True only for a correct signature by that key; garbage keys or signatures are simply false.</summary>
    public static bool Verifies(byte[] publicKey, byte[] data, byte[] signature)
    {
        try { return DeviceKeyGenerator.Verify(publicKey, data, signature); }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
