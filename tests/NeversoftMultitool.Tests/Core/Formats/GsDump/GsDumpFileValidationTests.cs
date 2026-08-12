using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.GsDump;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsDumpFileValidationTests
{
    [Fact]
    public void Parse_OverflowingScreenshotGeometry_DoesNotExposePixels()
    {
        var dump = GsDumpFile.Parse(BuildDump(0x40000001, 1, [1, 2, 3, 4]));

        Assert.Equal(0x40000001, dump.ScreenshotWidth);
        Assert.Equal(1, dump.ScreenshotHeight);
        Assert.Equal(4, dump.ScreenshotSize);
        Assert.Empty(dump.ScreenshotPixels);
    }

    [Fact]
    public void Parse_OnePixelScreenshot_ExposesPixels()
    {
        byte[] expected = [1, 2, 3, 4];

        var dump = GsDumpFile.Parse(BuildDump(1, 1, expected));

        Assert.Equal(expected, dump.ScreenshotPixels);
    }

    private static byte[] BuildDump(uint width, uint height, byte[] screenshotPixels)
    {
        const int headerSize = 36;
        var headerBlockSize = headerSize + screenshotPixels.Length;
        var raw = new byte[8 + headerBlockSize + 8192];

        BinaryPrimitives.WriteUInt32LittleEndian(raw, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)headerBlockSize);

        var header = raw.AsSpan(8, headerBlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint)screenshotPixels.Length);
        screenshotPixels.CopyTo(header[headerSize..]);

        return raw;
    }
}
