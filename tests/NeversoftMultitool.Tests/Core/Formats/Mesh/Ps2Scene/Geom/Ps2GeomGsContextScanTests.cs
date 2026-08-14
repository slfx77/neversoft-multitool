using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomGsContextScanTests
{
    [Fact]
    public void MergeWith_NoPresentRegistersLeavesInheritedStateUnchanged()
    {
        var inherited = MakeInheritedContext();
        var scan = new Ps2GeomGsContextScan(new Ps2GeomGsContext(), GsRegisterMask.None);

        var merged = scan.MergeWith(inherited);

        Assert.False(scan.HasRegisters);
        Assert.Equal(inherited, merged);
    }

    [Fact]
    public void MergeWith_AlphaAndTestOnlyUpdatesThoseRegisters()
    {
        var inherited = MakeInheritedContext();
        var scan = new Ps2GeomGsContextScan(
            new Ps2GeomGsContext
            {
                Alpha1 = 0xAAAA,
                Test1 = 0xBBBB,
                Frame1 = 0xCCCC
            },
            GsRegisterMask.Alpha1 | GsRegisterMask.Test1 | GsRegisterMask.Frame1);

        var merged = scan.MergeWith(inherited);

        Assert.Equal(inherited.Tex0, merged.Tex0);
        Assert.Equal(inherited.Tex1, merged.Tex1);
        Assert.Equal(inherited.MipTbp1, merged.MipTbp1);
        Assert.Equal(inherited.MipTbp2, merged.MipTbp2);
        Assert.Equal(inherited.Clamp1, merged.Clamp1);
        Assert.Equal(0xAAAAUL, merged.Alpha1);
        Assert.Equal(0xBBBBUL, merged.Test1);
        Assert.Equal(0xCCCCUL, merged.Frame1);
    }

    [Fact]
    public void MergeWith_Tex0OnlyKeepsPriorClampAlphaAndTest()
    {
        var inherited = MakeInheritedContext();
        var scan = new Ps2GeomGsContextScan(
            new Ps2GeomGsContext { Tex0 = 0x9999 },
            GsRegisterMask.Tex0);

        var merged = scan.MergeWith(inherited);

        Assert.Equal(0x9999UL, merged.Tex0);
        Assert.Equal(inherited.Clamp1, merged.Clamp1);
        Assert.Equal(inherited.Alpha1, merged.Alpha1);
        Assert.Equal(inherited.Test1, merged.Test1);
        Assert.Equal(inherited.Frame1, merged.Frame1);
    }

    [Fact]
    public void ScanBatchForGsContext_CapturesFrame1Register()
    {
        const ulong frame = 0xFF000000000A0000;
        var data = MakeGifRegisterBlock(0x4C, frame);

        var scan = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, 0, data.Length);

        Assert.True(scan.HasValue);
        Assert.True((scan.Value.Present & GsRegisterMask.Frame1) != 0);
        Assert.Equal(frame, scan.Value.Context.Frame1);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    public void ScanBatchForGsContext_PrimaryPatternDoesNotBorrowNextBatchRegisterBlock(int batchEnd)
    {
        var data = MakeGifRegisterBlock(0x4C, 0x123456789ABCDEF0);

        var scan = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, 0, batchEnd);

        Assert.Null(scan);
    }

    [Fact]
    public void ScanBatchForGsContext_StandalonePatternDoesNotBorrowPayloadPastBatchEnd()
    {
        var data = MakeStandaloneRegisterBlock(0x4C, 0x123456789ABCDEF0);

        var scan = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, 0, 4);

        Assert.Null(scan);
    }

    [Fact]
    public void ScanBatchForGsContext_StandalonePatternAcceptsPayloadEndingAtBatchEnd()
    {
        const ulong frame = 0x123456789ABCDEF0;
        var data = MakeStandaloneRegisterBlock(0x4C, frame);

        var scan = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, 0, data.Length);

        Assert.True(scan.HasValue);
        Assert.Equal(GsRegisterMask.Frame1, scan.Value.Present);
        Assert.Equal(frame, scan.Value.Context.Frame1);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(24)]
    public void ScanBatchForCenter_DoesNotBorrowNextBatchPayload(int batchEnd)
    {
        var data = MakeCenterBlock(1.25f, -2.5f, 3.75f);

        var center = Ps2GeomMdlBatchScanner.ScanBatchForCenter(data, 0, batchEnd);

        Assert.Null(center);
    }

    [Fact]
    public void ScanBatchForCenter_AcceptsPayloadEndingAtBatchEnd()
    {
        var data = MakeCenterBlock(1.25f, -2.5f, 3.75f);

        var center = Ps2GeomMdlBatchScanner.ScanBatchForCenter(data, 0, data.Length);

        Assert.True(center.HasValue);
        Assert.Equal(1.25f, center.Value.X);
        Assert.Equal(-2.5f, center.Value.Y);
        Assert.Equal(3.75f, center.Value.Z);
    }

    private static Ps2GeomGsContext MakeInheritedContext()
    {
        return new Ps2GeomGsContext
        {
            Tex0 = 0x10,
            Tex1 = 0x20,
            MipTbp1 = 0x30,
            MipTbp2 = 0x40,
            Clamp1 = 0x50,
            Alpha1 = 0x60,
            Test1 = 0x70,
            Frame1 = 0x80
        };
    }

    private static byte[] MakeGifRegisterBlock(uint reg, ulong value)
    {
        var data = new byte[36];
        data[2] = 1;
        data[3] = 0x6C;
        data[22] = 1;
        data[23] = 0x68;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), (uint)value);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)(value >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), reg);
        return data;
    }

    private static byte[] MakeStandaloneRegisterBlock(uint reg, ulong value)
    {
        var data = new byte[16];
        data[2] = 1;
        data[3] = 0x68;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)value);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)(value >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), reg);
        return data;
    }

    private static byte[] MakeCenterBlock(float x, float y, float z)
    {
        var data = new byte[36];
        data[2] = 1;
        data[3] = 0x6C;
        data[22] = 1;
        data[23] = 0x68;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(24), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(28), y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(32), z);
        return data;
    }
}
