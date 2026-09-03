using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Psp;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psp;

public sealed class PspLevelFileTests(TestPaths paths)
{
    private const string RemixBuild =
        "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";
    private const string Project8FinalBuild =
        "Tony Hawk's Project 8 (2006-10-14, PSP - Final)";
    private const string Project8Rev1Build =
        "Tony Hawk's Project 8 (2007-2-16, PSP - Rev1)";
    private const string Thug2WindowsBuild =
        "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    [Fact]
    public void Parse_SyntheticFixedAndFloatStrips_DecodesProvenGeometryAndT4Pixels()
    {
        var data = CreateSyntheticLevel();

        var parsed = PspLevelFile.Parse(data);

        Assert.Equal(data.Length, parsed.Summary.FileBytes);
        Assert.Equal(2, parsed.Summary.PrimitiveCount);
        Assert.Equal(6, parsed.Summary.VertexCount);
        Assert.Equal(2, parsed.Summary.TheoreticalTriangleCount);
        Assert.Equal(36, parsed.Summary.FixedVertexBytes);
        Assert.Equal(60, parsed.Summary.FloatVertexBytes);
        Assert.Equal(1, parsed.Summary.CommandListCount);
        Assert.Equal(1, parsed.Summary.TextureCount);

        var sector = Assert.Single(parsed.Scene.Sectors);
        var mesh = Assert.Single(sector.Meshes);
        Assert.True(mesh.IsPreTriangulated);
        Assert.Equal([0, 1, 2, 3, 4, 5], mesh.FaceIndices);
        Assert.Equal(new Vector3(25, 10, 5), mesh.Vertices[0].Position);
        Assert.Equal(new Vector3(26, 10, 5), mesh.Vertices[1].Position);
        Assert.Equal(new Vector3(25, 11, 5), mesh.Vertices[2].Position);
        Assert.Equal(new Vector3(25, 10, 5), mesh.Vertices[3].Position);
        Assert.Equal(new Vector2(0.75f, 0.5f), mesh.Vertices[0].TexCoord);
        Assert.Equal(new Vector4(3 / 15f, 2 / 15f, 1 / 15f, 1), mesh.Vertices[0].Color);
        Assert.Equal(new Vector4(10 / 255f, 20 / 255f, 30 / 255f, 40 / 255f),
            mesh.Vertices[3].Color);

        var texture = Assert.Single(parsed.Textures);
        Assert.Equal((32, 8, 4, 1),
            (texture.Width, texture.Height, texture.PixelFormat, texture.MipCount));
        Assert.NotNull(texture.Rgba);
        Assert.Equal([0, 1, 2, 255, 1, 2, 3, 255], texture.Rgba![..8]);
        Assert.Equal(0x89, parsed.ResolveTexture(texture.Checksum)![0]); // PNG signature.
    }

    [Fact]
    public void DetectorAndModelParser_UseTheSharedTexturedGlbRoute()
    {
        var data = CreateSyntheticLevel();
        var route = MeshTypeDetector.DetectFromBytes("synthetic.psp_level", data, data.Length);
        Assert.True(route.IsSupported, route.UnsupportedReason);
        Assert.Equal(MeshFileKind.XbxScene, route.Kind);
        Assert.Equal("PSP Level (static world; objects omitted)", route.DisplayFormat);
        Assert.True(MeshTypeDetector.ReportsPartialSupport(route));

        var path = Path.Combine(Path.GetTempPath(), $"nmt-{Guid.NewGuid():N}.psp_level");
        try
        {
            File.WriteAllBytes(path, data);
            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = new FileSystemAssetSource(path),
                FileName = Path.GetFileName(path),
                OutputStem = "synthetic",
                SourceKind = ModelSourceKind.XbxScene
            });

