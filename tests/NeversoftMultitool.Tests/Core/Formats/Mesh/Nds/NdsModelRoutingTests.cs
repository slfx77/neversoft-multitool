using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the per-entry route to a DS model: the road a GUI row and the generic
///     <c>mesh</c> command take, where there is one container entry in hand rather
///     than a whole cart to index.
///
///     What matters is that it reaches the same place as the dedicated
///     <c>nds-mesh</c> command, which indexes every bank and texel blob up front.
///     The per-entry route cannot do that — it asks for each companion by the name
///     the loader spells — so agreement is a real check, not a tautology.
/// </summary>
public sealed class NdsModelRoutingTests(TestPaths paths)
{
    [Fact]
    public void Detector_RoutesARecoveredGeometryNameToTheDsParser()
    {
        var route = MeshTypeDetector.DetectByName("0067ee06.532e1440.geometry.bin");
        Assert.Equal(MeshFileKind.NdsGeometry, route.Kind);
        Assert.Equal(ModelSourceKind.NdsModel, MeshTypeDetector.ToSourceKind(route.Kind));
        Assert.False(route.RequiresContentProbe);
        Assert.Equal("DS Model", route.DisplayFormat);
    }

    [Fact]
    public void Detector_NeverClaimsTheBareBinEveryDsAssetIs()
    {
        // An unnamed DS file extracts as <crc>.bin, and so does every other DS asset,
        // so the name must not claim it. Nothing is lost by that: of the geometry
        // files the carts leave unnamed, not one carries a vertex.
        Assert.Equal(MeshFileKind.None, MeshTypeDetector.DetectByName("3f2a10c8.bin").Kind);
        Assert.Equal(MeshFileKind.None, MeshTypeDetector.DetectByName("track.hwas").Kind);
    }

    [Fact]
    public void Policy_ADsLevelIsLevelContentAndGetsTheSkatersEye()
    {
        // A DS level is a synthetic row over a model SET, so the scanner says what it
        // is outright — there is no shape to infer it from, since the container spells
        // a level's world pieces and a skater's body parts identically.
        var level = new MeshLevelFacts(
            "Level_Alcatraz_Visual", "cart.nds::Level_Alcatraz_Visual", "Level_Alcatraz_Visual",
            IsPsx: false, IsN64Model: false, IsPs2Geom: false, PsxIsSuperModel: false,
            PsxMeshFormatRevision.Unknown, Ps2SceneSubFormat.None,
            HasPlacedPsxCompanion: false, HasSupportedLevelObjectCompanion: false,
            N64MaxBoundsRadius: 0f, ObjectCount: 135, IsNdsLevel: true);

        Assert.True(MeshLevelPolicy.IsLevelContent(level));
        Assert.Equal(MeshLevelPolicy.NdsLevelWalkEyeHeight,
            MeshLevelPolicy.ResolveWalkEyeHeight(level, isLevel: true));

        // A DS MODEL row is not a level, and gets no eye height.
        var model = level with { IsNdsLevel = false, FileName = "a.b.geometry.bin" };
        Assert.False(MeshLevelPolicy.IsLevelContent(model));
        Assert.Null(MeshLevelPolicy.ResolveWalkEyeHeight(model, isLevel: false));
    }

    /// <summary>
    ///     The road the Levels tab takes to convert a row: the parser composites the
    ///     whole set from the container behind any one of its pieces. It must land on
    ///     the same document the nds-mesh command builds, because they now share
    ///     NdsLevelComposer and would otherwise have diverged silently.
    /// </summary>
    [CorpusFact]
    public void RealCart_ALevelRowCompositesTheSameDocumentTheCommandDoes()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds");
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        var cart = ArchiveAssetBackend.TryOpen(romPath!);
        var backend = cart!.TryOpenNested(
            cart.FileSystem.FindByPath("vvobj/generated/gob/main.gob")!);
        var container = backend!.FileSystem;
        var naming = NeversoftMultitool.Core.Formats.Mesh.Nds.NdsModelNaming.For(container);
        var idA = naming.Sets.First(s => s.Value == "Level_Warehouse_Visual").Key;

        // What the command builds.
        var composed = NeversoftMultitool.Core.Formats.Mesh.Nds.NdsLevelComposer.Compose(
            container, idA, "Level_Warehouse_Visual",
            NeversoftMultitool.Core.Formats.Mesh.Nds.NdsTextureLookup.Build(container),
            naming, placeEntities: true);
        Assert.NotNull(composed);
        Assert.True(composed!.Entities > 0);

