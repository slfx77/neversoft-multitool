using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the recovery of every DS model set's authored name.
///
///     The claim is unusually strong for a name recovery, because nothing is being
///     searched for: a model set's id IS the CRC-32 of its name, so a string lying in
///     the cart's code either re-hashes onto a set or it does not. What the corpus
///     numbers below establish is that the studio left EVERY name in the image.
/// </summary>
public sealed class NdsSetNamesTests(TestPaths paths)
{
    [Fact]
    public void IsLevel_SeparatesALevelsOwnSetFromItsSky()
    {
        Assert.True(NdsSetNames.IsLevel("Level_Alcatraz_Visual"));
        Assert.False(NdsSetNames.IsLevel("Level_Alcatraz_Sky_Visual"));
        // The front end is a level in its own right: it has world geometry
        // and its own Frontend_Collision.prp, exactly like a skateable level.
        Assert.True(NdsSetNames.IsLevel("Frontend_Visual"));
        Assert.False(NdsSetNames.IsLevel("skate_s"));
        Assert.False(NdsSetNames.IsLevel(null));
    }

    [Fact]
    public void Harvest_DeclinesAnIdTwoDifferentStringsWouldClaim()
    {
        // Contrived: two spellings can only collide by CRC accident, and none does
        // in the shipped carts — but the rule that governs that case is worth
        // pinning, because the honest answer to an ambiguous name is no name.
        const uint id = 0x2A2A2A2A;
        var regions = new List<(string, uint, byte[])>
        {
            ("arm9", 0u, System.Text.Encoding.ASCII.GetBytes("\0alpha\0beta\0"))
        };

        var names = NdsSetNames.Harvest(regions, new HashSet<uint> { id });
        Assert.Empty(names);
    }

    [Fact]
    public void Harvest_NamesASetWhoseNameTheImageSpells()
    {
        // CRC-32 of "skate_s", the id its one-piece model set really carries.
        const uint skateS = 0xD8E3EBB1;
        var regions = new List<(string, uint, byte[])>
        {
            ("arm9", 0u, System.Text.Encoding.ASCII.GetBytes("\0padding\0skate_s\0more\0"))
        };

        var names = NdsSetNames.Harvest(regions, new HashSet<uint> { skateS });
        Assert.Equal("skate_s", Assert.Contains(skateS, names));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 196, 8)]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 124, 7)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 160, 8)]
    public void RealCart_EverySetIsNamed_AndEveryLevelPairsWithItsOwnDataFile(
        string build, string rom, string gobPath, int expectedSets, int expectedLevels)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var names = NdsCartManifests.ReadSetNames(cart, gob!);
        var sets = NdsCartManifests.Read(cart, gob!);

        // Every model set the container groups is named — not most of them.
        Assert.Equal(expectedSets, names.Count);

        // Corroboration from a source that shares no machinery with the hash: a
        // level's model set and its per-level data file are spelled by different
        // halves of the build, and they agree one-for-one.
        var levels = names.Values.Where(NdsSetNames.IsLevel).ToList();
        Assert.Equal(expectedLevels, levels.Count);
        foreach (var level in levels)
        {
            var stem = level[..^NdsSetNames.LevelSuffix.Length];
            Assert.NotNull(gob!.FindByPath($"{stem}_Collision.prp"));
        }

        // The manifests and the names describe the same sets, so a manifest never
        // turns up for a set the names do not cover.
        foreach (var idA in sets.Keys)
            Assert.Contains(idA, names);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", "")]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", "")]
    // Proving Ground's front end is the one set where the two rules part company,
    // and the NAME is right: it is a real 3D scene with its own collision file, but
    // its whole span is 78 units, under the 93 the measured rule needs. That is the
    // case that makes the stated classification worth preferring rather than merely
    // confirming — a compact scene is invisible to a size test.
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", "Frontend_Visual")]
    public void RealCart_TheStatedLevelAgreesWithTheMeasuredOne(
        string build, string rom, string gobPath, string expectedDisagreements)
    {
        // NdsModelSetBounds decides "level or many-part model" from the pieces,
        // because the container spells both the same way. The name says it outright.
        // Where they differ the name wins, so the exceptions are pinned by name.
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        var names = NdsCartManifests.ReadSetNames(cart, gob!);

        var pieces = new Dictionary<uint, List<NdsGeometryFile>>();
        foreach (var entry in gob!.Entries)
        {
            if (!NdsModelSet.TryParseGeometryName(
                    NeversoftMultitool.Core.Formats.Gob.GobNames.TryResolve(entry.Crc),
                    out var idA, out _))
            {
                continue;
            }

            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
                continue;
            if (!pieces.TryGetValue(idA, out var list))
                pieces[idA] = list = [];
            list.Add(geometry);
        }

        var disagreements = new List<string>();
        foreach (var (idA, list) in pieces)
        {
            if (list.Count < 2 || !names.TryGetValue(idA, out var name))
                continue;
            var stated = NdsSetNames.IsLevel(name);
            var measured = NdsModelSetBounds.IsWorldScale(list);
            if (stated != measured)
            {
                // Only ever the measured rule under-calling a real level.
                Assert.True(stated, $"{name} measured as a level but is not named one");
                disagreements.Add(name);
            }
        }

        disagreements.Sort(StringComparer.Ordinal);
        Assert.Equal(expectedDisagreements, string.Join(",", disagreements));
    }
}
