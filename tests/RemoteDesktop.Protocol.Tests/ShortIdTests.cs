using RemoteDesktop.Protocol;

namespace RemoteDesktop.Protocol.Tests;

public sealed class ShortIdTests
{
    [Theory]
    [InlineData("482913057", "482913057")]
    [InlineData("482 913 057", "482913057")]
    [InlineData("482-913-057", "482913057")]
    [InlineData("  482 913 057  ", "482913057")]
    [InlineData("4 8 2 9 1 3 0 5 7", "482913057")]
    [InlineData("100000000", "100000000")]
    [InlineData("999999999", "999999999")]
    public void ValidShortIds_AreNormalizedToNineDigits(string typed, string expected)
    {
        Assert.True(ShortId.TryNormalize(typed, out var digits));
        Assert.Equal(expected, digits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345678")]        // too short
    [InlineData("1234567890")]      // too long
    [InlineData("012345678")]       // leading zero
    [InlineData("12345678a")]
    [InlineData("12.345.678")]
    [InlineData("１２３４５６７８９")] // full-width digits are not ASCII digits
    [InlineData("b80e3fbd-e8a8-4bac-8ef6-8f9902627b64")] // a full ID is not a short ID
    [InlineData("123456789123456789123456789")]          // absurdly long input is refused outright
    public void Anything_Else_IsNotAShortId(string? typed) => Assert.False(ShortId.TryNormalize(typed, out _));

    [Fact]
    public void Format_GroupsInThrees() => Assert.Equal("482 913 057", ShortId.Format("482913057"));

    [Fact]
    public void Format_LeavesOtherLengthsAlone() => Assert.Equal("123", ShortId.Format("123"));

    [Fact]
    public void FormattedIds_ParseBackToTheSameDigits()
    {
        Assert.True(ShortId.TryNormalize(ShortId.Format("482913057"), out var back));
        Assert.Equal("482913057", back);
    }

    [Fact]
    public void ARandomFullId_IsNeverMistakenForAShortId()
    {
        for (var i = 0; i < 2000; i++)
            Assert.False(ShortId.TryNormalize(Guid.NewGuid().ToString(), out _));
    }
}
