using System.Text;
using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public class QbStructBufferTests
{
    // ESymbolType values (THUG Gel/Scripting/symboltype.h)
    private const byte TInt = 1, TFloat = 2, TString = 3, TStruct = 10, TArray = 12, TName = 13;
    private const byte TUInt8 = 16, TZeroInt = 18, TNone = 0;

    private static byte[] Buffer(params object[] parts)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var part in parts)
        {
            switch (part)
            {
                case byte b: w.Write(b); break;
                case uint u: w.Write(u); break;
                case int i: w.Write(i); break;
                case ushort s: w.Write(s); break;
                case float f: w.Write(f); break;
                case string str:
                    w.Write(Encoding.Latin1.GetBytes(str));
                    w.Write((byte)0);
                    break;
                default: throw new InvalidOperationException();
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void Parse_MixedComponents_RoundTrips()
    {
        // { version = 3 (u8), duration = 1.5f, label = "hi", model = NAME 0xCAFEBABE, zero = 0 }
        var data = Buffer(
            TUInt8, 0x11111111u, (byte)3,
            TFloat, 0x22222222u, 1.5f,
            TString, 0x33333333u, "hi",
            TName, 0x44444444u, 0xCAFEBABEu,
            TZeroInt, 0x55555555u,
            TNone);

        var comps = QbStructBuffer.Parse(data);

        Assert.Equal(5, comps.Count);
        Assert.Equal(3, comps[0].Value);
        Assert.Equal(1.5f, comps[1].Value);
        Assert.Equal("hi", comps[2].Value);
        Assert.Equal(0xCAFEBABEu, comps[3].Value);
        Assert.True(comps[3].IsNameValue);
        Assert.Equal(0, comps[4].Value);
    }

    [Fact]
    public void Parse_ArrayOfStructs_MatchesCifstructShape()
    {
        // Objects = array[2] of struct { ObjectName = NAME, count = int }
        var data = Buffer(
            TArray, 0xAAAAAAAAu, TStruct, (ushort)2,
            TName, 0x01010101u, 0xDEAD0001u, TInt, 0x02020202u, 7, TNone,
            TName, 0x01010101u, 0xDEAD0002u, TInt, 0x02020202u, 9, TNone,
            TNone);

        var comps = QbStructBuffer.Parse(data);

        var array = Assert.IsType<QbStructBuffer.Array>(comps[0].Value);
        Assert.Equal(2, array.Elements.Count);
        var first = Assert.IsType<List<QbStructBuffer.Component>>(array.Elements[0]);
        Assert.Equal(0xDEAD0001u, first[0].Value);
        Assert.Equal(7, first[1].Value);
    }

    [Fact]
    public void Parse_TrailingBytes_Throws()
    {
        var data = Buffer(TZeroInt, 0x11111111u, TNone, (byte)0xFF);
        Assert.Throws<InvalidDataException>(() => QbStructBuffer.Parse(data));
    }

    [Fact]
    public void Parse_CompressedName_ThrowsWithClearMessage()
    {
        // Bit 7 on the type byte = 8-bit compression-table name (tables are game data)
        var data = new byte[] { 0x80 | TName, 0x00, TNone };
        var ex = Assert.Throws<InvalidDataException>(() => QbStructBuffer.Parse(data));
        Assert.Contains("compression-table", ex.Message);
    }
}