using Argonaut.Features.Hex;

namespace Argonaut.Tests.Features.Hex;

public sealed class HexCopyTests
{
    [Fact]
    public void AsHex_IsTwoDigitsAByte_SpaceSeparated()
    {
        Assert.Equal("89 50 4E 47 00 0A", HexCopy.AsHex([0x89, 0x50, 0x4E, 0x47, 0x00, 0x0A]));
        Assert.Equal("FF", HexCopy.AsHex([0xFF]));
        Assert.Equal(string.Empty, HexCopy.AsHex([]));
    }

    [Fact]
    public void AsText_IsTheCharacterColumn()
    {
        Assert.Equal(".PNG..", HexCopy.AsText([0x89, 0x50, 0x4E, 0x47, 0x00, 0x0A]));
    }
}
