using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the N64 model bundle path through the shared mesh pipeline
///     (2026-08-06): a bundle read straight out of a .z64 produces a document
///     with the shell's skeleton and a metadata record describing its render
///     bank. Geometry is deliberately absent until the group2 vertex codec is
///     decoded — the metadata says so explicitly rather than the document
///     merely coming out empty.
/// </summary>
public sealed class N64ModelParseTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    private ModelDocument ParseBundle(string slot, out IArchiveFileSystem fs)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");
        fs = ArchiveFileSystem.TryOpen(romPath!)!;
        var backend = ArchiveAssetBackend.TryOpen(romPath!)!;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "models_000",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    [Fact]
    public void Parse_ProducesTheShellSkeletonAndRenderBankMetadata()
    {
        var document = ParseBundle("000", out var fs);
        using var _ = fs;

        Assert.Equal(ModelSourceKind.N64Model, document.SourceKind);

        var skeleton = Assert.Single(document.Skeletons);
        Assert.Equal(19, skeleton.Bones.Count);

        var metadata = Assert.Single(document.NativeMetadata.OfType<N64ModelRenderMetadata>());
        Assert.Equal(22u, metadata.RenderBankId);
        Assert.Equal(19, metadata.ObjectCount);
        Assert.True(metadata.RenderBankBytes > 0, "the render bank record should have loaded");
        Assert.True(metadata.GeometryDecoded);

        // Geometry from group2/022.bin: 570 triangles, split into one node per
        // G_MTX index so the character's parts stay separable.
        Assert.Equal(570, document.TriangleCount);
        Assert.NotEmpty(document.Meshes);
        var indices = document.Meshes
            .SelectMany(static m => m.Primitives)
            .Sum(static p => p.Indices.Length);
        Assert.Equal(570 * 3, indices);
        Assert.Equal(document.Meshes.Count, document.Nodes.Count(static n => n.MeshIndex.HasValue));
    }

    /// <summary>
    ///     End-to-end: a bundle read from the ROM exports a real GLB carrying
    ///     the decoded render-bank geometry.
    /// </summary>
    [Fact]
    public void Document_ExportsAValidGlbWithGeometry()
    {
        var document = ParseBundle("000", out var fs);
        using var _ = fs;

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);

        Assert.NotNull(glb);
        Assert.Equal(570, triangles);
        // glTF binary container magic.
        Assert.Equal((byte)'g', glb![0]);
        Assert.Equal((byte)'l', glb[1]);
        Assert.Equal((byte)'T', glb[2]);
        Assert.Equal((byte)'F', glb[3]);
        Assert.True(glb.Length > 512, $"expected a populated GLB, got {glb.Length} bytes");
    }

    [Fact]
    public void Parse_RejectsAnEmptyBundleSlotWithAClearMessage()
    {
        // models/049 is a 24-byte authored-empty shell.
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var document = ParseBundle("049", out var fs);
            fs.Dispose();
            return document;
        });

        Assert.Contains("N64 model shell", error.Message, StringComparison.Ordinal);
    }
}
