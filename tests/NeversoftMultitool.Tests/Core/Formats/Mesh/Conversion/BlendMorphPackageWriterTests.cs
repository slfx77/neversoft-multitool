using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the blend package's morph-target payload: the delta buffers a mesh's
///     shape keys are built from, the weights track that drives them, and — the
///     load-bearing half — every shape that must be REFUSED, because a
///     half-written morph rig would deform the wrong vertices rather than fail
///     visibly.
/// </summary>
public sealed class BlendMorphPackageWriterTests(TestPaths paths)
{
    private const string GbaBuild = "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)";
    private const string GbaRomName = "Tony Hawk's Pro Skater 2 (USA, Europe).gba";

    [Fact]
    public void Write_MorphTargets_EmitsParallelDeltaBuffersAndAWeightsTrack()
    {
        var document = CreateMorphDocument(targetsPerPrimitive: 2);
        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "morph.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifest = ReadManifest(archive);

        var primitives = manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives");
        Assert.Equal(2, primitives.GetArrayLength());
        foreach (var primitive in primitives.EnumerateArray())
        {
            var targets = primitive.GetProperty("MorphTargets");
            Assert.Equal(2, targets.GetArrayLength());
            var vertexCount = primitive.GetProperty("VertexCount").GetInt32();
            for (var target = 0; target < targets.GetArrayLength(); target++)
            {
                Assert.Equal($"target_{target}", targets[target].GetProperty("Name").GetString());
                Assert.Equal(vertexCount, targets[target].GetProperty("VertexCount").GetInt32());
                var deltas = ReadFloats(
                    archive, targets[target].GetProperty("PositionDeltaBuffer").GetString()!);
                Assert.Equal(vertexCount * 3, deltas.Length);
                // Synthetic deltas are (target+1, 0, 0) on every vertex.
                for (var vertex = 0; vertex < vertexCount; vertex++)
                {
                    Assert.Equal(target + 1f, deltas[vertex * 3]);
                    Assert.Equal(0f, deltas[vertex * 3 + 1]);
                    Assert.Equal(0f, deltas[vertex * 3 + 2]);
                }
            }
        }

        var animation = manifest.RootElement.GetProperty("Animations")[0];
        Assert.Empty(animation.GetProperty("Channels").EnumerateArray());
        var channel = animation.GetProperty("MorphChannel");
        Assert.Equal(0, channel.GetProperty("MeshIndex").GetInt32());
        Assert.Equal(2, channel.GetProperty("TargetCount").GetInt32());
        Assert.Equal(3, channel.GetProperty("KeyCount").GetInt32());
        Assert.Equal(
            [0f, 0.5f, 1f],
            ReadFloats(archive, channel.GetProperty("TimesBuffer").GetString()!));
        // Key-major: one target fully applied per key, then both at half.
        Assert.Equal(
            [1f, 0f, 0f, 1f, 0.5f, 0.5f],
            ReadFloats(archive, channel.GetProperty("WeightsBuffer").GetString()!));
    }

    /// <summary>
    ///     The morph fields are null-omitted, so a document without morph data
    ///     produces exactly the package it produced before morph support existed.
    /// </summary>
    [Fact]
    public void Write_WithoutMorphData_OmitsEveryMorphKeyAndBuffer()
    {
        var document = CreateMorphDocument(targetsPerPrimitive: 0);
        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "static.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, static entry => entry.FullName.Contains("morph"));

        using var manifest = ReadManifest(archive);
        foreach (var primitive in manifest.RootElement.GetProperty("Meshes")[0]
                     .GetProperty("Primitives").EnumerateArray())
        {
            Assert.False(primitive.TryGetProperty("MorphTargets", out _));
        }

        Assert.False(manifest.RootElement.GetProperty("Animations")[0]
            .TryGetProperty("MorphChannel", out _));
    }

    /// <summary>
    ///     Weights apply mesh-wide, so primitives that disagree on target count
    ///     have no well-defined track. Emitting the targets anyway would give the
    ///     importer a rig the manifest cannot address.
    /// </summary>
    [Fact]
    public void Write_PrimitivesDisagreeingOnTargetCount_OmitsTargetsAndChannel()
    {
        var document = CreateMorphDocument(targetsPerPrimitive: 2);
        var mesh = document.Meshes[0];
        mesh.Primitives[1] = WithTargets(mesh.Primitives[1], targetCount: 1, deltaLength: null);

        AssertNoMorphPayload(document);
    }

