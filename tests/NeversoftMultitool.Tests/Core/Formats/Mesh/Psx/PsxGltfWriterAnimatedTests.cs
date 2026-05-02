using System.Text.Json;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public class PsxGltfWriterAnimatedTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";

    [Fact]
    public void WriteAnimated_CarnagePsx_EmitsGlbWithExpectedStructure()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        Assert.NotNull(animFile);

        // Decode anim 0 — it's at the END of the pool per the diagnostic, so
        // exercises the "non-sorted entries" decoding path.
        var entry = animFile.Entries[0];
        var animation = PsxAnimDecoder.Decode(
            animFile.Pool.Span[entry.PoolOffset..], psxFile.Objects.Count, entry.FrameCount);

        var (model, triangles) = PsxGltfWriter.BuildAnimated(
            psxFile,
            [("anim_0", animation)],
            textureProvider: null,
            pshFile: null);

        Assert.True(triangles > 0, "carnage should produce triangles");
        Assert.Single(model.LogicalAnimations);
        Assert.Equal("anim_0", model.LogicalAnimations[0].Name);
        Assert.Single(model.LogicalSkins);
        Assert.Equal(psxFile.Objects.Count, model.LogicalSkins[0].JointsCount);
    }

    [Fact]
    public void WriteAnimated_ThrowsForEmptyAnimationList()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var psxFile = PsxMeshFile.Parse(File.ReadAllBytes(path!));
        Assert.NotNull(psxFile);

        Assert.Throws<ArgumentException>(
            () => PsxGltfWriter.BuildAnimated(psxFile, []));
    }

    [Fact]
    public void WriteAnimated_CarnagePsx_WritesValidGlbFile()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "carnage.psx");
        Assert.SkipWhen(path == null, "carnage.psx not found in sample builds");

        var data = File.ReadAllBytes(path!);
        var psxFile = PsxMeshFile.Parse(data);
        Assert.NotNull(psxFile);
        var meshBlockEnd = PsxMeshFile.GetMeshBlockEnd(data);
        var animFile = PsxAnimFile.Parse(data, psxFile.Objects.Count, meshBlockEnd);
        Assert.NotNull(animFile);

        var entry = animFile.Entries[1]; // 30 frames, well-behaved
        var animation = PsxAnimDecoder.Decode(
            animFile.Pool.Span[entry.PoolOffset..], psxFile.Objects.Count, entry.FrameCount);

        var outDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "carnage_anim1.glb");

        try
        {
            var triangles = PsxGltfWriter.WriteAnimated(
                psxFile, [("anim_1", animation)], outPath);

            Assert.True(triangles > 0);
            Assert.True(File.Exists(outPath));

            // Sanity: GLB header magic 'glTF' + JSON parses
            using var s = File.OpenRead(outPath);
            using var r = new BinaryReader(s);
            Assert.Equal(0x46546C67u, r.ReadUInt32()); // 'glTF'
            r.ReadUInt32(); // version
            r.ReadUInt32(); // total length
            var jsonLen = r.ReadInt32();
            r.ReadUInt32(); // chunk type
            var jsonBytes = r.ReadBytes(jsonLen);
            var doc = JsonDocument.Parse(jsonBytes);
            Assert.True(doc.RootElement.TryGetProperty("animations", out var anims));
            Assert.Equal(1, anims.GetArrayLength());
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
