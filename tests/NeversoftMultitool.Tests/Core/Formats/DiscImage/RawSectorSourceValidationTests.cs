using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class RawSectorSourceValidationTests
{
    [Fact]
    public void ReadSector_SixteenBytePhysicalSector_IsRejected()
    {
        var path = WriteTempFile(Enumerable.Repeat((byte)0xA5, 16).ToArray());
        try
        {
            var region = new DiscTrackRegion(0, 1, path, 0, 16, false);

            var exception = Assert.Throws<InvalidDataException>(() =>
            {
                using var source = new RawSectorSource([region]);
                source.ReadSector(0, new byte[IsoSectorSource.UserDataSize]);
            });

            Assert.Contains("Unsupported physical sector size 16", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSector_2048BytePhysicalSector_ReturnsExactData()
    {
        var expected = Enumerable.Range(0, IsoSectorSource.UserDataSize)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var path = WriteTempFile(expected);

        try
        {
            var region = new DiscTrackRegion(
                0,
                1,
                path,
                0,
                IsoSectorSource.UserDataSize,
                false);
            using var source = new RawSectorSource([region]);
            var output = new byte[IsoSectorSource.UserDataSize];

            var submode = source.ReadSector(0, output);

            Assert.Equal((byte)0, submode);
            Assert.Equal(expected, output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt_raw_sector_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, data);
        return path;
    }
}
