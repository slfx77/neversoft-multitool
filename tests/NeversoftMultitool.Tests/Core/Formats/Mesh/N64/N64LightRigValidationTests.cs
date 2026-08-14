using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public sealed class N64LightRigValidationTests
{
    [Fact]
    public void TryParse_ValidRigBody_ReturnsRig()
    {
        var rig = N64LightRig.TryParse(BuildBoot(secondaryLightColourPad: 0));

        Assert.NotNull(rig);
        Assert.Equal(-Vector3.UnitY, rig!.Direction);
    }

    [Fact]
    public void TryParse_NonzeroSecondaryLightColourPad_ReturnsNull()
    {
        var rig = N64LightRig.TryParse(BuildBoot(secondaryLightColourPad: 0x5A));

        Assert.Null(rig);
    }

    [Fact]
    public void TryParse_WrappingAmbientPointer_ReturnsNull()
    {
        const int displayListOffset = 24;
        var boot = BuildBoot(secondaryLightColourPad: 0);
        WriteWord(boot, displayListOffset + 12, 0);
        WriteWord(boot, displayListOffset + 20, 0xFFFF_FFF8);

        var rig = N64LightRig.TryParse(boot);

        Assert.Null(rig);
    }

    private static byte[] BuildBoot(byte secondaryLightColourPad)
    {
        const int displayListOffset = 24;
        var boot = new byte[displayListOffset + 24];

        byte[] rigBody =
        [
            70, 70, 70, 0,
            70, 70, 70, 0,
            105, 105, 105, 0,
            105, 105, 105, secondaryLightColourPad,
            0, 0x81, 0, 0,
            0, 0, 0, 0
        ];
        rigBody.CopyTo(boot, 0);

        WriteWord(boot, displayListOffset, 0xDB02_0000);
        WriteWord(boot, displayListOffset + 4, 0x0000_0018);
        WriteWord(boot, displayListOffset + 8, 0xDC08_060A);
        WriteWord(boot, displayListOffset + 12, 0x8000_1008);
        WriteWord(boot, displayListOffset + 16, 0xDC08_090A);
        WriteWord(boot, displayListOffset + 20, 0x8000_1000);
        return boot;
    }

    private static void WriteWord(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    }
}
