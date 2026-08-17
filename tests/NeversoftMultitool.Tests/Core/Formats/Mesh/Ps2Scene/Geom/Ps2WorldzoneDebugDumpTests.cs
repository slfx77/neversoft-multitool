using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     The worldzone triage diagnostics are strictly additive: with
///     <c>WorldzoneDebugDirectory</c> set the conversion result is identical
///     and the rejection/material CSVs appear with their stable shapes.
/// </summary>
public sealed class Ps2WorldzoneDebugDumpTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    private static readonly string[] KnownWriterReasons =
    [
        "entry_out_of_bounds",
        "not_pak_mdl",
        "qb_resolved_empty",
        "too_few_vertices",
        "time_of_day_night_overlay",
        "geometric_quarantine",
        "redundant_blend_layer"
    ];

    [CorpusFact]
    public void ParsePs2Worldzone_ZBh_DebugDumpChangesNothingAndWritesTheTriageCsvs()
    {
        var pakPath = paths.FindSampleFile(ThawPs2Build, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath == null, "THAW PS2 z_bh.pak.ps2 sample not available");

        var debugDir = Path.Combine(
            AppContext.BaseDirectory, "TestOutput", nameof(Ps2WorldzoneDebugDumpTests));
        if (Directory.Exists(debugDir))
            Directory.Delete(debugDir, recursive: true);

        var parser = new MeshModelParser();
        var baseline = parser.Parse(CreateRequest(pakPath!, null));
        var debugged = parser.Parse(CreateRequest(pakPath!, debugDir));

        // No behavior change: identical geometry with and without diagnostics.
        Assert.Equal(CountTriangles(baseline), CountTriangles(debugged));
        Assert.Equal(baseline.Meshes.Count, debugged.Meshes.Count);
        Assert.Equal(baseline.Materials.Count, debugged.Materials.Count);
        Assert.Equal(baseline.Nodes.Count, debugged.Nodes.Count);

        var rejectionsCsv = Path.Combine(debugDir, "z_bh.rejections.csv");
        var materialsCsv = Path.Combine(debugDir, "z_bh.materials.csv");
        Assert.True(File.Exists(rejectionsCsv), "rejections.csv was not written");
        Assert.True(File.Exists(materialsCsv), "materials.csv was not written");

        var rejectionLines = File.ReadAllLines(rejectionsCsv);
        Assert.Equal(
            "mdl,stage,reason,leafIndex,vertexCount,tex0,minX,minY,minZ,maxX,maxY,maxZ",
            rejectionLines[0]);

        var materialLines = File.ReadAllLines(materialsCsv);
        Assert.Equal(
            "mdl,leafIndex,space,drawIndex,passIndex,overlapGroup,isBillboard," +
            "alpha1,alphaA,alphaB,alphaC,alphaD,alphaFix," +
            "test1,ate,atst,aref,afail," +
            "fbmskAlphaByte,alphaMode,renderLayer,renderOrderKey,groupChecksum," +
            "tex0,textureChecksum,syntheticDestAlpha,textureResolved," +
            "resolveMode,sourceLabel,entryLabel,bakeClass,vertexCount," +
            "minX,minY,minZ,maxX,maxY,maxZ",
            materialLines[0]);

        // Every emitted leaf produced a material row.
        Assert.True(materialLines.Length > 1, "materials.csv has no data rows");

        // Writer-stage rejection reasons come from a closed vocabulary so the
        // triage decision trees can key on them.
        foreach (var line in rejectionLines.Skip(1))
        {
            var fields = line.Split(',');
            if (fields[1] != "writer")
                continue;
            Assert.Contains(fields[2], KnownWriterReasons);
        }
    }

    private static MeshImportRequest CreateRequest(string pakPath, string? debugDir) => new()
    {
        Source = new FileSystemAssetSource(pakPath),
        FileName = Path.GetFileName(pakPath),
        OutputStem = "z_bh",
        SourceKind = ModelSourceKind.Ps2Worldzone,
        WorldzoneDebugDirectory = debugDir
    };

    private static int CountTriangles(ModelDocument document) =>
        document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.TriangleCount);
}
