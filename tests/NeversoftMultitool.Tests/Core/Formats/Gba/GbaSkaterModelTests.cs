using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS2 GBA 3D skater model: the content-located header/clip/frame
///     complex, the shared-mesh + per-character-colour split, and the GLB
///     conversion. Every structural number here was derived by closure arithmetic
///     during the original decode (see GbaSkaterModel) — a change means the locate
///     regressed, not that the ROM did.
/// </summary>
public sealed class GbaSkaterModelTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void LocatesTheFullModelComplexByContent()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var model = GbaSkaterModel.TryLocate(rom);
        Assert.NotNull(model);

        // The header identity also matches a second, clipless mesh header earlier in
        // the ROM (0x744C98) — the locate must walk past it to the skater complex.
        Assert.Equal(0x775CDC, model.HeaderOffset);
        Assert.Equal(864, model.FrameStride);
        Assert.Equal(new byte[] { 6, 16, 18, 4, 99, 3, 26, 0 }, model.VertCounts);
        Assert.Equal(new byte[] { 8, 16, 18, 6, 99, 4, 8, 0 }, model.NormCounts);
        Assert.Equal(new byte[] { 8, 16, 20, 6, 178, 2, 36, 0 }, model.FaceCounts);
        Assert.Equal(0x779DF4, model.FaceBankOffset);

        // The clip/tick boundary is solved from remap-length closure.
        Assert.Equal(221, model.ClipCount);
        Assert.Equal(7874, model.TickCount);
        Assert.Equal(4772, model.FrameCount);
        Assert.Equal(0x383BC, model.FramePoolOffset); // ends exactly at char 0's assets

        Assert.Equal(0x77582C, model.CharacterTableOffset);
        Assert.Equal(15, model.CharacterCount);
        Assert.Equal("Tony Hawk", GbaSkaterModel.TryGetCharacterName(rom, model, 0));
        Assert.Equal("Spider-Man", GbaSkaterModel.TryGetCharacterName(rom, model, 13));
        Assert.Equal("Mindy", GbaSkaterModel.TryGetCharacterName(rom, model, 14));
    }

    [Fact]
    public void FacesAndFramesStayInsideTheirSubObjects()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;

        var faces = GbaSkaterModel.ReadFaces(rom, model);
        Assert.Equal(266, faces.Count);
        Assert.All(faces, f =>
        {
            Assert.InRange(f.Material, 0, 45);
            Assert.True(f.V0 < model.VertCounts[f.SubObject]);
            Assert.True(f.V1 < model.VertCounts[f.SubObject]);
            Assert.True(f.V2 < model.VertCounts[f.SubObject]);
        });

        // Frame 0 decodes 172 vertices spanning exactly 101 z-up units (deck at
        // −16 under the feet, head at +85) — a fixed frame, so an exact pin.
        var verts = GbaSkaterModel.ReadFrameVertices(rom, model, 0);
        Assert.Equal(172, verts.Sum(sub => sub.Length));
        var all = verts.SelectMany(sub => sub).ToList();
        Assert.Equal(101, all.Max(v => v[2]) - all.Min(v => v[2]));

        // Every tick remaps to a physical frame inside the pool.
        var clips = GbaSkaterModel.ReadClips(rom, model);
        Assert.Equal(221, clips.Count);
        foreach (var clip in clips)
            for (var t = clip.TickStart; t < clip.TickStart + clip.TickCount; t++)
                Assert.InRange(GbaSkaterModel.FrameForTick(rom, model, t), 0, model.FrameCount - 1);
    }

    [Fact]
    public void CharacterColoursDressTheSkater()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;

        // Every character/outfit decodes a full 46-material colour set.
        for (var c = 0; c < model.CharacterCount; c++)
            Assert.NotNull(GbaSkaterModel.TryGetMaterialColors(rom, model, c, 0));

        // Spider-Man's suit is the can't-pass-by-accident check: material 0 is a
        // red ramp (184,8,24 mid shade) and material 3 a blue one (24,24,176) —
        // dominance, not exact shades, so the pin survives ramp-shade policy
        // changes.
        var spidey = GbaSkaterModel.TryGetMaterialColors(rom, model, 13, 0)!;
        Assert.True(spidey[0][0] > spidey[0][2] + 40, "Spider-Man material 0 should be red");
        Assert.True(spidey[3][2] > spidey[3][0] + 40, "Spider-Man material 3 should be blue");

        Assert.Null(GbaSkaterModel.TryGetMaterialColors(rom, model, 15, 0));
        Assert.Null(GbaSkaterModel.TryGetMaterialColors(rom, model, 0, 8));
    }

    /// <summary>
    ///     All 15 characters share one mesh, so each wears only the sub-objects
    ///     its roster record names. Three facts fix the reading beyond doubt:
    ///     the ONLY sub-object no character draws is the only empty one, the
    ///     only two every character draws are the body and the deck, and
    ///     sub-objects 1 and 2 occupy the same space at the feet with every
    ///     character taking exactly one.
    /// </summary>
    [Fact]
    public void EachCharacterWearsOnlyTheSubObjectsItsPartMaskNames()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;

        var masks = Enumerable.Range(0, model.CharacterCount)
            .Select(c => GbaSkaterModel.GetPartMask(rom, model, c))
            .ToArray();
        int Draws(int sub) => masks.Count(m => (m >> sub & 1) != 0);

        Assert.Equal(0, Draws(7));   // the empty sub-object, worn by nobody
        Assert.Equal(15, Draws(4));  // the body
        Assert.Equal(15, Draws(6));  // the deck
        Assert.All(masks, m =>
            Assert.Equal(1, (int)(m >> 1 & 1) + (int)(m >> 2 & 1))); // one leg style each

        // Sub-object 5 is a flat plane behind the head, worn by exactly the two
        // female skaters: the ponytail. Sub-object 0 sits on top of the head and
        // four characters wear it, Muska among them: the hood.
        var ponytail = Enumerable.Range(0, model.CharacterCount)
            .Where(c => (masks[c] >> 5 & 1) != 0)
            .Select(c => GbaSkaterModel.TryGetCharacterName(rom, model, c))
            .ToArray();
        Assert.Equal(["Elissa Steamer", "Mindy"], ponytail);
        Assert.Equal(4, Draws(0));
        Assert.NotEqual(0u, masks[8] & 1); // Chad Muska
    }

    [Fact]
    public void CarvedCharacterSuffixRoutesToTheMeshPipeline()
    {
        var route = MeshTypeDetector.DetectByName("13_spider_man.chr.gba");
        Assert.Equal(MeshFileKind.GbaModel, route.Kind);
        Assert.False(route.RequiresContentProbe);
        Assert.Equal(ModelSourceKind.GbaModel, MeshTypeDetector.ToSourceKind(route.Kind));
        Assert.Equal("13_spider_man", MeshTypeDetector.GetStem("13_spider_man.chr.gba"));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
        "Tony Hawk's Pro Skater 3 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)",
        "Tony Hawk's Pro Skater 4 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)",
        "Tony Hawk's Underground (USA, Europe).gba")]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)",
        "Tony Hawk's Underground 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)",
        "Tony Hawk's American Sk8land (USA).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
        "Tony Hawk's Downhill Jam (USA).gba")]
    public void LaterGamesDoNotClaimTheThps2MorphTargetComplex(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");

        // This is deliberately a format claim, not a claim that the games have no
        // 3D riders: Downhill Jam, for example, has its own independently decoded
        // rider container. None closes as THPS2's header + clip/remap + roster
        // complex, so routing them through the THPS2 decoder would be fabricated.
        Assert.Null(GbaSkaterModel.TryLocate(File.ReadAllBytes(path!)));
    }

    [CorpusFact]
    public void ConvertsSpiderManToAColouredModel()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom)!;

        var record = rom.AsSpan(model.CharacterTableOffset + 13 * 0x4C, 0x4C).ToArray();
        var native = new GbaModelNativeSource(record, rom, 13, "Spider-Man", Outfit: 0);
        var document = ModelDocument.CreateNative("13_spider_man", ModelSourceKind.GbaModel, native);
        GbaModelGeometryWriter.Populate(document, native);

        // Spider-Man's own part set: the shared mesh's 266 triangles minus
        // the sub-objects his roster mask leaves off (hood, ponytail, the
        // other leg style).
        Assert.Equal(234, document.TriangleCount);
        Assert.True(document.Materials.Count > 10); // one per used material ramp
        Assert.All(document.Materials, m => Assert.StartsWith("Spider-Man_m", m.Name));
    }
}
