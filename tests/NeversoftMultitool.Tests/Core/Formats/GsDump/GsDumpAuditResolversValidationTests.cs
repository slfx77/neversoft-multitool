using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.GsDump;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsDumpAuditResolversValidationTests
{
    [Fact]
    public void EmptyScreenshotWhoseGeometryWrapsToZero_IsNotEmbedded()
    {
        var dump = GsDumpFile.Parse(BuildDump(65_536, 65_536, []));

        Assert.Equal(65_536, dump.ScreenshotWidth);
        Assert.Equal(65_536, dump.ScreenshotHeight);
        Assert.Empty(dump.ScreenshotPixels);
        Assert.False(GsDumpAuditResolvers.HasEmbeddedScreenshot(dump));
        Assert.Null(GsDumpAuditResolvers.LoadReferencePixels(dump, null));
    }

    [Fact]
    public void OnePixelScreenshot_IsEmbedded()
    {
        var dump = GsDumpFile.Parse(BuildDump(1, 1, [1, 2, 3, 4]));

        Assert.True(GsDumpAuditResolvers.HasEmbeddedScreenshot(dump));
        var reference = GsDumpAuditResolvers.LoadReferencePixels(dump, null);
        Assert.NotNull(reference);
        Assert.Equal(1, reference!.Width);
        Assert.Equal(1, reference.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, reference.Pixels);
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
