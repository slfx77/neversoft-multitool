using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class MpegProgramStreamProbeTests
{
    public static TheoryData<string, byte[], bool> PackHeaderCases
    {
        get
        {
            byte[] mpeg1 = [
                0x00, 0x00, 0x01, 0xBA,
                0x21, 0x00, 0x01, 0x00, 0x01, 0x80, 0x00, 0x03
            ];
            byte[] mpeg2 = [
                0x00, 0x00, 0x01, 0xBA,
                0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x00, 0x00, 0x07, 0xF8
            ];
            var invalidVersion = (byte[])mpeg2.Clone();
            invalidVersion[4] = 0x00;

            return new TheoryData<string, byte[], bool>
            {
                { "magic only", [0x00, 0x00, 0x01, 0xBA], false },
                { "truncated MPEG-1", mpeg1[..^1], false },
                { "complete MPEG-1", mpeg1, true },
                { "truncated MPEG-2", mpeg2[..^1], false },
                { "complete MPEG-2", mpeg2, true },
                { "invalid version discriminator", invalidVersion, false }
            };
        }
    }

    [Theory]
    [MemberData(nameof(PackHeaderCases))]
    public void HasPackHeader_ValidatesVersionAndPhysicalHeaderSize(
        string _, byte[] data, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-mpeg-ps-{Guid.NewGuid():N}.sfd");
        try
        {
            File.WriteAllBytes(path, data);

            Assert.Equal(expected, MpegProgramStreamProbe.HasPackHeader(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
