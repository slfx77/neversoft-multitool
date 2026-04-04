using System.Numerics;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

public sealed class ThawZoneTexHeaderSourceResolverTests
{
    [Fact]
    public void BuildHeaderSourceEntryGroupsFromHeaderLists_TracksSourceIndices()
    {
        var first = CreateEntry(0x11111111, CreateTex0(10, 4, Ps2TexPixelDecoder.PSMT4, 64, 64, 20,
            Ps2TexPixelDecoder.PSMCT16));
        var second = CreateEntry(0x22222222, CreateTex0(10, 4, Ps2TexPixelDecoder.PSMT4, 32, 64, 20,
            Ps2TexPixelDecoder.PSMCT16));

        IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>[] headerLists =
        [
            [first],
            [second]
        ];

        var group = Assert.Single(ThawZoneTexFile.BuildHeaderSourceEntryGroupsFromHeaderLists(headerLists)).Value;

        Assert.Collection(
            group,
            entry =>
            {
                Assert.Equal(first.Checksum, entry.Entry.Checksum);
                Assert.Equal(0, entry.SourceIndex);
            },
            entry =>
            {
                Assert.Equal(second.Checksum, entry.Entry.Checksum);
                Assert.Equal(1, entry.SourceIndex);
            });
    }

    [Fact]
    public void TryResolveHeaderSourceEntry_PrefersBestScoringCandidate()
    {
        var requestedTex0 = CreateTex0(10, 4, Ps2TexPixelDecoder.PSMT4, 64, 64, 20, Ps2TexPixelDecoder.PSMCT16);
        var worse = CreateEntry(0xAAAAAAAA, CreateTex0(10, 4, Ps2TexPixelDecoder.PSMT4, 32, 64, 20,
            Ps2TexPixelDecoder.PSMCT16));
        var better = CreateEntry(0xBBBBBBBB, requestedTex0, 2, 8, 16);
        var tex1 = 2ul << 2;

        IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>[] headerLists =
        [
            [worse],
            [better]
        ];

        var exactMap = ThawZoneTexFile.BuildHeaderSourceEntryMapByTex0FromHeaderLists(
            Array.Empty<IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>>());
        var groups = ThawZoneTexFile.BuildHeaderSourceEntryGroupsFromHeaderLists(headerLists);

        var resolved = ThawZoneTexFile.TryResolveHeaderSourceEntry(requestedTex0, tex1, exactMap, groups,
            out var sourceEntry);

        Assert.True(resolved);
        Assert.Equal(better.Checksum, sourceEntry.Entry.Checksum);
        Assert.Equal(1, sourceEntry.SourceIndex);
    }

    private static ThawZoneTexFile.ZoneTexHeaderEntry CreateEntry(
        uint checksum,
        ulong tex0,
        uint mipLevelCount = 0,
        uint uploadOffset = 0,
        uint dataOffset = 0)
    {
        return new ThawZoneTexFile.ZoneTexHeaderEntry(
            checksum,
            tex0,
            0x100,
            dataOffset,
            0x20,
            uploadOffset,
            mipLevelCount);
    }

    private static ulong CreateTex0(uint tbp, uint tbw, uint psm, int width, int height, uint cbp, uint cpsm)
    {
        return tbp
               | ((ulong)tbw << 14)
               | ((ulong)psm << 20)
               | ((ulong)BitOperations.TrailingZeroCount((uint)width) << 26)
               | ((ulong)BitOperations.TrailingZeroCount((uint)height) << 30)
               | ((ulong)cbp << 37)
               | ((ulong)cpsm << 51);
    }
}