    [Fact]
    public void Write_DeltaCountNotParallelToVertices_OmitsTargetsAndChannel()
    {
        var document = CreateMorphDocument(targetsPerPrimitive: 2);
        var mesh = document.Meshes[0];
        mesh.Primitives[0] = WithTargets(
            mesh.Primitives[0], targetCount: 2, deltaLength: mesh.Primitives[0].Vertices.Length - 1);

        AssertNoMorphPayload(document);
    }

    [Theory]
    [InlineData("weights")]
    [InlineData("times")]
    [InlineData("mesh")]
    [InlineData("count")]
    public void Write_MalformedMorphChannel_KeepsTargetsButOmitsTheChannel(string defect)
    {
        var document = CreateMorphDocument(targetsPerPrimitive: 2);
        var channel = document.Animations[0].MorphChannel!;
        document.Animations[0] = new ModelAnimation
        {
            Name = document.Animations[0].Name,
            MorphChannel = new ModelMorphChannel
            {
                MeshIndex = defect == "mesh" ? 7 : channel.MeshIndex,
                TargetCount = defect == "count" ? 3 : channel.TargetCount,
                Times = defect == "times" ? [0f, float.NaN, 1f] : channel.Times,
                Weights = defect == "weights" ? [1f, 0f] : channel.Weights
            }
        };

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "malformed.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifest = ReadManifest(archive);
        Assert.False(manifest.RootElement.GetProperty("Animations")[0]
            .TryGetProperty("MorphChannel", out _));
        Assert.DoesNotContain(
            archive.Entries, static entry => entry.FullName.Contains("_morph."));
        // The geometry is intact, so the mesh still gets its shape keys.
        Assert.Equal(2, manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives")[0].GetProperty("MorphTargets").GetArrayLength());
    }

    /// <summary>
    ///     The real skater: 44 primitives sharing one clip's 18 distinct poses,
    ///     every delta buffer parallel to its own primitive and every key naming
    ///     exactly one pose — the same shape the glTF weights track carries.
    /// </summary>
    [CorpusFact]
    public void Write_GbaKickflipClip_CarriesEveryPrimitivesDeltasAndOneTargetPerKey()
    {
        var document = ParseGbaClip(clipIndex: 20);
        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "kickflip.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifest = ReadManifest(archive);

        var primitives = manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives");
        // 44, not the whole 46: a character draws only the sub-objects its own
        // roster mask names (see the per-character parts fix), so this skater
        // wears neither the hood nor the other leg style.
        Assert.Equal(44, primitives.GetArrayLength());
        foreach (var primitive in primitives.EnumerateArray())
        {
            var vertexCount = primitive.GetProperty("VertexCount").GetInt32();
            var targets = primitive.GetProperty("MorphTargets");
            Assert.Equal(18, targets.GetArrayLength());
            foreach (var target in targets.EnumerateArray())
            {
                Assert.StartsWith("Kickflip (20)_f", target.GetProperty("Name").GetString());
                Assert.Equal(
                    vertexCount * 3,
                    ReadFloats(archive, target.GetProperty("PositionDeltaBuffer").GetString()!).Length);
            }
        }

        var channel = manifest.RootElement.GetProperty("Animations")[0]
            .GetProperty("MorphChannel");
        Assert.Equal(18, channel.GetProperty("TargetCount").GetInt32());
        Assert.Equal(18, channel.GetProperty("KeyCount").GetInt32());
        var weights = ReadFloats(archive, channel.GetProperty("WeightsBuffer").GetString()!);
        Assert.Equal(18 * 18, weights.Length);
        for (var key = 0; key < 18; key++)
            Assert.Equal(1f, weights.Skip(key * 18).Take(18).Sum());

        var times = ReadFloats(archive, channel.GetProperty("TimesBuffer").GetString()!);
        Assert.Equal(18, times.Length);
        Assert.Equal(0f, times[0]);
        // One key per 60 Hz tick, the writer's declared export cadence.
        Assert.Equal(17f / 60f, times[17], 5);
    }

    private static void AssertNoMorphPayload(ModelDocument document)
    {
        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "rejected.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, static entry => entry.FullName.Contains("morph"));

        using var manifest = ReadManifest(archive);
        foreach (var primitive in manifest.RootElement.GetProperty("Meshes")[0]
                     .GetProperty("Primitives").EnumerateArray())
        {
            Assert.False(primitive.TryGetProperty("MorphTargets", out _));
        }

        Assert.False(manifest.RootElement.GetProperty("Animations")[0]
            .TryGetProperty("MorphChannel", out _));
    }

    private ModelDocument ParseGbaClip(int clipIndex)
    {
        var romPath = paths.FindSampleFile(GbaBuild, GbaRomName);
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaSkaterModel.TryLocate(rom);
        Assert.NotNull(model);

        var directory = Path.Combine(
            Path.GetTempPath(), $"nmt-gba-morph-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, GbaLevelCarver.RomEntryName), rom);
            var record = rom.AsSpan(
                model.CharacterTableOffset + 13 * GbaSkaterModel.CharacterRecordSize,
                GbaSkaterModel.CharacterRecordSize).ToArray();
            var path = Path.Combine(directory, "13_character.chr.gba");
            File.WriteAllBytes(path, record);

            return new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = new FileSystemAssetSource(path),
                FileName = Path.GetFileName(path),
                OutputStem = "13_character",
                SourceKind = ModelSourceKind.GbaModel,
                GbaAnimationIndices = [clipIndex]
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelDocument CreateMorphDocument(int targetsPerPrimitive)
    {
        var document = new ModelDocument { Name = "morph", SourceKind = ModelSourceKind.GbaModel };
        document.Materials.Add(new RenderMaterial { Name = "mat", BaseColor = Vector4.One });
        var mesh = new ModelMesh { Name = "morphing" };
        for (var primitiveIndex = 0; primitiveIndex < 2; primitiveIndex++)
        {
            // Different vertex counts, so a delta buffer copied from the wrong
            // primitive could not pass the parallel-length check by accident.
            var vertexCount = 3 * (primitiveIndex + 1);
            mesh.Primitives.Add(WithTargets(
                new ModelPrimitive
                {
                    Name = $"p{primitiveIndex}",
                    MaterialIndex = 0,
                    Vertices = Enumerable.Range(0, vertexCount)
                        .Select(index => new ModelVertex(
                            new Vector3(index, primitiveIndex, 0f),
                            Vector3.UnitZ,
                            Vector4.One,
                            Vector2.Zero))
                        .ToArray(),
                    Indices = Enumerable.Range(0, vertexCount).ToArray()
                },
                targetsPerPrimitive,
                deltaLength: null));
        }

        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "morphing", MeshIndex = 0 });
        document.Animations.Add(new ModelAnimation
        {
            Name = "clip",
            MorphChannel = targetsPerPrimitive == 0
                ? null
                : new ModelMorphChannel
                {
                    MeshIndex = 0,
                    TargetCount = targetsPerPrimitive,
                    Times = [0f, 0.5f, 1f],
                    Weights = [1f, 0f, 0f, 1f, 0.5f, 0.5f]
                }
        });
        return document;
    }

    private static ModelPrimitive WithTargets(
        ModelPrimitive primitive, int targetCount, int? deltaLength)
    {
        return new ModelPrimitive
        {
            Name = primitive.Name,
            MaterialIndex = primitive.MaterialIndex,
            Vertices = primitive.Vertices,
            Indices = primitive.Indices,
            MorphTargets = targetCount == 0
                ? null
                : Enumerable.Range(0, targetCount)
                    .Select(target => new ModelMorphTarget
                    {
                        Name = $"target_{target}",
                        PositionDeltas = Enumerable
                            .Repeat(new Vector3(target + 1f, 0f, 0f),
                                deltaLength ?? primitive.Vertices.Length)
                            .ToArray()
                    })
                    .ToArray()
        };
    }

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        using var stream = archive.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream);
    }

    private static float[] ReadFloats(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var raw = bytes.ToArray();
        var values = new float[raw.Length / sizeof(float)];
        Buffer.BlockCopy(raw, 0, values, 0, values.Length * sizeof(float));
        return values;
    }
}