            Assert.Equal(2, document.TriangleCount);
            Assert.Single(document.Textures);
            Assert.Equal(ModelAlphaMode.Blend, Assert.Single(document.Materials).AlphaMode);
            var (glb, triangles) = new GltfModelExporter().BuildGlbBytes(document);
            Assert.Equal(2, triangles);
            Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(glb, 0, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Probe_RejectsTrailingAndStructurallyCorruptFiles()
    {
        var valid = CreateSyntheticLevel();
        Assert.True(PspLevelFile.TryInspect(valid, out _, out _));

        var trailing = valid.Append((byte)0).ToArray();
        Assert.False(PspLevelFile.TryInspect(trailing, out _, out var trailingError));
        Assert.Contains("consume", trailingError);

        var badSentinel = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSentinel.AsSpan(0x3C), 0);
        Assert.False(PspLevelFile.TryInspect(badSentinel, out _, out var sentinelError));
        Assert.Contains("sentinel", sentinelError);

        var badReturn = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badReturn.AsSpan(GetVertexReturnOffset(badReturn)), 0);
        Assert.False(PspLevelFile.TryInspect(badReturn, out _, out var returnError));
        Assert.Contains("RET", returnError);

        var badBox = (byte[])valid.Clone();
        var stream = GetPrimitiveStreamOffset(badBox);
        BinaryPrimitives.WriteUInt16LittleEndian(badBox.AsSpan(stream), (3 << 11) | 1);
        Assert.False(PspLevelFile.TryInspect(badBox, out _, out var boxError));
        Assert.Contains("box reference", boxError);

