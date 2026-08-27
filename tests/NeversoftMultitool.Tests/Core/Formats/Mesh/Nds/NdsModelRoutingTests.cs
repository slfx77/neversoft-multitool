using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

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
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", "1062/88609/862")]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", "1014/93309/944")]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", "1416/119015/1329")]
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

        // These are the pinned corpus figures: 1,062 + 1,014 + 1,416 = 3,492 models,
        // 88,609 + 93,309 + 119,015 = 300,933 triangles, 862 + 944 + 1,329 = 3,135
        // textured — the same totals the nds-mesh command reports.
        Assert.Equal(expected, $"{models}/{triangles}/{textured}");
    }
}
