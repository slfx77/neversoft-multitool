using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins how Downhill Jam and Proving Ground bind a model to its clip.
///
///     Those two carts give a model ONE clip under a single opaque id, and that id
///     is recoverable no other way: it hashes onto no authored name and coincides
///     with no geometry id. The code that asks for the file holds it inside the
///     record that also carries the geometry pair, three and two words ahead.
///
///     What makes that a measurement rather than a pattern-match is the shape of the
///     result — a bijection, every id used, every id landing on a pair the container
///     really holds — against a control of values drawn from the same word pool,
///     which binds nothing.
/// </summary>
public sealed class NdsAnimationBindingTests(TestPaths paths)
{
    [Fact]
    public void AnimationName_MatchesOnlyTheOneIdForm()
    {
        Assert.True(NdsModelSet.TryParseAnimationName(".\\8309354d.animation.bin", out var id));
        Assert.Equal(0x8309354Du, id);

        // Sk8land's indexed clips carry two ids and an ordinal — a different family.
        Assert.False(NdsModelSet.TryParseAnimationName(
            ".\\a4754788.8568a2d5.7.animation.bin", out _));
        Assert.False(NdsModelSet.TryParseAnimationName(".\\8309354d.geometry.bin", out _));
        Assert.False(NdsModelSet.TryParseAnimationName(null, out _));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 322)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 467)]
    public void RealCart_EveryClipBindsToExactlyOnePiece(
        string build, string rom, string gobPath, int expected)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);

        var clips = new HashSet<uint>();
        var pieces = new HashSet<(uint, uint)>();
        foreach (var entry in gob!.Entries)
        {
            var name = GobNames.TryResolve(entry.Crc);
            if (NdsModelSet.TryParseAnimationName(name, out var id))
                clips.Add(id);
            else if (NdsModelSet.TryParseGeometryName(name, out var idA, out var idB))
                pieces.Add((idA, idB));
        }

        var bindings = NdsCartManifests.ReadAnimationBindings(cart, gob);

        // Every clip is used, exactly once, and every target is a real piece —
        // a bijection with nothing left over on either side.
        Assert.Equal(expected, clips.Count);
        Assert.Equal(expected, bindings.Count);
        Assert.Equal(expected, bindings.Values.Distinct().Count());
        Assert.All(bindings, b => Assert.Contains(b.Key, pieces));
        Assert.All(bindings, b => Assert.Contains(b.Value, clips));
    }

    [CorpusFact]
    public void Sk8land_BindsNothingThisWay_BecauseItNamesItsClipsInstead()
    {
        // The control that the rule is not simply finding pairs everywhere: Sk8land
        // spells an indexed library per model and ships no one-id animation file, so
        // the same code over the same kind of cart binds zero.
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds");
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath("vvobj/generated/gob/main.gob")!);
        Assert.Empty(NdsCartManifests.ReadAnimationBindings(cart, gob));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 322, 121)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 467, 131)]
    public void RealCart_EveryBoundClipAppliesAndMostOfThemBake(
        string build, string rom, string gobPath, int expectedApplicable, int expectedBaked)
    {
        // Two separate numbers, and the first is evidence about the BINDING.
        //
        // NdsPoseScatter.CanApply compares a clip's channel counts against the
        // geometry's joint-flag census, and it accepts EVERY bound pair in both
        // carts. That is not something a wrong pairing survives: the counts vary
        // widely across models, so a shuffled binding would mismatch almost
        // everywhere. It is an independent check on a rule derived from the code.
        //
        // Baking is a further step and declines more: a model whose display list
        // leaves a joint singular has no inverse bind matrix, and the writer refuses
        // rather than hand the exporter a matrix it cannot invert.
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        var cart = ArchiveAssetBackend.TryOpen(romPath!);
        var backend = cart!.TryOpenNested(cart.FileSystem.FindByPath(gobPath)!);

        var applicable = 0;
        var baked = 0;
        foreach (var entry in backend!.FileSystem.Entries)
        {
            if (!NdsModelSet.TryParseGeometryName(
                    GobNames.TryResolve(entry.Crc), out _, out _))
            {
                continue;
            }

            var source = new ArchiveAssetSource(backend, entry);
            byte[] data;
            try
            {
                data = source.ReadBytes();
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
                continue;
            var clips = NdsModelCompanions.ReadClips(source);
            if (clips.Count == 0 || !NdsPoseScatter.CanApply(geometry, clips[0].Clip))
                continue;
            applicable++;

            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = source,
                FileName = entry.Name,
                OutputStem = "clip",
                SourceKind = ModelSourceKind.NdsModel,
                IncludeAllNdsAnimations = true
            });
            if (document.Animations.Count > 0)
                baked++;
        }

        Assert.Equal(expectedApplicable, applicable);
        Assert.Equal(expectedBaked, baked);
    }
}
