using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.GsDump;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.GsDump;

public sealed class GsGifInterpreterAlphaFailTests
{
    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 255, 255)]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 248, 0)]
    public void Interpret_RgbOnlyAlphaFailure_UsesFramebufferFormatSemantics(
        uint psm,
        byte expectedRed,
        byte expectedAlpha)
    {
        var gif = Concat(
            AdTag(
                (0x4C, MakeFrame(psm)),
                (0x47, 1UL | (3UL << 12))),
            PackedTag(
                1,
                2,
                MakeRegs(0x01, 0x04),
                0,
                Rgbaq(248, 0, 0, 128),
                PackedXyz(1, 1)));
        var dump = GsDumpFile.Parse(BuildRawDump(gif));

        var result = GsGifInterpreter.Interpret(
            dump,
            new GsGifInterpretOptions { Width = 4, Height = 4 });

        var offset = (1 * 4 + 1) * 4;
        Assert.Equal(expectedRed, result.DirectPixels[offset]);
        Assert.Equal((byte)0, result.DirectPixels[offset + 1]);
        Assert.Equal((byte)0, result.DirectPixels[offset + 2]);
        Assert.Equal(expectedAlpha, result.DirectPixels[offset + 3]);
    }

    private static byte[] BuildRawDump(byte[] gif)
    {
        using var stream = new MemoryStream();
        WriteU32(stream, 0);
        WriteU32(stream, 0);
        stream.Write(new byte[8192]);
        stream.WriteByte(0);
        stream.WriteByte(3);
        WriteU32(stream, (uint)gif.Length);
        stream.Write(gif);
        return stream.ToArray();
    }

    private static byte[] AdTag(params (int Address, ulong Value)[] writes)
    {
        return PackedTag(
            writes.Length,
            1,
            0x0E,
            0,
            writes.Select(static write => Qword(write.Value, (ulong)write.Address)).ToArray());
    }

    private static byte[] PackedTag(
        int nloop,
        int nreg,
        ulong regs,
        ulong primitive,
        params byte[][] qwords)
    {
        using var stream = new MemoryStream();
        stream.Write(GifTag(nloop, nreg, regs, primitive));
        foreach (var qword in qwords)
            stream.Write(qword);
        return stream.ToArray();
    }

    private static byte[] GifTag(int nloop, int nreg, ulong regs, ulong primitive)
    {
        var lo = (uint)nloop |
                 (1UL << 46) |
                 ((primitive & 0x7FF) << 47) |
                 ((ulong)(nreg & 0xF) << 60);
        return Qword(lo, regs);
    }

    private static ulong MakeRegs(params int[] registers)
    {
        ulong value = 0;
        for (var index = 0; index < registers.Length; index++)
            value |= ((ulong)registers[index] & 0xF) << (index * 4);
        return value;
    }

    private static byte[] Rgbaq(byte r, byte g, byte b, byte a)
    {
        var qword = new byte[16];
        qword[0] = r;
        qword[4] = g;
        qword[8] = b;
        qword[12] = a;
        return qword;
    }

    private static byte[] PackedXyz(int x, int y)
    {
        var qword = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(qword, (ushort)(x * 16));
        BinaryPrimitives.WriteUInt16LittleEndian(qword.AsSpan(4), (ushort)(y * 16));
        BinaryPrimitives.WriteUInt32LittleEndian(qword.AsSpan(8), 10);
        return qword;
    }

    private static ulong MakeFrame(uint psm)
    {
        return 1UL << 16 | (ulong)psm << 24;
    }

    private static byte[] Qword(ulong low, ulong high = 0)
    {
        var qword = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(qword, low);
        BinaryPrimitives.WriteUInt64LittleEndian(qword.AsSpan(8), high);
        return qword;
    }

    private static byte[] Concat(params byte[][] chunks)
    {
        var result = new byte[chunks.Sum(static chunk => chunk.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
