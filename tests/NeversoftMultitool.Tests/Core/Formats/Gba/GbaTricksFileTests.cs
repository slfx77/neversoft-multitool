using System.Numerics;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS2 GBA cart's embedded tricks.bin and the clip names it gives
///     the skater's animation bank. The naming rests on a semantic oracle rather
///     than on the byte layout alone: a clip a trick NAMES as a kickflip has to
///     roll the deck once, a triple kickflip three times, and a grind not at all.
/// </summary>
public sealed class GbaTricksFileTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void LocatesTheTrickTableByContentAndParsesEveryTrick()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var tricks = GbaTricksFile.TryRead(rom);
        Assert.NotNull(tricks);
        Assert.Equal(174, tricks.Count);
        Assert.Equal(146, tricks.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());

        // Every clip a trick plays is inside the model's own clip table — the
        // grammar is decoded from the ROM's dispatcher, and a single wrong
        // opcode width would desynchronise the stream into out-of-range junk.
        var model = GbaSkaterModel.TryLocate(rom)!;
        Assert.All(tricks.SelectMany(t => t.ClipIndices),
            clip => Assert.InRange(clip, 0, model.ClipCount - 1));

        var names = tricks.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Kickflip", names);
        Assert.Contains("{The 900}", names);
        Assert.Contains("KICKFLIP", names); // the separate special-variant list
    }

    [Fact]
    public void NamesOnlyTheClipsASingleTrickOwns()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;

        var names = GbaTricksFile.TryBuildClipNames(rom, model.ClipCount);
        Assert.NotNull(names);
        Assert.Equal(105, names.Count);

        Assert.Equal("Kickflip", names[20]);
        Assert.Equal("HeelFlip", names[21]);
        Assert.Equal("Impossible", names[22]);
        Assert.Equal("{The 900}", names[181]);
        // The uppercase list is the SPECIAL-variant animation set, not a
        // duplicate: it plays different clips, so both spellings get names.
        Assert.Equal("KICKFLIP", names[149]);

        // Clips two tricks share keep their synthetic label rather than taking
        // an arbitrary owner's name. These particular collisions are real
        // skating identities — a backside boardslide IS a frontside lipslide,
        // and a backside Smith IS a frontside feeble — which is itself evidence
        // the extraction is reading trick semantics and not byte coincidences.
        Assert.DoesNotContain(136, names.Keys); // BS Boardslide / FS Lipslide
        Assert.DoesNotContain(137, names.Keys); // BS Lipslide / FS Boardslide
        Assert.DoesNotContain(58, names.Keys);  // BS Smith / FS Feeble
        Assert.DoesNotContain(74, names.Keys);  // BS Feeble / FS Smith
    }

    /// <summary>
    ///     The check that cannot pass by accident: a name that STATES how many
    ///     times the board flips must match what the clip's deck actually does.
    ///     Vertex correspondence is exact across morph frames, so three fixed
    ///     deck vertices give a stable per-frame plane normal.
    /// </summary>
    [CorpusFact]
    public void NamedFlipTricksRollTheDeckAsManyTimesAsTheirNameClaims()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;
        var names = GbaTricksFile.TryBuildClipNames(rom, model.ClipCount)!;
        var clips = GbaSkaterModel.ReadClips(rom, model);
        var byName = names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

        (string Name, int Flips)[] expectations =
        [
            ("Kickflip", 1), ("HeelFlip", 1), ("Impossible", 1),
            ("{Triple Kickflip}", 3), ("{Triple Heelflip}", 3), ("{Double Hardflip}", 2),
            ("Nosegrind", 0), ("Melon", 0)  // a grind and a grab hold the deck flat
        ];

        foreach (var (name, flips) in expectations)
        {
            Assert.True(byName.TryGetValue(name, out var clipIndex), $"'{name}' should own a clip");
            var measured = MeasureDeckRotationDegrees(rom, model, clips[clipIndex]);
            Assert.Equal(flips, (int)Math.Round(measured / 360.0));
        }

        // The same measurement one clip off must NOT reproduce the claim — the
        // binding is exact, not a neighbourhood correlation.
        var kickflip = byName["Kickflip"];
        Assert.NotEqual(1, (int)Math.Round(
            MeasureDeckRotationDegrees(rom, model, clips[kickflip - 1]) / 360.0));
    }

    [CorpusFact]
    public void OtherGbaCartsCarryNoTrickTable()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
            "Tony Hawk's Pro Skater 3 (USA, Europe).gba");
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");

        // The locator fails closed rather than reading a lookalike structure.
        Assert.Null(GbaTricksFile.TryRead(File.ReadAllBytes(romPath!)));
    }

    /// <summary>
    ///     Total rotation of the deck's plane across a clip, in degrees. The deck
    ///     is sub-object 6; three fixed, well-separated vertices define its plane
    ///     in every frame because the model is morph-target (vertex i is the same
    ///     vertex in every pose).
    /// </summary>
    private static double MeasureDeckRotationDegrees(
        ReadOnlySpan<byte> rom, GbaSkaterModel.ModelInfo model, GbaSkaterModel.Clip clip)
    {
        const int deckSubObject = 6;
        var bind = GbaSkaterModel.ReadFrameVertices(rom, model, 0)[deckSubObject];
        var (a, b, c) = LargestTriangle(bind);

        var frames = GbaSkaterModel.ClipFrames(rom, model, clip);
        var total = 0.0;
        Vector3? previous = null;
        foreach (var frame in frames)
        {
            var deck = GbaSkaterModel.ReadFrameVertices(rom, model, frame)[deckSubObject];
            var normal = Vector3.Normalize(Vector3.Cross(
                ToVector(deck[b]) - ToVector(deck[a]), ToVector(deck[c]) - ToVector(deck[a])));
            if (previous.HasValue)
                total += Math.Acos(Math.Clamp(Vector3.Dot(normal, previous.Value), -1f, 1f))
                         * 180.0 / Math.PI;
            previous = normal;
        }

        return total;
    }

    private static (int A, int B, int C) LargestTriangle(sbyte[][] vertices)
    {
        var best = (Area: -1f, A: 0, B: 1, C: 2);
        for (var i = 0; i < vertices.Length; i++)
        for (var j = i + 1; j < vertices.Length; j++)
        for (var k = j + 1; k < vertices.Length; k++)
        {
            var area = Vector3.Cross(
                ToVector(vertices[j]) - ToVector(vertices[i]),
                ToVector(vertices[k]) - ToVector(vertices[i])).Length();
            if (area > best.Area)
                best = (area, i, j, k);
        }

        return (best.A, best.B, best.C);
    }

    private static Vector3 ToVector(sbyte[] v) => new(v[0], v[1], v[2]);
}
