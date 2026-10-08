using System.Security.Cryptography;
using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class DeviceKeyGeneratorTests
{
    [Fact]
    public void Generate_ProducesNonEmptyKeys()
    {
        var kp = DeviceKeyGenerator.Generate();
        Assert.NotEmpty(kp.PublicKey);
        Assert.NotEmpty(kp.PrivateKey);
    }

    [Fact]
    public void Generate_ProducesUniqueKeyPairs()
    {
        var kp1 = DeviceKeyGenerator.Generate();
        var kp2 = DeviceKeyGenerator.Generate();
        Assert.False(kp1.PublicKey.SequenceEqual(kp2.PublicKey));
        Assert.False(kp1.PrivateKey.SequenceEqual(kp2.PrivateKey));
    }

    [Fact]
    public void Generate_PublicKeyIsValidSubjectPublicKeyInfo()
    {
        var kp = DeviceKeyGenerator.Generate();
        // Should be importable without throwing
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(kp.PublicKey, out var consumed);
        Assert.Equal(kp.PublicKey.Length, consumed);
    }

    [Fact]
    public void Generate_PrivateKeyIsValidECPrivateKey()
    {
        var kp = DeviceKeyGenerator.Generate();
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(kp.PrivateKey, out var consumed);
        Assert.Equal(kp.PrivateKey.Length, consumed);
    }

    [Fact]
    public void Sign_VerifiesWithMatchingPublicKey()
    {
        var kp = DeviceKeyGenerator.Generate();
        var data = "device-challenge-12345"u8.ToArray();

        var signature = DeviceKeyGenerator.Sign(kp, data);

        Assert.True(DeviceKeyGenerator.Verify(kp.PublicKey, data, signature));
    }

    [Fact]
    public void Sign_DoesNotVerifyWithDifferentKey()
    {
        var kp1 = DeviceKeyGenerator.Generate();
        var kp2 = DeviceKeyGenerator.Generate();
        var data = "hello"u8.ToArray();

        var signature = DeviceKeyGenerator.Sign(kp1, data);

        Assert.False(DeviceKeyGenerator.Verify(kp2.PublicKey, data, signature));
    }

    [Fact]
    public void Sign_DoesNotVerifyWithAlteredData()
    {
        var kp = DeviceKeyGenerator.Generate();
        var data = "original"u8.ToArray();
        var signature = DeviceKeyGenerator.Sign(kp, data);

        var tampered = "tampered"u8.ToArray();
        Assert.False(DeviceKeyGenerator.Verify(kp.PublicKey, tampered, signature));
    }
}
