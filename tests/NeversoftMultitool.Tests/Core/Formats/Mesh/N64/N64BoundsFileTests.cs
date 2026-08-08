using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the <c>bounds.bin</c> grammar (2026-08-07): big-endian
///     <c>u32 count</c> then that many 24-byte records, one per MESH.
/// </summary>
public sealed class N64BoundsFileTests
{
    private static byte[] Build(params (uint Kind, uint RawRadius, short[] Axes)[] records)
    {
        var data = new byte[4 + records.Length * N64BoundsFile.RecordSize];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            var offset = 4 + i * N64BoundsFile.RecordSize;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), records[i].Kind);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4), records[i].RawRadius);
            for (var a = 0; a < 6; a++)
                BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset + 8 + a * 2), records[i].Axes[a]);
        }

        return data;
    }

    [Fact]
    public void Parse_ReadsKindRadiusAndExtents()
    {
        var data = Build(
            (8u, 729_088u, [-72, 73, -78, 110, -118, 120]),
            (10u, 4096u, [-1, 1, -2, 2, -3, 3]));

        var records = N64BoundsFile.Parse(data);
        Assert.Equal(2, records.Count);

        Assert.Equal(8u, records[0].Kind);
        Assert.Equal(178f, records[0].Radius);   // 729088 / 4096, the c_kart value
        Assert.Equal(-72, records[0].MinX);
        Assert.Equal(73, records[0].MaxX);
        Assert.Equal(-78, records[0].MinY);
        Assert.Equal(110, records[0].MaxY);
        Assert.Equal(-118, records[0].MinZ);
        Assert.Equal(120, records[0].MaxZ);

        Assert.Equal(10u, records[1].Kind);
        Assert.Equal(1f, records[1].Radius);
    }

    [Fact]
    public void MaxRadius_TakesTheLargestRecord()
    {
        var data = Build(
            (8u, 4096u, [0, 0, 0, 0, 0, 0]),
            (8u, 40_960u, [0, 0, 0, 0, 0, 0]),
            (8u, 8192u, [0, 0, 0, 0, 0, 0]));

        Assert.Equal(10f, N64BoundsFile.MaxRadius(data));
    }

    /// <summary>
    ///     80 of THPS2's 116 bundles carry four zero bytes after the last
    ///     record, so the parse must accept a tail rather than pin the length —
    ///     pinning it would reject two thirds of the corpus.
    /// </summary>
    [Fact]
    public void Parse_ToleratesTrailingPadding()
    {
        var padded = Build((8u, 4096u, [0, 0, 0, 0, 0, 0])).Concat(new byte[4]).ToArray();

        Assert.Single(N64BoundsFile.Parse(padded));
        Assert.Equal(1f, N64BoundsFile.MaxRadius(padded));
    }

    /// <summary>
    ///     An authored-empty stub ships a 4-byte bounds.bin. 144 of 594 bundles
    ///     are stubs, so this is the normal case, not an error case.
    /// </summary>
    [Fact]
    public void Parse_ReturnsNothingForStubsAndTruncatedData()
    {
        Assert.Empty(N64BoundsFile.Parse([]));
        Assert.Empty(N64BoundsFile.Parse([0, 0, 0, 0]));
        Assert.Empty(N64BoundsFile.Parse([0, 0, 0, 1]));            // claims 1, carries none
        Assert.Equal(0f, N64BoundsFile.MaxRadius([0, 0, 0, 0]));

        // A count far beyond the buffer must be rejected, not trusted.
        var lying = new byte[4 + N64BoundsFile.RecordSize];
        BinaryPrimitives.WriteUInt32BigEndian(lying, 0xFFFF_FFFF);
        Assert.Empty(N64BoundsFile.Parse(lying));
        Assert.Equal(0f, N64BoundsFile.MaxRadius(lying));
    }
}
