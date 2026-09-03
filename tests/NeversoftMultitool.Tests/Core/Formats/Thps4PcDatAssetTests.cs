using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats;

/// <summary>
///     Aspyr's THPS4 PC port gives unrelated payloads the same .dat extension and
///     encodes the actual kind at the end of the stem. These gates pin the only
///     two delimiter-free DAT families that the existing WPC readers fully prove.
/// </summary>
public sealed class Thps4PcDatAssetTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    [Theory]
    [InlineData("alctex.dat", true)]
    [InlineData("Anl_ChickenTEX.DAT", true)]
    [InlineData("01234567.tex.dat", false)]
    [InlineData("alctex.bin", false)]
    [InlineData("tex.dat", false)]
    public void TextureNameGate_AdmitsOnlyDelimiterFreeThps4PcNames(string name, bool expected)
    {
        Assert.Equal(expected, Thps4PcDatTextureFile.IsCandidateFileName(name));
    }

    [Fact]
    public void TexturePayloadGate_RequiresExactEofAndDecodablePixels()
    {
        var empty = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(empty, 1);

        Assert.True(Thps4PcDatTextureFile.Parse(empty).Success);
        Assert.False(Thps4PcDatTextureFile.Parse([.. empty, 0]).Success);

        var unsupported = BuildUnsupportedTexture();
        var permissive = XbxTexFile.Parse(unsupported);
        Assert.True(permissive.Success);
        Assert.Null(Assert.Single(permissive.Textures).Pixels);
        Assert.False(Thps4PcDatTextureFile.Parse(unsupported).Success);
    }

    [Theory]
    [InlineData(32, 0, 0, 256, 64)]
    [InlineData(16, 0, 0, 128, 32)]
    [InlineData(8, 0, 1024, 64, 16)]
    [InlineData(4, 0, 64, 32, 8)]
    [InlineData(32, 1, 0, 32, 8)]
    [InlineData(32, 2, 0, 32, 8)]
    [InlineData(32, 5, 0, 64, 16)]
    public void TexturePayloadGate_AcceptsExactStoredLengthForEverySupportedLayout(
        int texelDepth,
        int dxtVersion,
        int paletteSize,
        int levelZeroSize,
        int levelOneSize)
    {
        var data = BuildSupportedTexture(
            8,
            8,
            (uint)texelDepth,
            (uint)dxtVersion,
            (uint)paletteSize,
            levelZeroSize,
            levelOneSize);

        var result = Thps4PcDatTextureFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(8 * 8 * 4, Assert.Single(result.Textures).Pixels!.Length);
    }

    [Fact]
    public void TexturePayloadGate_RejectsZeroByteLowerMip()
    {
        var data = BuildSupportedTexture(8, 8, 32, 1, 0, 32, 0);

        var result = Thps4PcDatTextureFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains("mip 1", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected exactly 8", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TexturePayloadGate_RejectsOversizedDxt1Mip()
    {
        var data = BuildSupportedTexture(8, 8, 32, 1, 0, 40, 8);

        var result = Thps4PcDatTextureFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains("mip 0", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected exactly 32", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextureProbe_UsesTheFullDatPayloadGate()
    {
        var directory = CreateTempDirectory();
        var valid = Path.Combine(directory, "alctex.dat");
        var trailing = Path.Combine(directory, "badtex.dat");
        var empty = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(empty, 1);
        File.WriteAllBytes(valid, empty);
        File.WriteAllBytes(trailing, [.. empty, 0]);

        try
        {
            var supported = FormatProbe.ProbeTexture(valid);
            Assert.Equal(FormatProbe.FormatSupport.Supported, supported.Support);
            Assert.Contains("THPS4 PC TEX", supported.FormatName);

            var rejected = FormatProbe.ProbeTexture(trailing);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, rejected.Support);
            Assert.Contains("trailing", rejected.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CollisionRoute_RequiresACompleteVersion8Payload()
    {
        var complete = BuildEmptyObjectCollision(version: 8);
        var named = MeshTypeDetector.DetectFromBytes("Anl_Chickencol.dat", complete, complete.Length);

        Assert.Equal(MeshFileKind.Collision, named.Kind);
        Assert.True(named.IsSupported);
        Assert.Equal("THPS4 PC COL Collision (v8)", named.DisplayFormat);
        Assert.Equal("Anl_Chicken", MeshTypeDetector.GetStem("Anl_Chickencol.dat"));

        var prefixOnly = MeshTypeDetector.DetectFromBytes("Anl_Chickencol.dat", complete[..32], complete.Length);
        Assert.False(prefixOnly.IsSupported);
        Assert.Contains("complete", prefixOnly.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);

        var wrongVersion = MeshTypeDetector.DetectFromBytes(
            "Anl_Chickencol.dat", BuildEmptyObjectCollision(version: 9), complete.Length);
        Assert.False(wrongVersion.IsSupported);
        Assert.Contains("version 8", wrongVersion.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(MeshFileKind.None, MeshTypeDetector.DetectByName("Anl_Chickenimg.dat").Kind);
    }

    [CorpusFact]
    public void Corpus_All601TextureDatFilesDecodeAll8332TexturesAnd38093MipsExactly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(BuildName, "*tex.dat")
            .Where(file => Thps4PcDatTextureFile.IsCandidateFileName(Path.GetFileName(file)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(601, files.Length);

        long totalBytes = 0;
        var totalTextures = 0;
        var totalMipLevels = 0;
        var emptyDictionaries = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            totalBytes += new FileInfo(file).Length;
            var probe = FormatProbe.ProbeTexture(file);
            if (probe.Support != FormatProbe.FormatSupport.Supported)
            {
                failures.Add($"{Path.GetFileName(file)} probe: {probe.UnsupportedReason}");
                continue;
            }

            var parsed = Thps4PcDatTextureFile.Parse(file);
            if (!parsed.Success)
            {
                failures.Add($"{Path.GetFileName(file)} parse: {parsed.ErrorMessage}");
                continue;
            }

            totalTextures += parsed.Textures.Count;
            totalMipLevels += CountTextureMipLevels(File.ReadAllBytes(file));
            if (parsed.Textures.Count == 0)
                emptyDictionaries++;

            for (var i = 0; i < parsed.Textures.Count; i++)
            {
                var texture = parsed.Textures[i];
                if (texture.Pixels == null ||
                    texture.Pixels.LongLength != (long)texture.Width * texture.Height * 4)
                {
                    failures.Add($"{Path.GetFileName(file)} texture {i}: invalid RGBA output");
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(58_941_068, totalBytes);
        Assert.Equal(8_332, totalTextures);
        Assert.Equal(38_093, totalMipLevels);
        Assert.Equal(1, emptyDictionaries);
    }

    [CorpusFact]
    public void Corpus_All601CollisionDatFilesParseAndRouteExactly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(BuildName, "*col.dat")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(601, files.Length);

        long totalBytes = 0;
        long totalObjects = 0;
        long totalVertices = 0;
        long totalFaces = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            totalBytes += new FileInfo(file).Length;
            var route = MeshTypeDetector.Detect(file);
            if (!route.IsSupported || route.Kind != MeshFileKind.Collision)
            {
                failures.Add($"{Path.GetFileName(file)} route: {route.UnsupportedReason}");
                continue;
            }

            try
            {
                var scene = ColFile.Parse(file);
                if (scene.Version != 8)
                {
                    failures.Add($"{Path.GetFileName(file)}: version {scene.Version}");
                    continue;
                }

                totalObjects += scene.Objects.LongLength;
                totalVertices += scene.TotalVertices;
                totalFaces += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(24_240_836, totalBytes);
        Assert.Equal(11_701, totalObjects);
        Assert.Equal(646_877, totalVertices);
        Assert.Equal(669_796, totalFaces);
    }

    [CorpusFact]
    public void Cli_RepresentativeTextureAndCollisionDatFilesExportWithCleanStems()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var texture = paths.FindSampleFile(BuildName, "Anl_Chickentex.dat");
        var collision = paths.FindSampleFile(BuildName, "Anl_Chickencol.dat");
        Assert.SkipWhen(texture == null || collision == null, "Representative THPS4 PC DAT assets not found");
        var directory = CreateTempDirectory();
        var textureOutput = Path.Combine(directory, "textures");
        var collisionOutput = Path.Combine(directory, "collision");

        try
        {
            Assert.Equal(0, XbxTexCommand.Execute(
                texture, textureOutput, verbose: true, TestContext.Current.CancellationToken));
            var pngs = Directory.EnumerateFiles(textureOutput, "*.png", SearchOption.AllDirectories).ToArray();
            Assert.Equal(3, pngs.Length);
            Assert.All(pngs, png => Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                File.ReadAllBytes(png)[..8]));
            Assert.Contains(pngs, png => Path.GetDirectoryName(png)!.EndsWith(
                "Anl_Chicken", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(0, ColCommand.Execute(
                collision,
                collisionOutput,
                verbose: true,
                MeshOutputFormat.Glb,
                blenderHelperPath: null,
                TestContext.Current.CancellationToken));
            var glb = Assert.Single(Directory.EnumerateFiles(
                collisionOutput, "*.glb", SearchOption.AllDirectories));
            Assert.Equal("Anl_Chicken.glb", Path.GetFileName(glb), ignoreCase: true);
            Assert.Equal(168, ModelRoot.Load(glb).LogicalMeshes.Sum(mesh =>
                mesh.Primitives.Sum(primitive => primitive.GetTriangleIndices().Count())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] BuildUnsupportedTexture()
    {
        var data = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 99);
        return data;
    }

    private static byte[] BuildSupportedTexture(
        uint width,
        uint height,
        uint texelDepth,
        uint dxtVersion,
        uint paletteSize,
        params int[] mipSizes)
    {
        var length = checked(8 + 32 + (int)paletteSize +
                             mipSizes.Sum(static size => checked(4 + size)));
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), width);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), height);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)mipSizes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), texelDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), dxtVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), paletteSize);

        var offset = checked(40 + (int)paletteSize);
        foreach (var mipSize in mipSizes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)mipSize);
            offset = checked(offset + 4 + mipSize);
        }

        return data;
    }

    private static int CountTextureMipLevels(ReadOnlySpan<byte> data)
    {
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var offset = 8;
        var total = 0;
        for (var textureIndex = 0u; textureIndex < textureCount; textureIndex++)
        {
            var levels = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
            var paletteSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 28)..]);
            total = checked(total + (int)levels);
            offset = checked(offset + 32 + (int)paletteSize);
            for (var mip = 0u; mip < levels; mip++)
            {
                var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                offset = checked(offset + 4 + (int)dataSize);
            }
        }

        Assert.Equal(data.Length, offset);
        return total;
    }

    private static byte[] BuildEmptyObjectCollision(int version)
    {
        const int bspSizeOffset = 96;
        const int bspLeafOffset = bspSizeOffset + sizeof(uint);
        var data = new byte[bspLeafOffset + 20];
        BinaryPrimitives.WriteInt32LittleEndian(data, version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspSizeOffset), 20);
        data[bspLeafOffset] = byte.MaxValue;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspLeafOffset + 8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspLeafOffset + 12), uint.MaxValue);
        return data;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nmt-thps4-pc-dat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