        // What a tab row builds: any one piece of the set names it.
        var piece = container.Entries.First(e =>
            e.Name.EndsWith(MeshTypeDetector.NdsGeometrySuffix, StringComparison.OrdinalIgnoreCase)
            && NdsModelSet.TryParseGeometryName(".\\" + e.Name, out var a, out _) && a == idA);
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new ArchiveAssetSource(backend, piece),
            FileName = piece.Name,
            OutputStem = "Level_Warehouse_Visual",
            SourceKind = ModelSourceKind.NdsLevel
        });

        Assert.Equal(composed.Document.TriangleCount, document.TriangleCount);
        Assert.Equal(composed.Document.Meshes.Count, document.Meshes.Count);
        Assert.Equal(ModelSourceKind.NdsLevel, document.SourceKind);
    }

    /// <summary>
    ///     The animation route a GUI selection takes: the clips are companion files
    ///     the loader names from the model's own two ids, so a per-entry caller
    ///     enumerates them by asking rather than by indexing the container.
    /// </summary>
    [CorpusFact]
    public void RealCart_ASelectionOfClipsBakesThroughTheParser()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds");
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        var cart = ArchiveAssetBackend.TryOpen(romPath!);
        var backend = cart!.TryOpenNested(
            cart.FileSystem.FindByPath("vvobj/generated/gob/main.gob")!);
        // proMullen — the skater, whose set ships the 225-clip library.
        var entry = backend!.FileSystem.FindByPath("a4754788.8568a2d5.geometry.bin");
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);

        var clips = NeversoftMultitool.Core.Formats.Mesh.Nds.NdsModelCompanions.ReadClips(source);
        Assert.Equal(225, clips.Count);
        // Contiguous from zero — the property that makes "ask for the next one"
        // a complete enumeration rather than a truncation.
        Assert.Equal(Enumerable.Range(0, 225), clips.Select(c => c.Index));

        var parser = new MeshModelParser();
        ModelDocument Parse(IReadOnlyList<int>? indices) => parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry!.Name,
            SourceKind = ModelSourceKind.NdsModel,
            OutputStem = "skater",
            NdsAnimationIndices = indices
        });

        // No selection: the static document.
        var stat = Parse(null);
        Assert.Empty(stat.Animations);
        Assert.True(stat.TriangleCount > 0);

        // A selection of three bakes exactly three, and does not disturb the mesh.
        var picked = Parse([0, 7, 42]);
        Assert.Equal(3, picked.Animations.Count);
        Assert.Equal(stat.TriangleCount, picked.TriangleCount);

        // An index the library does not hold is ignored, not invented, and the
        // document falls back to static rather than erroring.
        var none = Parse([9999]);
        Assert.Empty(none.Animations);
        Assert.Equal(stat.TriangleCount, none.TriangleCount);
    }

    /// <summary>
    ///     The naming a GUI row and the CLI share. An entity is a one-piece set, so
    ///     its set name IS its name; a level piece takes the set plus the artist's
    ///     own object name from the manifest.
    /// </summary>
    [CorpusFact]
    public void RealCart_NamesAModelTheWayTheStudioDid()
    {
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds");
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath("vvobj/generated/gob/main.gob")!);
        var names = NeversoftMultitool.Core.Formats.Mesh.Nds.NdsModelNaming.For(gob!);

        // CRC-32 of "skate_s"; an entity set carries the same id twice.
        Assert.Equal("skate_s", names.StemFor(0xD8E3EBB1, 0xD8E3EBB1));
        // A level piece: the set is named, and the manifest names the piece.
        var alcatraz = names.Sets.First(s => s.Value == "Level_Alcatraz_Visual").Key;
        var piece = names.StemFor(alcatraz, 0xD81B6ED9);
        Assert.Equal("Level_Alcatraz_Visual__RAILS_Section01_12", piece);
        // Nothing is invented for a set the cart does not name.
        Assert.Null(names.StemFor(0x12345678, 0x12345678));
    }

    /// <summary>
    ///     The GUI reaches a cart's models by WALKING it — enqueueing any entry it
    ///     cannot classify and asking whether it opens as a nested archive — not by
    ///     knowing where the GOB lives. This pins that the walk gets there, which is
    ///     the one part of the tab's road the per-entry parse test above assumes.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", 1167)]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", 1325)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", 1858)]
    public void RealCart_WalkingTheCartFindsItsGeometryWithoutBeingToldWhereTheGobIs(
        string build, string rom, int expectedCandidates)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        var root = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(root);

        var candidates = 0;
        var pending = new Queue<ArchiveAssetBackend>();
        pending.Enqueue(root!);
        while (pending.Count > 0)
        {
            var backend = pending.Dequeue();
            foreach (var entry in backend.Entries)
            {
                if (MeshTypeDetector.IsMeshCandidate(entry.Name))
                {
                    candidates++;
                    continue;
                }

                var nested = backend.TryOpenNested(entry);
                if (nested != null)
                    pending.Enqueue(nested);
            }
        }

        Assert.Equal(expectedCandidates, candidates);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", "1062/88653/862")]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", "1014/93313/944")]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", "1416/119045/1329")]
    public void RealCart_ThePerEntryRouteConvertsWhatTheCommandDoes(
        string build, string rom, string gobPath, string expected)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        var cart = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(cart);
        var backend = cart!.TryOpenNested(cart.FileSystem.FindByPath(gobPath)!);
        Assert.NotNull(backend);

        var parser = new MeshModelParser();
        var models = 0;
        var triangles = 0;
        var textured = 0;

        foreach (var entry in backend!.FileSystem.Entries)
        {
            if (!entry.Name.EndsWith(
                    MeshTypeDetector.NdsGeometrySuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = new ArchiveAssetSource(backend, entry);
            ModelDocument document;
            try
            {
                document = parser.Parse(new MeshImportRequest
                {
                    Source = source,
                    SourceKind = ModelSourceKind.NdsModel,
                    FileName = entry.Name,
                    OutputStem = MeshTypeDetector.GetStem(entry.Name)
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                continue;
            }

            // The command counts a model only when it has geometry; the authored-empty
            // files convert successfully and contribute nothing.
            if (document.TriangleCount == 0)
                continue;
            models++;
            triangles += document.TriangleCount;
            if (document.Textures.Count > 0)
                textured++;
        }

        // The pinned corpus figures: 1,062 + 1,014 + 1,416 = 3,492 models,
        // 88,653 + 93,313 + 119,045 = 301,011 triangles, 862 + 944 + 1,329 = 3,135
        // textured — the same totals the nds-mesh command reports.
        //
        // The triangle counts depend on ModelDocumentGeometryAdapter's degeneracy
        // rule, which is shared by every format. They rose by 44 / 4 / 30 when that
        // rule became scale-relative (clamped so it can only ever RELAX), because a
        // fixed area threshold was culling small-but-real DS triangles.
        Assert.Equal(expected, $"{models}/{triangles}/{textured}");
    }
}
