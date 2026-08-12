using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Pins the two late PS1 ports added 2026-08-04 — THPS3 by Shaba Games
///     (SLUS-01419) and THPS4 by Vicarious Visions (SLUS-01485). Both ship the
///     Neversoft data lineage wholesale: hashed CD.HED + CD.WAD (names cracked
///     into <c>HedDictionaryPart5</c>), PSX v4 meshes, v2.0 TRGs, VAB/SFX
///     audio, and items.psx/skmedals.psx pickups. A signature scan against the
///     symboled THPS2 proto found the anim codec (<c>DecompressStream</c>),
///     spool lookups (<c>Spool_FindAnim</c>/<c>Spool_GetModel</c>) and math
///     helpers byte-identical (1.000) in BOTH EXEs — only the playback state
///     machine, loader, and spool memory layer were reworked, which is why the
///     existing parsers cover the data unchanged.
///
///     Level naming: THPS3 uses <c>aa&lt;stem&gt;</c> bare-stem levels
///     (recognized by their <c>_o.psx</c> + <c>_t.trg</c> siblings); THPS4 uses
///     Spider-Man's <c>_g.psx</c> geometry convention.
/// </summary>
public sealed class ThpsLatePs1PortTests(TestPaths paths)
{
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)";
    private const string Thps4Build = "Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)";

    [CorpusFact]
    public void Thps3_Japan_ConvertsWithBankAndTriggerLayers()
    {
        var path = paths.FindSampleFile(Thps3Build, "aajap.psx");
        Assert.SkipWhen(path == null, "THPS3 PS1 sample not available");

        var document = ParseDocument(path!);

        // Pinned 2026-08-04: 8,713 authored level triangles + the 2-object
        // aajap_o bank (+64). Levels carry 86 textures.
        Assert.Equal(8_777, document.TriangleCount);
        Assert.Equal(86, document.Textures.Count);
        // The bare-stem companion recognition fires (aajap_o.psx + aajap_t.trg
        // siblings), so the bank's meshes place into the level.
        Assert.Contains(document.Meshes, static mesh =>
            mesh.Name.StartsWith("objects_", StringComparison.Ordinal)
            || mesh.Name.StartsWith("mesh_", StringComparison.Ordinal));
    }

    [CorpusFact]
    public void Thps4_College_GeometryConvertsUnderTheSpiderManConvention()
    {
        var path = paths.FindSampleFile(Thps4Build, "a1col_g.psx");
        Assert.SkipWhen(path == null, "THPS4 PS1 sample not available");

        var document = ParseDocument(path!);

        // Pinned 2026-08-04. The _g suffix routes through the Spider-Man-style
        // level companion path (a1col_o.psx bank + a1col_t.trg).
        Assert.Equal(10_586, document.TriangleCount);
        Assert.True(document.Textures.Count > 50);
    }

    /// <summary>
    ///     Closes the visual report that College's sky gradient might be
    ///     vertically reversed. The bank itself authors the dome dark at its
    ///     native-space top and bright toward the horizon. PSX native +Y is
    ///     down, so the ordinary (x, -y, -z) glTF handedness conversion must
    ///     reverse the Y ordering while keeping each corner's palette colour
    ///     attached to it. The viewer then draws the dome unlit over the TRG's
    ///     light-blue SetSkyColor backdrop; no converter-side colour reversal
    ///     is involved.
    /// </summary>
    [CorpusFact]
    public void Thps4_College_SkyGradientPreservesTheAuthoredVerticalDirection()
    {
        var levelPath = paths.FindSampleFile(Thps4Build, "a1col_g.psx");
        var bankPath = paths.FindSampleFile(Thps4Build, "a1col_o.psx");
        Assert.SkipWhen(levelPath == null || bankPath == null, "THPS4 PS1 College samples not available");

        var bank = Assert.IsType<PsxMeshFile>(
            PsxMeshFile.Parse(bankPath!, bakeColourPulses: false));
        var skyMeshIndex = Array.IndexOf(bank.MeshNameHashes, 0xAD50109Bu);
        Assert.Equal(5, skyMeshIndex);
        var skyMesh = bank.Meshes[skyMeshIndex];
        Assert.All(skyMesh.Faces, static face =>
        {
            Assert.True(face.IsGouraud);
            Assert.True(face.IsTextured);
        });

        var palette = Assert.IsType<Vector4[]>(bank.GouraudPalette);
        var nativeCorners = skyMesh.Faces
            .SelectMany(face => Enumerable.Range(0, face.IsQuad ? 4 : 3)
                .Select(slot => (
                    Y: skyMesh.Vertices[(int)GetFaceVertexIndex(face, slot)].Y,
                    Color: palette[GetFacePaletteIndex(face, slot)])))
            .ToArray();
        var nativeTopY = nativeCorners.Min(static corner => corner.Y);
        var nativeHorizonY = nativeCorners.Max(static corner => corner.Y);
        var nativeTop = nativeCorners.Where(corner => NearlyEqual(corner.Y, nativeTopY)).ToArray();
        var nativeHorizon = nativeCorners.Where(corner => NearlyEqual(corner.Y, nativeHorizonY)).ToArray();

        // The serialized top ring is the dark blue RGBs[0] entry. The bottom
        // ring is more than 0.3 display-luminance brighter: this is authored
        // data, before any converter or viewer transform can affect it.
        var authoredTopColor = new Vector4(35f / 255f, 35f / 255f, 66f / 255f, 1f);
        Assert.All(nativeTop, corner => AssertVectorClose(authoredTopColor, corner.Color));
        Assert.True(
            nativeHorizon.Average(static corner => Luminance(corner.Color))
            > nativeTop.Average(static corner => Luminance(corner.Color)) + 0.3f);

        var document = ParseDocument(levelPath!);
        var emittedSky = Assert.Single(document.Meshes,
            static mesh => mesh.Name == "sky__mesh_00000005");
        Assert.Equal(112, emittedSky.Primitives.Sum(static primitive => primitive.TriangleCount));
        var emittedVertices = emittedSky.Primitives
            .SelectMany(static primitive => primitive.Vertices)
            .ToArray();
        Assert.All(emittedVertices, static vertex => Assert.True(vertex.PsxPacketColor.HasValue));

        var emittedTopY = emittedVertices.Max(static vertex => vertex.Position.Y);
        var emittedHorizonY = emittedVertices.Min(static vertex => vertex.Position.Y);
        Assert.True(NearlyEqual(emittedTopY, -nativeTopY));
        Assert.True(NearlyEqual(emittedHorizonY, -nativeHorizonY));
        var emittedTop = emittedVertices.Where(vertex => NearlyEqual(vertex.Position.Y, emittedTopY)).ToArray();
        var emittedHorizon = emittedVertices
            .Where(vertex => NearlyEqual(vertex.Position.Y, emittedHorizonY))
            .ToArray();
        Assert.All(emittedTop,
            vertex => AssertVectorClose(authoredTopColor, vertex.PsxPacketColor!.Value));
        Assert.True(
            emittedHorizon.Average(static vertex => Luminance(vertex.PsxPacketColor!.Value))
            > emittedTop.Average(static vertex => Luminance(vertex.PsxPacketColor!.Value)) + 0.3f);

        var skyMetadata = emittedSky.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxSkyRenderMetadata>()
            .ToArray();
        Assert.NotEmpty(skyMetadata);
        Assert.All(skyMetadata, static metadata => Assert.Equal(0xAEC0DDu, metadata.SkyColor));
    }

    [CorpusFact]
    public void Thps4_Trg_ParsesWithFullNodePopulation()
    {
        var path = paths.FindSampleFile(Thps4Build, "a1col_t.trg");
        Assert.SkipWhen(path == null, "THPS4 PS1 sample not available");

        var trg = TrgFile.Parse(path!);

        // v2.0, same node grammar as THPS1/THPS2 — RAILDEF-dominated with a
        // real POWERUP population (57), so the pickup layer has data to place.
        Assert.Equal(2, trg.VersionMajor);
        Assert.Equal(0, trg.VersionMinor);
        Assert.Equal(1_242, trg.NodeCount);
        Assert.Equal(57, trg.Nodes.Count(static node => node.Type == "POWERUP"));
    }

    [CorpusTheory]
    // Full-build .psx sweeps: every mesh-bearing file converts; the only
    // non-conversions are the texture-only classes (_l libraries, bits.psx),
    // exactly as in every other THPS build. Counts measured 2026-08-04.
    [InlineData(Thps3Build, 159, 143)]
    [InlineData(Thps4Build, 149, 134)]
    public void LatePort_PsxSweep_ConvertsEverythingWithMeshData(
        string buildName,
        int expectedPsxFiles,
        int expectedConverted)
    {
        var files = paths.FindSampleFiles(buildName, "*.psx")
            .Where(static file => file.Replace('\\', '/').Contains("/CD/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.SkipWhen(files.Length == 0, $"{buildName} samples not available");

        Assert.Equal(expectedPsxFiles, files.Length);
        var converted = 0;
        var crashes = new List<string>();
        var noMeshData = new List<string>();
        foreach (var file in files)
        {
            try
            {
                ParseDocument(file);
                converted++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No mesh data"))
            {
                // The texture-only classes: LEVEL _l libraries and bits.psx.
                // (Character _l libraries in these builds DO carry meshes and
                // convert, so the split is asserted by name below, not by
                // suffix heuristics.)
                noMeshData.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
            }
            catch (Exception ex)
            {
                crashes.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // No parse crashes ever — an unknown revision would surface here.
        Assert.Empty(crashes);
        Assert.Equal(expectedConverted, converted);
        // Every non-conversion is a level texture library or bits.psx.
        Assert.All(noMeshData, static name =>
            Assert.True(
                name.EndsWith("_l", StringComparison.Ordinal)
                || name.EndsWith("_l2", StringComparison.Ordinal)
                || name == "bits",
                $"unexpected no-mesh file: {name}"));
    }

    private static ModelDocument ParseDocument(string path)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path),
            FileName = Path.GetFileName(path),
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = true
        });
    }

    private static uint GetFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static byte GetFacePaletteIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.R,
            1 => face.G,
            2 => face.B,
            3 => face.Mode,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static float Luminance(Vector4 color)
    {
        return color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
    }

    private static bool NearlyEqual(float left, float right)
    {
        return MathF.Abs(left - right) <= 0.001f;
    }

    private static void AssertVectorClose(Vector4 expected, Vector4 actual)
    {
        Assert.InRange(Vector4.Distance(expected, actual), 0f, 1e-6f);
    }
}