        var badCommand = (byte[])valid.Clone();
        badCommand[GetTextureCommandOffset(badCommand) + 3] = 0x99;
        Assert.False(PspLevelFile.TryInspect(badCommand, out _, out var commandError));
        Assert.Contains("unsupported command", commandError);
    }

    [Fact]
    public void Probe_RequiresTheCompleteNamedPayload()
    {
        var data = CreateSyntheticLevel();
        var route = MeshTypeDetector.DetectFromBytes("partial.psp_level", data[..^1], data.Length);

        Assert.False(route.IsSupported);
        Assert.Equal("PSP Level", route.DisplayFormat);
        Assert.Contains("complete payload", route.UnsupportedReason);
    }

    [CorpusFact]
    public void Corpus_All668FilesPassExactStaticAndTextureAccounting()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var groups = new[]
        {
            (RemixBuild, 80, 80_327_774L),
            (Project8FinalBuild, 294, 61_488_910L),
            (Project8Rev1Build, 294, 61_488_910L)
        };

        long primitiveCount = 0;
        long vertexCount = 0;
        long triangleCount = 0;
        long fixedBytes = 0;
        long floatBytes = 0;
        foreach (var (build, expectedCount, expectedBytes) in groups)
        {
            var files = FindLevelFiles(build);
            Assert.Equal(expectedCount, files.Length);
            Assert.Equal(expectedBytes, files.Sum(static file => new FileInfo(file).Length));
            foreach (var file in files)
            {
                var data = File.ReadAllBytes(file);
                Assert.True(
                    PspLevelFile.TryInspect(data, out var summary, out var error),
                    $"{file}: {error}");
                Assert.Equal(data.LongLength, summary.FileBytes);
                primitiveCount += summary.PrimitiveCount;
                vertexCount += summary.VertexCount;
                triangleCount += summary.TheoreticalTriangleCount;
                fixedBytes += summary.FixedVertexBytes;
                floatBytes += summary.FloatVertexBytes;
            }
        }

        Assert.Equal(1_785_387, primitiveCount);
        Assert.Equal(8_646_324, vertexCount);
        Assert.Equal(5_075_550, triangleCount);
        Assert.Equal(100_076_868, fixedBytes);
        Assert.Equal(6_131_700, floatBytes);
    }

    [CorpusFact]
    public void Project8Revisions_Have293IdenticalPairsAndOneTextureOnlyDifference()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var final = FindLevelFiles(Project8FinalBuild)
            .ToDictionary(RelativeLevelPath, StringComparer.OrdinalIgnoreCase);
        var rev1 = FindLevelFiles(Project8Rev1Build)
            .ToDictionary(RelativeLevelPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(294, final.Count);
        Assert.Equal(final.Keys.Order(), rev1.Keys.Order());

        var changed = final.Keys
            .Where(key => !SHA256.HashData(File.ReadAllBytes(final[key]))
                .AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(rev1[key]))))
            .ToArray();
        var changedPath = Assert.Single(changed);
        Assert.EndsWith("z_dj/z_dj.psp_level", changedPath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

        var a = File.ReadAllBytes(final[changedPath]);
        var b = File.ReadAllBytes(rev1[changedPath]);
        Assert.Equal(467_820, a.Length);
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.AsSpan(0, 0x39E10).SequenceEqual(b.AsSpan(0, 0x39E10)));
        Assert.True(a.AsSpan(0x3A202).SequenceEqual(b.AsSpan(0x3A202)));
        Assert.Equal(709, a.Zip(b).Count(static pair => pair.First != pair.Second));
    }

    [CorpusFact]
    public void RemixTr_StaticBoundsAndSkyUvsAgreeWithTheWindowsSibling()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var pspRoot = Path.Combine(paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "levels");
        var windowsRoot = Path.Combine(paths.SampleBuildsDir!, Thug2WindowsBuild,
            "Installed", "Tony Hawk's Underground 2", "Game", "Data", "pre", "TRscn", "levels");

        var pspMain = PspLevelFile.Parse(File.ReadAllBytes(Path.Combine(pspRoot, "tr", "tr.psp_level")));
        var windowsMain = XbxSceneFile.Parse(File.ReadAllBytes(Path.Combine(windowsRoot, "TR", "TR.scn.xbx")));
        var pspBounds = Bounds(pspMain.Scene);
        var windowsBounds = Bounds(windowsMain);
        AssertVectorNear(windowsBounds.Min, pspBounds.Min, 0.25f);
        AssertVectorNear(windowsBounds.Max, pspBounds.Max, 0.25f);
        AssertVectorNear(new Vector3(-3904.875f, -1632.75f, -12854.4375f), windowsBounds.Min, 0.001f);
        AssertVectorNear(new Vector3(16732.688f, 2129.0625f, 10043.375f), windowsBounds.Max, 0.001f);

        var pspSky = PspLevelFile.Parse(File.ReadAllBytes(
            Path.Combine(pspRoot, "tr_sky", "tr_sky.psp_level")));
        var windowsSky = XbxSceneFile.Parse(File.ReadAllBytes(
            Path.Combine(windowsRoot, "TR_sky", "TR_sky.scn.xbx")));
        var portableVertices = Vertices(pspSky.Scene);
        var siblingVertices = Vertices(windowsSky);
        Assert.NotEmpty(portableVertices);
        // The PSP sky was re-authored at a different radius, so position is
        // not an identity join. Its quantized UV 16392/32768, however, maps to
        // the same authored half-atlas boundary as the Windows float UV.
        var pspUvError = portableVertices
            .SelectMany(static vertex => new[] { vertex.TexCoord.X, vertex.TexCoord.Y })
            .Min(value => MathF.Abs(value - 0.50024414f));
        var windowsUvError = siblingVertices
            .SelectMany(static vertex => new[] { vertex.TexCoord.X, vertex.TexCoord.Y })
            .Min(value => MathF.Abs(value - 0.50025f));
        Assert.InRange(pspUvError, 0, 0.000001f);
        Assert.InRange(windowsUvError, 0, 0.000001f);
    }

    [CorpusFact]
    public void RemixTrSky_TexturesHavePinnedPixelsAndDownsampledSiblingColour()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var pspPath = Path.Combine(paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "levels", "tr_sky", "tr_sky.psp_level");
        var xboxPath = Path.Combine(paths.SampleBuildsDir!, Thug2WindowsBuild,
            "Installed", "Tony Hawk's Underground 2", "Game", "Data", "pre", "TRscn",
            "levels", "TR_sky", "TR_sky.tex.xbx");
        var parsed = PspLevelFile.Parse(File.ReadAllBytes(pspPath));
        Assert.Equal(4, parsed.Textures.Count);
        Assert.All(parsed.Textures, static texture => Assert.Equal((64, 64, 5),
            (texture.Width, texture.Height, texture.PixelFormat)));

        var first = parsed.Textures[0];
        Assert.NotNull(first.Rgba);
        Assert.Equal("141FBE4D4849CD3E42B3C6754DB771877C1483219A9C4D04AFEA725A9DACC9D5",
            Convert.ToHexString(SHA256.HashData(first.Rgba!)));
        Assert.Equal("31964009B545C7E9B2C4CC5B501E8BDB317AFB0E1E84C69F4B4217110EBFB8E7",
            Convert.ToHexString(SHA256.HashData(parsed.ResolveTexture(first.Checksum)!)));

        var sibling = XbxTexFile.Parse(xboxPath);
        Assert.True(sibling.Success, sibling.ErrorMessage);
        Assert.Equal(4, sibling.Textures.Count);
        for (var i = 0; i < 4; i++)
        {
            var psp = parsed.Textures[i];
            var xbox = sibling.Textures[i];
            Assert.Equal((psp.Width * 2, psp.Height * 2), (xbox.Width, xbox.Height));
            Assert.NotNull(psp.Rgba);
            Assert.NotNull(xbox.Pixels);
            var error = DownsampledRgbMeanAbsoluteError(psp.Rgba!, psp.Width, psp.Height,
                xbox.Pixels!, xbox.Width);
            Assert.InRange(error, 0, 6.0);
        }
    }

    [CorpusFact]
    public void RemixTrSky_ExportsARealTexturedCameraLockedGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "levels", "tr_sky", "tr_sky.psp_level");
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path),
            FileName = Path.GetFileName(path),
            OutputStem = "tr_sky",
            SourceKind = ModelSourceKind.XbxScene
        });

        Assert.True(document.TriangleCount > 0);
        Assert.Equal(4, document.Textures.Count);
        Assert.All(document.Meshes, static mesh => Assert.StartsWith("sky__", mesh.Name));
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Contains(primitive.NativeMetadata,
                metadata => metadata is PsxSkyRenderMetadata));
        var (glb, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.Equal(document.TriangleCount, triangles);
        Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(glb, 0, 4));
        Assert.True(glb.Length > 20_000);
    }

    [CorpusFact]
    public void Project8FinalAndRev1_WorldAndStandaloneSkyExportThroughTheSharedRoute()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        foreach (var build in new[] { Project8FinalBuild, Project8Rev1Build })
        {
            var worldPath = Path.Combine(paths.SampleBuildsDir!, build,
                "PSP_GAME", "USRDIR", "datap", "worlds", "worldzones", "z_dj",
                "z_dj.psp_level");
            // z_dj is the one level container the two revisions do not share
            // byte-for-byte (709 bytes inside a single 1,010-byte window, pinned
            // by Project8Revisions_Have293IdenticalPairsAndOneTextureOnlyDifference),
            // yet both still decode to the same geometry.
            var world = ParseDocument(worldPath, "z_dj");
            Assert.Equal(7_349, world.TriangleCount);
            Assert.NotEmpty(world.Textures);
            var (worldGlb, worldTriangles) = new GltfModelExporter().BuildGlbBytes(world);
            Assert.Equal(7_349, worldTriangles);
            Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(worldGlb, 0, 4));
        }

        var skyPath = Path.Combine(paths.SampleBuildsDir!, Project8FinalBuild,
            "PSP_GAME", "USRDIR", "datap", "skies", "default_sky",
            "default_sky.psp_level");
        var sky = ParseDocument(skyPath, "default_sky");
        Assert.Equal(96, sky.TriangleCount);
        Assert.NotEmpty(sky.Textures);
        Assert.All(sky.Meshes, static mesh => Assert.StartsWith("sky__", mesh.Name));
        Assert.All(sky.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Contains(primitive.NativeMetadata,
                metadata => metadata is PsxSkyRenderMetadata));
        var (skyGlb, skyTriangles) = new GltfModelExporter().BuildGlbBytes(sky);
        Assert.Equal(96, skyTriangles);
        Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(skyGlb, 0, 4));
    }

    private string[] FindLevelFiles(string build)
    {
        var root = Path.Combine(paths.SampleBuildsDir!, build);
        Assert.True(Directory.Exists(root), $"PSP corpus build missing: {build}");
        return Directory.EnumerateFiles(root, "*.psp_level", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string RelativeLevelPath(string path)
    {
        var datap = path.IndexOf($"{Path.DirectorySeparatorChar}datap{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
        Assert.True(datap >= 0, path);
        return path[(datap + 7)..];
    }

    private static ModelDocument ParseDocument(string path, string outputStem)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path),
            FileName = Path.GetFileName(path),
            OutputStem = outputStem,
            SourceKind = ModelSourceKind.XbxScene
        });
    }

    private static (Vector3 Min, Vector3 Max) Bounds(ParsedXbxScene scene)
    {
        var vertices = Vertices(scene);
        return (
            new Vector3(vertices.Min(static vertex => vertex.Position.X),
                vertices.Min(static vertex => vertex.Position.Y),
                vertices.Min(static vertex => vertex.Position.Z)),
            new Vector3(vertices.Max(static vertex => vertex.Position.X),
                vertices.Max(static vertex => vertex.Position.Y),
                vertices.Max(static vertex => vertex.Position.Z)));
    }

    private static XbxVertex[] Vertices(ParsedXbxScene scene)
    {
        return scene.Sectors
            .SelectMany(static sector => sector.Meshes)
            .SelectMany(static mesh => mesh.Vertices)
            .ToArray();
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, tolerance);
    }

    private static double DownsampledRgbMeanAbsoluteError(
        byte[] psp,
        int width,
        int height,
        byte[] xbox,
        int xboxWidth)
    {
        long error = 0;
        long components = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var average = 0;
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                average += xbox[((y * 2 + dy) * xboxWidth + x * 2 + dx) * 4 + channel];
            error += Math.Abs(psp[(y * width + x) * 4 + channel] - average / 4);
            components++;
        }

        return error / (double)components;
    }

    private static byte[] CreateSyntheticLevel()
    {
        const int textureBlobBytes = 64 + 128;
        const int boxCount = 1;
        const int staticVertexBytes = 36 + 60;
        const int commandCount = 18;
        const int primitiveShortCount = 8;
        const int firstStreamShorts = 3;
        const int firstStreamVertexBytes = 36;
        var length = 0x40 + textureBlobBytes + boxCount * 16 + staticVertexBytes + 4
                     + commandCount * 4 + boxCount * 4 + 4 + primitiveShortCount * 2;
        var data = new byte[length];
        WriteU32(data, 0x00, PspLevelFile.Version);
        WriteU32(data, 0x08, textureBlobBytes);
        WriteU32(data, 0x0C, boxCount);
        WriteU32(data, 0x10, staticVertexBytes);
        WriteU32(data, 0x14, commandCount);
        WriteU32(data, 0x24, primitiveShortCount);
        WriteU32(data, 0x28, 100);
        WriteU32(data, 0x2C, unchecked((uint)-20));
        WriteU32(data, 0x30, 40);
        WriteU32(data, 0x34, firstStreamShorts);
        WriteU32(data, 0x38, firstStreamVertexBytes);
        WriteU32(data, 0x3C, PspLevelFile.HeaderSentinel);

        var texture = 0x40;
        for (var i = 0; i < 16; i++)
        {
            data[texture + i * 4] = (byte)i;
            data[texture + i * 4 + 1] = (byte)(i + 1);
            data[texture + i * 4 + 2] = (byte)(i + 2);
            data[texture + i * 4 + 3] = 255;
        }
        for (var i = 0; i < 128; i++)
            data[texture + 64 + i] = (byte)(((i * 2 + 1) & 0xF) << 4 | (i * 2 & 0xF));

        var vertices = texture + textureBlobBytes + 16;
        WriteFixedVertex(data, vertices, 16384, 8192, 0, 0, 0);
        WriteFixedVertex(data, vertices + 12, 0, 0, 4, 0, 0);
        WriteFixedVertex(data, vertices + 24, 0, 0, 0, 0, 4);
        WriteFloatVertex(data, vertices + 36, 0, 0, 0, 0, 0);
        WriteFloatVertex(data, vertices + 56, 0, 0, 4f / 32768, 0, 0);
        WriteFloatVertex(data, vertices + 76, 0, 0, 0, 0, 4f / 32768);

        var vertexReturn = vertices + staticVertexBytes;
        WriteU32(data, vertexReturn, 0x0B000000);
        var commands = vertexReturn + 4;
        var ci = 0;
        WriteCommand(data, commands, ci++, 0xB0, 0);
        WriteCommand(data, commands, ci++, 0xC4, 2);
        WriteCommand(data, commands, ci++, 0xA0, 64);
        WriteCommand(data, commands, ci++, 0xA8, 32);
        WriteCommand(data, commands, ci++, 0xB8, 0x0305); // 32x8.
        WriteCommand(data, commands, ci++, 0xC2, 1);
        WriteCommand(data, commands, ci++, 0xC3, 4);
        WriteCommand(data, commands, ci++, 0x1D, 1);
        WriteCommand(data, commands, ci++, 0xC7, 0x0101);
        WriteCommand(data, commands, ci++, 0x48, 0x400000); // 2.0f.
        WriteCommand(data, commands, ci++, 0x49, 0x400000);
        WriteCommand(data, commands, ci++, 0x4A, 0xBE8000); // -0.25f.
        WriteCommand(data, commands, ci++, 0x4B, 0x000000);
        WriteCommand(data, commands, ci++, 0xC6, 0x107);
        WriteCommand(data, commands, ci++, 0xDB, 0x008007);
        WriteCommand(data, commands, ci++, 0x21, 1);
        WriteCommand(data, commands, ci++, 0xCB, 0);
        WriteCommand(data, commands, ci, 0x0B, 0);

        var stream = GetPrimitiveStreamOffset(data);
        WriteU16(data, stream, (3 << 11) | 2);
        WriteU16(data, stream + 2, 0);
        WriteU16(data, stream + 4, 0);
        WriteU16(data, stream + 6, 0);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(stream + 8), -1);
        WriteU16(data, stream + 10, (3 << 11) | 2);
        WriteU16(data, stream + 12, 0);
        WriteU16(data, stream + 14, 0);
        return data;
    }

    private static void WriteFixedVertex(
        byte[] data, int offset, ushort u, ushort v, short x, short y, short z)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), u);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 2), v);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 4), 0xF123);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 6), x);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 8), y);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 10), z);
    }

    private static void WriteFloatVertex(
        byte[] data, int offset, ushort u, ushort v, float x, float y, float z)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), u);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 2), v);
        data[offset + 4] = 10;
        data[offset + 5] = 20;
        data[offset + 6] = 30;
        data[offset + 7] = 40;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 8), x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 12), y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 16), z);
    }

    private static int GetVertexReturnOffset(byte[] data)
    {
        return 0x40
               + checked((int)ReadU32(data, 0x04) * 64)
               + checked((int)ReadU32(data, 0x08))
               + checked((int)ReadU32(data, 0x0C) * 16)
               + checked((int)ReadU32(data, 0x10));
    }

    private static int GetTextureCommandOffset(byte[] data) => GetVertexReturnOffset(data) + 4;

    private static int GetPrimitiveStreamOffset(byte[] data)
    {
        return GetTextureCommandOffset(data)
               + checked((int)ReadU32(data, 0x14) * 4)
               + checked((int)ReadU32(data, 0x18) * 4)
               + checked((int)ReadU32(data, 0x1C) * 44)
               + checked((int)ReadU32(data, 0x20) * 12)
               + checked((int)ReadU32(data, 0x04) * 4)
               + checked((int)ReadU32(data, 0x0C) * 4)
               + checked(((int)ReadU32(data, 0x0C) + 3) & ~3);
    }

    private static void WriteCommand(byte[] data, int offset, int index, byte command, uint argument)
    {
        WriteU32(data, offset + index * 4, (uint)command << 24 | argument & 0x00FFFFFF);
    }

    private static void WriteU16(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)value));
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }
}
