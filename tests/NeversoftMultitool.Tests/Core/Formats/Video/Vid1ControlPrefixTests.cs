using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1ControlPrefixTests
{
    [Fact]
    public void Probe99A38_GateBitSet_ReturnsSpecialStage()
    {
        // First bit = 1 → special stage, macroblock_type = 0x10 (callerCr4=0)
        var reader = new Vid1BitReader([0b10000000, 0, 0, 0]);
        var probe = Vid1ControlPrefix.Probe99A38(reader, reader, currentQuantizer: 5, callerCr4: 0, gmcEnabled: false);

        Assert.Equal(Vid1ControlStage.Special, probe.Stage);
        Assert.Equal(0x10, probe.MacroblockType);
        Assert.Equal(1, probe.GateBit);
        Assert.Equal(5, probe.Quantizer);
    }

    [Fact]
    public void Probe99A38_GateBitSet_Cr4_Returns0x11()
    {
        var reader = new Vid1BitReader([0b10000000, 0, 0, 0]);
        var probe = Vid1ControlPrefix.Probe99A38(reader, reader, currentQuantizer: 5, callerCr4: 1, gmcEnabled: false);
        Assert.Equal(Vid1ControlStage.SpriteWarp, probe.Stage);
        Assert.Equal(0x11, probe.MacroblockType);
    }

    [Fact]
    public void Probe99A38_GateBitSet_ConsumesOneBit()
    {
        var reader = new Vid1BitReader([0b10000000, 0, 0, 0]);
        Vid1ControlPrefix.Probe99A38(reader, reader, 5, 0, gmcEnabled: false);
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void Probe99A38_GateSet_DoesNotModifyQuantizer()
    {
        var reader = new Vid1BitReader([0b10000000, 0, 0, 0]);
        var probe = Vid1ControlPrefix.Probe99A38(reader, reader, currentQuantizer: 17, callerCr4: 0, gmcEnabled: false);
        Assert.Equal(17, probe.Quantizer);
    }

    [Fact]
    public void Probe99A38_GateSet_NoVlcFieldsSet()
    {
        var reader = new Vid1BitReader([0b10000000, 0, 0, 0]);
        var probe = Vid1ControlPrefix.Probe99A38(reader, reader, 5, 0, gmcEnabled: false);
        Assert.Equal(-1, probe.RawCode);
        Assert.Equal(-1, probe.Selector);
        Assert.Equal(-1, probe.QdeltaIndex);
    }

    // NOTE: Integration-level tests over hand-constructed bitstreams are
    // impractical for the non-gate-bit branches — multi-step VLC decode
    // makes bit layout fragile. Those paths are covered end-to-end by the
    // decoder integration tests in Stage 9 against real VID1 frames.
}
