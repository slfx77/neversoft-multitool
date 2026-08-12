using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class IsoSectorSourceTests
{
    [Fact]
    public void ReadSector_HugeLbaThatWouldWrapToZero_Throws()
    {
        var firstSector = new byte[IsoSectorSource.UserDataSize];
        firstSector[0] = 0xA5;
        using var source = new IsoSectorSource(new MemoryStream(firstSector, false));
        var output = new byte[IsoSectorSource.UserDataSize];

        var exception = Assert.Throws<InvalidDataException>(
            () => source.ReadSector(1L << 53, output));

        Assert.Contains("outside the ISO sector range", exception.Message);
        Assert.Equal((byte)0, output[0]);
    }

    [Fact]
    public void ReadSector_LbaZero_ReturnsExactSector()
    {
        var expected = Enumerable.Range(0, IsoSectorSource.UserDataSize)
            .Select(index => (byte)(index % 251))
            .ToArray();
        using var source = new IsoSectorSource(new MemoryStream(expected, false));
        var output = new byte[IsoSectorSource.UserDataSize];

        var submode = source.ReadSector(0, output);

        Assert.Equal((byte)0, submode);
        Assert.Equal(expected, output);
    }
}
