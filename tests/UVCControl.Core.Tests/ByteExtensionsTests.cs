using UVCControl.Core.Controls;

namespace UVCControl.Core.Tests;

public class ByteExtensionsTests
{
    [Theory]
    [InlineData(new byte[] { 0xE2, 0xFF }, true, -30)]
    [InlineData(new byte[] { 0xE2, 0xFF }, false, 65506)]
    [InlineData(new byte[] { 0x70, 0xCF, 0x0B, 0x00 }, true, 774_000)]
    [InlineData(new byte[] { 0x60, 0x0E, 0xFB, 0xFF }, true, -324_000)]
    public void ReadsLittleEndian(byte[] bytes, bool signed, long expected) =>
        Assert.Equal(expected, bytes.ReadInt(0, bytes.Length, signed));

    [Fact]
    public void ReadOutOfRangeReturnsZero() => Assert.Equal(0, new byte[] { 1 }.ReadInt(0, 2, false));

    [Fact]
    public void WithIntWritesFieldAndGrows()
    {
        byte[] original = [0xAA, 0xBB];
        var result = original.WithInt(-2, 2, 2);
        Assert.Equal([0xAA, 0xBB, 0xFE, 0xFF], result);
        Assert.Equal([0xAA, 0xBB], original); // unchanged
    }

    [Theory]
    [InlineData("0a 1B:ff,00", new byte[] { 0x0A, 0x1B, 0xFF, 0x00 })]
    [InlineData("", new byte[0])]
    public void ParsesHex(string text, byte[] expected) => Assert.Equal(expected, ByteExtensions.ParseHex(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("zz")]
    public void RejectsInvalidHex(string text) => Assert.Null(ByteExtensions.ParseHex(text));

    [Fact]
    public void FormatsHex() => Assert.Equal("00 7F FF", new byte[] { 0, 0x7F, 0xFF }.ToHexString());

    [Fact]
    public void ShowsAsciiOnlyWithEnoughPrintableBytes()
    {
        Assert.Equal("... uvc", new byte[] { 0, 1, 2, 0x20, 0x75, 0x76, 0x63 }.PrintableAscii());
        Assert.Null(new byte[] { 0, 0x41, 0x42 }.PrintableAscii());
    }

    [Fact]
    public void AdvertisedBitmap()
    {
        byte[] bm = [0x0A, 0x08];
        Assert.True(UvcControlCatalog.IsAdvertised(bm, 1));
        Assert.True(UvcControlCatalog.IsAdvertised(bm, 11));
        Assert.False(UvcControlCatalog.IsAdvertised(bm, 0));
        Assert.False(UvcControlCatalog.IsAdvertised(bm, 16));
        Assert.False(UvcControlCatalog.IsAdvertised(bm, null));
    }
}
