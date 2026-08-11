using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxBlendColourPulseTests
{
    private const string ProtoBuild = "Spider-Man (2000-2-18, PSX - Prototype)";
    private const string FinalBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private readonly TestPaths _paths = new();

    [Fact]
    public void BlendPackageWriter_WritesOnlyValidatedPortableChannelsAndFailStaticCodes()
    {
        var validMidInterval = new ModelColourPulseChannel(
            [new Vector4(9f), new Vector4(8f)],
            [new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 1f, 0f, 1f)],
            [10, 10],
            0,
            5);
        var malformed = new ModelColourPulseChannel(
            [Vector4.One],
            [new Vector4(float.NaN, 0f, 0f, 1f)],
            [1],
            0,
            0);
        var validZeroOverbright = new ModelColourPulseChannel(
            [new Vector4(7f), new Vector4(6f)],
            [new Vector4(3.5f, 0.25f, 0.5f, 0.75f), Vector4.Zero],
            [0, 12],
            0,
            0);
        var unusedValid = new ModelColourPulseChannel(
            [Vector4.One],
            [new Vector4(0.9f, 0.8f, 0.7f, 0.6f)],
            [12],
            0,
            0);
        var document = CreateTriangle(
            [
                new Vector4(0.5f, 0.5f, 0f, 1f),
                new Vector4(0.2f, 0.3f, 0.4f, 1f),
                new Vector4(3.5f, 0.25f, 0.5f, 0.75f)
            ],
            [1, 2, 3],
            [validMidInterval, malformed, validZeroOverbright, unusedValid]);

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "pulse.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var channels = manifest.RootElement.GetProperty("ColourPulseChannels");
        Assert.Equal(2, channels.GetArrayLength());
        Assert.False(channels[0].TryGetProperty("PacketKeys", out _));
        Assert.Equal(3.5f, channels[1].GetProperty("PortableKeys")[0][0].GetSingle());
        Assert.Equal(0, channels[1].GetProperty("Intervals")[0].GetInt32());
        Assert.Equal(5, channels[0].GetProperty("InitialAccumulator").GetInt32());

        var primitive = manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives")[0];
        var bufferPath = primitive.GetProperty("ColourPulseBuffer").GetString();
        Assert.NotNull(bufferPath);
        using var buffer = archive.GetEntry(bufferPath!)!.Open();
        using var bytes = new MemoryStream();
        buffer.CopyTo(bytes);
        Assert.Equal(new byte[] { 1, 0, 2 }, bytes.ToArray());
    }

    [Fact]
    public void BlendPackageWriter_InvalidChannelCodeOmitsAllStaticPulseBuffer()
    {
        var channel = new ModelColourPulseChannel(
            [Vector4.One],
            [new Vector4(1f, 0f, 0f, 1f)],
            [0],
            0,
            0);
        var document = CreateTriangle(
            [Vector4.UnitW, Vector4.UnitW, Vector4.UnitW],
            [2, 2, 2],
            [channel]);

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "static.blend");

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var primitive = manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives")[0];
        Assert.False(primitive.TryGetProperty("ColourPulseBuffer", out _));
        Assert.DoesNotContain(
            archive.Entries,
            static entry => entry.FullName.EndsWith(".pulse.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void BlendPackageWriter_L1A1ObjectBankCarriesMixedPointPulseCodes()
    {
        var path = _paths.FindSampleFile(ProtoBuild, "l1a1_o.psx");
        Assert.SkipWhen(path is null, "l1a1_o.psx not present in Sample/Builds");
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path!),
            FileName = Path.GetFileName(path!),
            OutputStem = "l1a1_o",
            SourceKind = ModelSourceKind.Psx
        });

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "l1a1_o.blend");
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);

        Assert.Equal(6, manifest.RootElement.GetProperty("ColourPulseChannels").GetArrayLength());
        var pulseBuffers = manifest.RootElement.GetProperty("Meshes").EnumerateArray()
            .SelectMany(static mesh => mesh.GetProperty("Primitives").EnumerateArray())
            .Where(static primitive => primitive.TryGetProperty("ColourPulseBuffer", out _))
            .ToArray();
        Assert.Equal(15, pulseBuffers.Length);
        var pulsedVertices = 0;
        var keptCounts = new Dictionary<int, int>();
        foreach (var primitive in pulseBuffers)
        {
            var pathValue = primitive.GetProperty("ColourPulseBuffer").GetString()!;
            using var stream = archive.GetEntry(pathValue)!.Open();
            for (var value = stream.ReadByte(); value >= 0; value = stream.ReadByte())
            {
                pulsedVertices += value > 0 ? 1 : 0;
                if (value > 0)
                    keptCounts[value] = keptCounts.GetValueOrDefault(value) + 1;
            }
        }
        var sourceCounts = document.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Where(static vertex => vertex.ColourPulseChannel > 0)
            .GroupBy(static vertex => vertex.ColourPulseChannel)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var table = Assert.Single(document.NativeMetadata.OfType<PsxColourPulseTableMetadata>());
        var samples = document.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Where(static vertex => vertex.ColourPulseChannel > 0)
            .GroupBy(static vertex => vertex.ColourPulseChannel)
            .OrderBy(static group => group.Key)
            .Select(group =>
                $"{group.Key}:vertex={group.First().Color},packetVertex={group.First().PsxPacketColor}," +
                $"portable0={EvaluateFrameZero(table.Channels[group.Key - 1], portable: true)}," +
                $"packet0={EvaluateFrameZero(table.Channels[group.Key - 1], portable: false)}");
        Assert.True(
            pulsedVertices == 192,
            $"Expected 192 codes; kept {pulsedVertices}. Source=" +
            string.Join(",", sourceCounts.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}:{pair.Value}")) +
            " Kept=" +
            string.Join(",", keptCounts.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}:{pair.Value}")) +
            " Samples=" + string.Join(";", samples));
    }

    [Fact]
    public void Export_Blend_SavedPulseGraphPreservesFrameOneThenEvaluatesPortableRgba()
    {
        var helperPath = GetBlenderHelperOrSkip();
        using var temp = new TempDirectory();
        var document = CreateAnimatedSyntheticDocument();
        var result = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            OutputStem = "synthetic_pulses",
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath
        });
        var blendPath = Assert.Single(result.OutputPaths);
        var report = InspectPulseBlend(helperPath, blendPath, temp.Path);

        Assert.Equal("INT", report.AttributeType);
        Assert.Equal("POINT", report.AttributeDomain);
        Assert.Equal([1, 2, 1, 3, 0, 3], report.PulseCodes);
        Assert.True(report.HasMixedChannelFace);
        Assert.Equal(1, report.GroupCount);
        Assert.Equal(4, report.ChannelCount);
        Assert.True(report.MaterialUsesPulseColor);
        Assert.True(report.MaterialAlphaLinked);
        Assert.True(report.MaterialEmissionLinked);
        Assert.Equal("additive", report.MaterialRecipe);
        Assert.Equal("ShaderNodeMixRGB", report.MaterialEmissionSourceType);
        Assert.Equal("subtractive", report.SubtractiveMaterialRecipe);
        Assert.True(report.SubtractiveMaterialAlphaLinked);
        Assert.Equal("ShaderNodeVertexColor", report.SubtractiveEmissionSourceType);
        Assert.Contains("frame - 1.0", report.DriverExpression, StringComparison.Ordinal);

        AssertColorsClose(report.BaseColors, report.FrameOneColors, 1e-6f);
        var channels = Assert.Single(document.NativeMetadata.OfType<PsxColourPulseTableMetadata>()).Channels;
        var elapsedAtFrameTwo = (int)Math.Floor(60f / report.EffectiveFramesPerSecond);
        var expectedFrameTwo = ExpectedLoopColors(
            report.BaseColors, report.CornerPulseCodes, channels, elapsedAtFrameTwo);
        AssertColorsClose(expectedFrameTwo, report.FrameTwoColors, 2e-5f);
        Assert.Contains(report.FrameTwoColors, static color => color[0] > 1f);

        var elapsedAtLateFrame = (int)Math.Floor(99f * 60f / report.EffectiveFramesPerSecond);
        var expectedLate = ExpectedLoopColors(
            report.BaseColors, report.CornerPulseCodes, channels, elapsedAtLateFrame);
        AssertColorsClose(expectedLate, report.FrameOneHundredColors, 2e-5f);
        // Channel 2 transitions through its positive interval into key 1's
        // zero interval and must hold there forever; channel 3 starts at zero.
        Assert.Equal(0.8f, report.FrameTwoColors[1][3], 5);
        Assert.Equal(report.FrameTwoColors[1][3], report.FrameOneHundredColors[1][3], 5);
        AssertColorsClose([report.FrameTwoColors[3]], [report.FrameOneHundredColors[3]], 2e-5f);
        // Static channel 0 always retains the authored bake.
        AssertColorsClose([report.BaseColors[4]], [report.FrameOneHundredColors[4]], 1e-6f);
        AssertColorsClose(report.SubtractiveBaseColors, report.SubtractiveFrameOneColors, 1e-6f);
        Assert.All(report.SubtractiveFrameTwoColors, static color =>
        {
            Assert.Equal(0f, color[0], 6);
            Assert.Equal(0f, color[1], 6);
            Assert.Equal(0f, color[2], 6);
            Assert.Equal(0.5f, color[3], 5);
        });
    }

    [Fact]
    public void Blender51_SavedPulseGraphUsesFpsBaseAndDoesNotReuseWrongGroup()
    {
        var helperPath = GetBlenderHelperOrSkip();
        var importerPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (!File.Exists(importerPath))
            Assert.Skip("Packaged Blender importer is unavailable for the fps_base oracle.");

        using var temp = new TempDirectory();
        var createScript = Path.Combine(temp.Path, "create_fps_base.py");
        var inspectScript = Path.Combine(temp.Path, "inspect_fps_base.py");
        var blendPath = Path.Combine(temp.Path, "fps_base.blend");
        var reportPath = Path.Combine(temp.Path, "fps_base.json");
        File.WriteAllText(createScript, FpsBaseCreateScript);
        File.WriteAllText(inspectScript, FpsBaseInspectScript);

        RunBlender(
            helperPath,
            ["--background", "--factory-startup", "--python-exit-code", "1", "--python", createScript,
                "--", importerPath, blendPath],
            "fps_base graph creation");
        RunBlender(
            helperPath,
            ["--background", blendPath, "--python-exit-code", "1", "--python", inspectScript,
                "--", reportPath],
            "fps_base saved-graph inspection");

        var report = JsonSerializer.Deserialize<FpsBaseReport>(File.ReadAllText(reportPath))!;
        Assert.Equal(20f, report.EffectiveFramesPerSecond, 5);
        AssertColorsClose([new[] { 1f, 0f, 0f, 1f }], [report.FrameOne], 1e-6f);
        // fps=30, fps_base=1.5 -> effective 20 fps -> Blender frame 2 is
        // native frame 3, exactly halfway through the six-frame interval.
        AssertColorsClose([new[] { 0.5f, 0.5f, 0f, 1f }], [report.FrameTwo], 2e-5f);
        Assert.True(report.SameTableReusedOneGroup);
        Assert.True(report.DifferentFpsCreatedDifferentGroup);
        Assert.True(report.DifferentTableCreatedDifferentGroup);
    }

    [Fact]
    public void Blender51_MalformedPulseBuffersAndChannelStayStaticAfterReopen()
    {
        var helperPath = GetBlenderHelperOrSkip();
        var importerPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        using var temp = new TempDirectory();
        var blendPath = Path.Combine(temp.Path, "malformed_pulses.blend");
        var package = BuildMalformedPulsePackage(blendPath);

        RunBlenderWithInput(
            helperPath,
            ["--background", "--factory-startup", "--python-exit-code", "1", "--python", importerPath,
                "--", "--stdin-zip"],
            package,
            "malformed pulse package import");

        var scriptPath = Path.Combine(temp.Path, "inspect_malformed.py");
        var reportPath = Path.Combine(temp.Path, "malformed_report.json");
        File.WriteAllText(scriptPath, MalformedInspectionScript);
        RunBlender(
            helperPath,
            ["--background", blendPath, "--python-exit-code", "1", "--python", scriptPath, "--", reportPath],
            "malformed pulse saved-graph inspection");
        var report = JsonSerializer.Deserialize<MalformedReport>(File.ReadAllText(reportPath))!;

        Assert.Equal(5, report.ObjectCount);
        Assert.Equal(0, report.PulseAttributeCount);
        Assert.Equal(0, report.PulseModifierCount);
        Assert.False(report.MaterialUsesPulseColor);
        AssertColorsClose(report.BaseColors, report.LateColors, 1e-6f);
    }

    [Fact]
    public void Export_Blend_RealPulseFixturesSurviveSavedReopen()
    {
        var helperPath = GetBlenderHelperOrSkip();
        var fixtures = new[]
        {
            (Name: "l1a1_o", Build: ProtoBuild, File: "l1a1_o.psx"),
            (Name: "l5a3_g", Build: ProtoBuild, File: "l5a3_g.psx"),
            (Name: "firedome", Build: FinalBuild, File: "firedome.psx"),
            (Name: "l1a4_g", Build: ProtoBuild, File: "l1a4_g.psx")
        };
        using var temp = new TempDirectory();
        var blendPaths = new List<string>();
        var documents = new Dictionary<string, ModelDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixture in fixtures)
        {
            var path = _paths.FindSampleFile(fixture.Build, fixture.File);
            Assert.SkipWhen(path is null, $"{fixture.Build}/{fixture.File} is unavailable.");
            var document = new MeshModelParser().Parse(new MeshImportRequest
            {
                Source = new FileSystemAssetSource(path!),
                FileName = Path.GetFileName(path!),
                OutputStem = fixture.Name,
                SourceKind = ModelSourceKind.Psx
            });
            documents[fixture.Name] = document;
            var result = ModelExportService.Export(document, new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                OutputStem = fixture.Name,
                Format = MeshOutputFormat.Blend,
                BlenderHelperPath = helperPath
            });
            blendPaths.Add(Assert.Single(result.OutputPaths));
        }

        var reports = InspectRealPulseBlends(helperPath, blendPaths, temp.Path)
            .ToDictionary(static report => report.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, reports.Count);
        Assert.All(reports.Values, static report =>
        {
            Assert.Equal(1, report.GroupCount);
            Assert.True(report.PulseObjectCount > 0);
            Assert.True(report.FrameOneMaxDifference <= 1e-6f,
                $"{report.Name} changed its baked frame-one fallback by {report.FrameOneMaxDifference}.");
            Assert.True(report.LaterMaxDifference > 1e-5f,
                $"{report.Name} did not advance its portable pulse channels.");
        });

        var l1a1 = reports["l1a1_o"];
        Assert.Equal(6, l1a1.ChannelCount);
        Assert.Equal(15, l1a1.PulseObjectCount);
        Assert.True(l1a1.HasMixedChannelFace);
        Assert.Equal(0, l1a1.TextureWibbleObjectCount);
        var l1a1Channels = Assert.Single(
            documents["l1a1_o"].NativeMetadata.OfType<PsxColourPulseTableMetadata>()).Channels;
        var l1a1Sample = Assert.Single(l1a1.ChannelSamples, static sample => sample.Code == 1);
        var l1a1Elapsed = (int)Math.Floor(9f * 60f / l1a1.EffectiveFramesPerSecond);
        var l1a1Expected = Evaluate(l1a1Channels[0], l1a1Elapsed);
        AssertColorsClose(
            [[l1a1Expected.X, l1a1Expected.Y, l1a1Expected.Z, l1a1Expected.W]],
            [l1a1Sample.LaterColor],
            2e-5f);
        var l1a1CodeOneVertex = documents["l1a1_o"].Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .First(static vertex => vertex.ColourPulseChannel == 1);
        Assert.True(Vector4.Distance(l1a1CodeOneVertex.Color, Evaluate(l1a1Channels[0], 0)) > 0.5f,
            "The l1a1 oracle must retain the known nonlinear packet/static versus portable midpoint difference.");

        var l5a3 = reports["l5a3_g"];
        Assert.True(l5a3.HasMixedChannelFace);
        Assert.True(l5a3.UntexturedPulseMaterialCount > 0);
        Assert.Equal(l5a3.UntexturedPulseMaterialCount, l5a3.AlphaLinkedMaterialCount);
        Assert.Equal(l5a3.UntexturedPulseMaterialCount, l5a3.EmissionLinkedMaterialCount);
        Assert.Contains("additive", l5a3.UntexturedPulseRecipes);
        Assert.True(l5a3.HasAlphaOnlyUntexturedChange);

        var firedome = reports["firedome"];
        Assert.True(firedome.ChannelCount >= 7);
        var firedomeChannels = Assert.Single(
            documents["firedome"].NativeMetadata.OfType<PsxColourPulseTableMetadata>()).Channels;
        var accumulatedChannels = firedomeChannels
            .Select((channel, index) => (Channel: channel, Code: index + 1))
            .Where(static item => item.Channel.InitialAccumulator == 15)
            .ToArray();
        Assert.NotEmpty(accumulatedChannels);
        var firedomeElapsed = (int)Math.Floor(9f * 60f / firedome.EffectiveFramesPerSecond);
        Assert.Contains(accumulatedChannels, item =>
        {
            var sample = firedome.ChannelSamples.FirstOrDefault(value => value.Code == item.Code);
            if (sample == null)
                return false;
            var expected = Evaluate(item.Channel, firedomeElapsed);
            return Vector4.Distance(
                       expected,
                       new Vector4(sample.LaterColor[0], sample.LaterColor[1],
                           sample.LaterColor[2], sample.LaterColor[3])) <= 2e-5f;
        });

        var l1a4 = reports["l1a4_g"];
        var l1a4Table = Assert.Single(
            documents["l1a4_g"].NativeMetadata.OfType<PsxColourPulseTableMetadata>()).Channels;
        var l1a4RawCodes = documents["l1a4_g"].Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.ColourPulseChannel)
            .Where(static code => code > 0)
            .Distinct()
            .ToArray();
        var l1a4ReferencedCodes = documents["l1a4_g"].Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Indices
                .Where(index => (uint)index < (uint)primitive.Vertices.Length)
                .Select(index => primitive.Vertices[index].ColourPulseChannel))
            .Where(static code => code > 0)
            .Distinct()
            .ToArray();
        Assert.Equal(56, l1a4Table.Count);
        Assert.Equal(56, l1a4RawCodes.Length);
        Assert.Equal(56, l1a4ReferencedCodes.Length);
        Assert.Equal(56, l1a4.ChannelCount);
        Assert.Equal(56, l1a4.ChannelSamples.Count);
        Assert.All(l1a4.ChannelSamples, static sample => Assert.InRange(sample.Code, 1, 56));
        Assert.True(l1a4.GroupNodeCount > 200,
            $"The 56-channel stress graph unexpectedly had only {l1a4.GroupNodeCount} nodes.");
    }

    private static string GetBlenderHelperOrSkip()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var importerPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(importerPath))
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to Blender 5.1 and make the packaged importer available " +
                "to run the native colour-pulse oracle.");
        return helperPath!;
    }

    private static ModelDocument CreateAnimatedSyntheticDocument()
    {
        var channels = new ModelColourPulseChannel[]
        {
            new(
                [Vector4.Zero, Vector4.One],
                [new Vector4(0.2f, 0.4f, 0.6f, 1f), new Vector4(3.5f, 1.2f, 0.8f, 1f)],
                [10, 10],
                0,
                5),
            new(
                [Vector4.Zero, Vector4.One, Vector4.One],
                [new Vector4(1f, 1f, 1f, 0.25f), new Vector4(1f, 1f, 1f, 0.8f),
                    new Vector4(1f, 1f, 1f, 0.1f)],
                [2, 0, 9],
                0,
                0),
            new(
                [Vector4.Zero, Vector4.One],
                [new Vector4(0.1f, 0.2f, 0.3f, 0.4f), new Vector4(0.9f, 0.8f, 0.7f, 0.6f)],
                [0, 8],
                0,
                0),
            new(
                [Vector4.Zero, Vector4.One],
                [new Vector4(0f, 0f, 0f, 0.2f), new Vector4(0f, 0f, 0f, 0.8f)],
                [4, 4],
                0,
                0)
        };
        var colors = new[]
        {
            new Vector4(0.11f, 0.12f, 0.13f, 0.14f),
            new Vector4(0.21f, 0.22f, 0.23f, 0.24f),
            new Vector4(0.31f, 0.32f, 0.33f, 0.34f),
            new Vector4(0.41f, 0.42f, 0.43f, 0.44f),
            new Vector4(0.51f, 0.52f, 0.53f, 0.54f),
            new Vector4(0.61f, 0.62f, 0.63f, 0.64f)
        };
        var codes = new[] { 1, 2, 1, 3, 0, 3 };
        var document = new ModelDocument { Name = "synthetic_pulses", SourceKind = ModelSourceKind.Psx };
        document.Materials.Add(new RenderMaterial
        {
            Name = "untextured_pulse__st1",
            AlphaMode = ModelAlphaMode.Blend,
            Unlit = true
        });
        var subtractiveMaterial = new RenderMaterial
        {
            Name = "untextured_pulse__st2",
            AlphaMode = ModelAlphaMode.Blend,
            Unlit = true
        };
        subtractiveMaterial.NativeMetadata.Add(new RwGsAlphaRenderMetadata(
            0x42, 0, false, true, false, null));
        document.Materials.Add(subtractiveMaterial);
        var vertices = new ModelVertex[6];
        var positions = new[]
        {
            Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            Vector3.UnitX * 2f, new Vector3(3f, 0f, 0f), new Vector3(2f, 1f, 0f)
        };
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new ModelVertex(positions[i], Vector3.UnitZ, colors[i], Vector2.Zero)
            {
                ColourPulseChannel = codes[i]
            };
        }
        var mesh = new ModelMesh { Name = "two_triangles" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "two_triangles",
            MaterialIndex = 0,
            Vertices = vertices,
            Indices = [0, 1, 2, 3, 4, 5]
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "two_triangles", MeshIndex = 0 });
        var subtractiveMesh = new ModelMesh { Name = "subtractive_triangle" };
        subtractiveMesh.Primitives.Add(new ModelPrimitive
        {
            Name = "subtractive_triangle",
            MaterialIndex = 1,
            Vertices =
            [
                PulseVertex(new Vector3(0f, 2f, 0f), new Vector4(0f, 0f, 0f, 0.2f), 4),
                PulseVertex(new Vector3(1f, 2f, 0f), new Vector4(0f, 0f, 0f, 0.2f), 4),
                PulseVertex(new Vector3(0f, 3f, 0f), new Vector4(0f, 0f, 0f, 0.2f), 4)
            ],
            Indices = [0, 1, 2]
        });
        document.Meshes.Add(subtractiveMesh);
        document.Nodes.Add(new ModelNode { Name = "subtractive_triangle", MeshIndex = 1 });
        document.NativeMetadata.Add(new PsxColourPulseTableMetadata(channels));
        return document;
    }

    private static byte[] BuildMalformedPulsePackage(string blendPath)
    {
        var channels = new ModelColourPulseChannel[]
        {
            new([Vector4.One], [new Vector4(0.2f, 0.3f, 0.4f, 1f)], [0], 0, 0),
            new([Vector4.One], [new Vector4(0.6f, 0.7f, 0.8f, 1f)], [0], 0, 0)
        };
        var document = new ModelDocument { Name = "malformed_pulses", SourceKind = ModelSourceKind.Psx };
        document.Materials.Add(new RenderMaterial
        {
            Name = "malformed_untextured__st1",
            AlphaMode = ModelAlphaMode.Blend,
            Unlit = true
        });
        var mesh = new ModelMesh { Name = "malformed" };
        for (var primitiveIndex = 0; primitiveIndex < 5; primitiveIndex++)
        {
            var code = primitiveIndex == 3 ? 2 : 1;
            var offset = primitiveIndex * 2f;
            var color = primitiveIndex == 3 ? channels[1].PortableKeys[0] : channels[0].PortableKeys[0];
            mesh.Primitives.Add(new ModelPrimitive
            {
                Name = $"malformed_{primitiveIndex}",
                MaterialIndex = 0,
                Vertices =
                [
                    PulseVertex(new Vector3(offset, 0f, 0f), color, code),
                    PulseVertex(new Vector3(offset + 1f, 0f, 0f), color, code),
                    PulseVertex(new Vector3(offset, 1f, 0f), color, code)
                ],
                Indices = [0, 1, 2]
            });
        }
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "malformed", MeshIndex = 0 });
        document.NativeMetadata.Add(new PsxColourPulseTableMetadata(channels));

        using var source = new MemoryStream();
        BlendPackageWriter.Write(document, source, blendPath);
        source.Position = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read, true))
        {
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                entries[entry.FullName] = bytes.ToArray();
            }
        }

        var manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        var primitives = manifest["Meshes"]![0]!["Primitives"]!.AsArray();
        var missingPath = primitives[0]!["ColourPulseBuffer"]!.GetValue<string>();
        primitives[0]!.AsObject().Remove("ColourPulseBuffer");
        var shortPath = primitives[1]!["ColourPulseBuffer"]!.GetValue<string>();
        var invalidIndexPath = primitives[2]!["ColourPulseBuffer"]!.GetValue<string>();
        entries[shortPath] = [1, 1];
        entries[invalidIndexPath] = [99, 99, 99];
        manifest["ColourPulseChannels"]![1]!["PortableKeys"]![0]![0] = "not-a-number";
        primitives[4]!["ColourPulseBuffer"] = 17;
        entries["manifest.json"] = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString());
        Assert.True(entries.ContainsKey(missingPath)); // orphaned bytes prove the missing manifest path is decisive.

        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return result.ToArray();
    }

    private static ModelVertex PulseVertex(Vector3 position, Vector4 color, int code) =>
        new(position, Vector3.UnitZ, color, Vector2.Zero) { ColourPulseChannel = code };

    private static List<float[]> ExpectedLoopColors(
        IReadOnlyList<float[]> baseColors,
        IReadOnlyList<int> cornerCodes,
        IReadOnlyList<ModelColourPulseChannel> channels,
        int elapsedFrames)
    {
        var result = new List<float[]>(cornerCodes.Count);
        for (var i = 0; i < cornerCodes.Count; i++)
        {
            var code = cornerCodes[i];
            var value = code == 0
                ? new Vector4(baseColors[i][0], baseColors[i][1], baseColors[i][2], baseColors[i][3])
                : Evaluate(channels[code - 1], elapsedFrames);
            result.Add([value.X, value.Y, value.Z, value.W]);
        }
        return result;
    }

    private static Vector4 Evaluate(ModelColourPulseChannel channel, int elapsedFrames)
    {
        var index = (int)channel.InitialKeyIndex;
        var time = (int)channel.InitialAccumulator + elapsedFrames;
        for (var guard = 0; guard < byte.MaxValue; guard++)
        {
            var interval = channel.Intervals[index];
            if (interval == 0 || time < interval)
                break;
            time -= interval;
            index = (index + 1) % channel.PortableKeys.Count;
        }
        var intervalFrames = channel.Intervals[index];
        var amount = intervalFrames == 0 ? 0f : Math.Clamp(time / (float)intervalFrames, 0f, 1f);
        return Vector4.Lerp(
            channel.PortableKeys[index],
            channel.PortableKeys[(index + 1) % channel.PortableKeys.Count],
            amount);
    }

    private static PulseBlendReport InspectPulseBlend(string helperPath, string blendPath, string directory)
    {
        var scriptPath = Path.Combine(directory, "inspect_pulses.py");
        var reportPath = Path.Combine(directory, "pulse_report.json");
        File.WriteAllText(scriptPath, PulseInspectionScript);
        RunBlender(
            helperPath,
            ["--background", blendPath, "--python-exit-code", "1", "--python", scriptPath, "--", reportPath],
            "saved pulse graph inspection");
        return JsonSerializer.Deserialize<PulseBlendReport>(File.ReadAllText(reportPath))!;
    }

    private static List<RealPulseReport> InspectRealPulseBlends(
        string helperPath,
        IReadOnlyList<string> blendPaths,
        string directory)
    {
        var scriptPath = Path.Combine(directory, "inspect_real_pulses.py");
        var reportPath = Path.Combine(directory, "real_pulse_report.json");
        File.WriteAllText(scriptPath, RealPulseInspectionScript);
        var arguments = new List<string>
        {
            "--background", "--factory-startup", "--python-exit-code", "1", "--python", scriptPath,
            "--", reportPath
        };
        arguments.AddRange(blendPaths);
        RunBlender(helperPath, arguments, "real pulse fixture inspection");
        return JsonSerializer.Deserialize<List<RealPulseReport>>(File.ReadAllText(reportPath))!;
    }

    private static void RunBlender(string helperPath, IReadOnlyList<string> arguments, string operation)
    {
        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start(), $"Failed to start Blender for {operation}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"Blender {operation} failed ({process.ExitCode}).{Environment.NewLine}{stdout}" +
            Environment.NewLine + stderr);
    }

    private static void RunBlenderWithInput(
        string helperPath,
        IReadOnlyList<string> arguments,
        byte[] input,
        string operation)
    {
        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start(), $"Failed to start Blender for {operation}.");
        process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"Blender {operation} failed ({process.ExitCode}).{Environment.NewLine}{stdout}" +
            Environment.NewLine + stderr);
    }

    private static void AssertColorsClose(
        IReadOnlyList<float[]> expected,
        IReadOnlyList<float[]> actual,
        float tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            for (var component = 0; component < 4; component++)
                Assert.InRange(Math.Abs(expected[i][component] - actual[i][component]), 0f, tolerance);
        }
    }

    private sealed record PulseBlendReport(
        string AttributeType,
        string AttributeDomain,
        List<int> PulseCodes,
        List<int> CornerPulseCodes,
        bool HasMixedChannelFace,
        int GroupCount,
        int ChannelCount,
        bool MaterialUsesPulseColor,
        bool MaterialAlphaLinked,
        bool MaterialEmissionLinked,
        string MaterialRecipe,
        string MaterialEmissionSourceType,
        string SubtractiveMaterialRecipe,
        bool SubtractiveMaterialAlphaLinked,
        string SubtractiveEmissionSourceType,
        string DriverExpression,
        float EffectiveFramesPerSecond,
        List<float[]> BaseColors,
        List<float[]> FrameOneColors,
        List<float[]> FrameTwoColors,
        List<float[]> FrameOneHundredColors,
        List<float[]> SubtractiveBaseColors,
        List<float[]> SubtractiveFrameOneColors,
        List<float[]> SubtractiveFrameTwoColors);

    private sealed record FpsBaseReport(
        float EffectiveFramesPerSecond,
        float[] FrameOne,
        float[] FrameTwo,
        bool SameTableReusedOneGroup,
        bool DifferentFpsCreatedDifferentGroup,
        bool DifferentTableCreatedDifferentGroup);

    private sealed record MalformedReport(
        int ObjectCount,
        int PulseAttributeCount,
        int PulseModifierCount,
        bool MaterialUsesPulseColor,
        List<float[]> BaseColors,
        List<float[]> LateColors);

    private sealed record RealPulseReport(
        string Name,
        int GroupCount,
        int ChannelCount,
        int GroupNodeCount,
        int PulseObjectCount,
        bool HasMixedChannelFace,
        int TextureWibbleObjectCount,
        int UntexturedPulseMaterialCount,
        int AlphaLinkedMaterialCount,
        int EmissionLinkedMaterialCount,
        List<string> UntexturedPulseRecipes,
        bool HasAlphaOnlyUntexturedChange,
        float EffectiveFramesPerSecond,
        List<RealChannelSample> ChannelSamples,
        float FrameOneMaxDifference,
        float LaterMaxDifference);

    private sealed record RealChannelSample(int Code, float[] LaterColor);

    private const string PulseInspectionScript = """
        import bpy
        import json
        import sys

        report_path = sys.argv[sys.argv.index('--') + 1]
        scene = bpy.context.scene
        pulse_objects = [
            item for item in bpy.data.objects
            if item.get('neversoft_psx_colour_pulse')]
        obj = next(
            item for item in pulse_objects
            if any(material and material.get('neversoft_viewport_blend_hint') == 'additive'
                   for material in item.data.materials))
        subtractive_obj = next(
            item for item in pulse_objects
            if any(material and material.get('neversoft_viewport_blend_hint') == 'subtractive'
                   for material in item.data.materials))
        attribute = obj.data.attributes['neversoft_psx_colour_pulse_channel']
        pulse_codes = [int(item.value) for item in attribute.data]
        corner_codes = [pulse_codes[loop.vertex_index] for loop in obj.data.loops]
        base_colors = [list(item.color) for item in obj.data.color_attributes['Color'].data]

        def evaluated_colors(target, frame):
            scene.frame_set(frame)
            depsgraph = bpy.context.evaluated_depsgraph_get()
            evaluated = target.evaluated_get(depsgraph)
            return [list(item.color) for item in evaluated.data.color_attributes['Color'].data]

        material = obj.data.materials[0]
        principled = next(
            node for node in material.node_tree.nodes
            if node.bl_idname == 'ShaderNodeBsdfPrincipled')
        emission = principled.inputs.get('Emission Color') or principled.inputs.get('Emission')
        subtractive_material = subtractive_obj.data.materials[0]
        subtractive_principled = next(
            node for node in subtractive_material.node_tree.nodes
            if node.bl_idname == 'ShaderNodeBsdfPrincipled')
        subtractive_emission = (
            subtractive_principled.inputs.get('Emission Color') or
            subtractive_principled.inputs.get('Emission'))
        subtractive_base = [
            list(item.color)
            for item in subtractive_obj.data.color_attributes['Color'].data]
        group = obj.modifiers[0].node_group
        timeline = next(node for node in group.nodes if node.label == 'PS1 60 Hz timeline')
        driver_curve = next(iter(group.animation_data.drivers))
        pulse_groups = [
            item for item in bpy.data.node_groups
            if item.get('neversoft_psx_colour_pulse_signature')]
        mixed = any(
            len({corner_codes[index] for index in polygon.loop_indices if corner_codes[index] > 0}) > 1
            for polygon in obj.data.polygons)
        effective_fps = float(scene.render.fps) / float(scene.render.fps_base)

        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump({
                'AttributeType': attribute.data_type,
                'AttributeDomain': attribute.domain,
                'PulseCodes': pulse_codes,
                'CornerPulseCodes': corner_codes,
                'HasMixedChannelFace': mixed,
                'GroupCount': len(pulse_groups),
                'ChannelCount': int(group['neversoft_psx_colour_pulse_channels']),
                'MaterialUsesPulseColor': bool(material.get('neversoft_psx_colour_pulse_material')),
                'MaterialAlphaLinked': bool(principled.inputs['Alpha'].is_linked),
                'MaterialEmissionLinked': bool(emission and emission.is_linked),
                'MaterialRecipe': str(material.get('neversoft_viewport_blend_hint', '')),
                'MaterialEmissionSourceType': (
                    emission.links[0].from_node.bl_idname if emission and emission.is_linked else ''),
                'SubtractiveMaterialRecipe': str(
                    subtractive_material.get('neversoft_viewport_blend_hint', '')),
                'SubtractiveMaterialAlphaLinked': bool(
                    subtractive_principled.inputs['Alpha'].is_linked),
                'SubtractiveEmissionSourceType': (
                    subtractive_emission.links[0].from_node.bl_idname
                    if subtractive_emission and subtractive_emission.is_linked else ''),
                'DriverExpression': driver_curve.driver.expression,
                'EffectiveFramesPerSecond': effective_fps,
                'BaseColors': base_colors,
                'FrameOneColors': evaluated_colors(obj, 1),
                'FrameTwoColors': evaluated_colors(obj, 2),
                'FrameOneHundredColors': evaluated_colors(obj, 100),
                'SubtractiveBaseColors': subtractive_base,
                'SubtractiveFrameOneColors': evaluated_colors(subtractive_obj, 1),
                'SubtractiveFrameTwoColors': evaluated_colors(subtractive_obj, 2),
            }, stream)
        """;

    private const string FpsBaseCreateScript = """
        import bpy
        import importlib.util
        import sys

        importer_path, blend_path = sys.argv[sys.argv.index('--') + 1:]
        spec = importlib.util.spec_from_file_location('nmt_import_package', importer_path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        scene = bpy.context.scene
        scene.render.fps = 30
        scene.render.fps_base = 1.5
        channels = [{
            'keys': [(1.0, 0.0, 0.0, 1.0), (0.0, 1.0, 0.0, 1.0)],
            'intervals': [6, 6],
            'initial_key': 0,
            'accumulator': 0,
        }]
        first = module._make_colour_pulse_node_group(channels)
        same = module._make_colour_pulse_node_group(channels)
        scene.render.fps_base = 1.0
        different_fps = module._make_colour_pulse_node_group(channels)
        scene.render.fps_base = 1.5
        different_table = module._make_colour_pulse_node_group([{
            'keys': [(0.0, 0.0, 1.0, 1.0), (1.0, 1.0, 0.0, 1.0)],
            'intervals': [6, 6],
            'initial_key': 0,
            'accumulator': 0,
        }])

        mesh = bpy.data.meshes.new('fps_base_triangle')
        mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
        mesh.update()
        module._assign_colors(mesh, [(1.0, 0.0, 0.0, 1.0)] * 3, force_float=True)
        if not module._assign_colour_pulse_attribute(mesh, [1, 1, 1]):
            raise RuntimeError('failed to assign the POINT pulse attribute')
        obj = bpy.data.objects.new('fps_base_triangle', mesh)
        bpy.context.collection.objects.link(obj)
        modifier = obj.modifiers.new(name='Neversoft PSX Colour Pulses', type='NODES')
        modifier.node_group = first
        obj['neversoft_psx_colour_pulse'] = True
        scene['same_table_reused_one_group'] = bool(first == same)
        scene['different_fps_created_different_group'] = bool(first != different_fps)
        scene['different_table_created_different_group'] = bool(first != different_table)
        scene.frame_set(1)
        bpy.ops.wm.save_as_mainfile(filepath=blend_path, compress=True)
        """;

    private const string FpsBaseInspectScript = """
        import bpy
        import json
        import sys

        report_path = sys.argv[sys.argv.index('--') + 1]
        scene = bpy.context.scene
        obj = bpy.data.objects['fps_base_triangle']

        def first_color(frame):
            scene.frame_set(frame)
            depsgraph = bpy.context.evaluated_depsgraph_get()
            evaluated = obj.evaluated_get(depsgraph)
            return list(evaluated.data.color_attributes['Color'].data[0].color)

        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump({
                'EffectiveFramesPerSecond': float(scene.render.fps) / float(scene.render.fps_base),
                'FrameOne': first_color(1),
                'FrameTwo': first_color(2),
                'SameTableReusedOneGroup': bool(scene['same_table_reused_one_group']),
                'DifferentFpsCreatedDifferentGroup': bool(scene['different_fps_created_different_group']),
                'DifferentTableCreatedDifferentGroup': bool(scene['different_table_created_different_group']),
            }, stream)
        """;

    private const string MalformedInspectionScript = """
        import bpy
        import json
        import sys

        report_path = sys.argv[sys.argv.index('--') + 1]
        scene = bpy.context.scene
        objects = sorted(
            [item for item in bpy.data.objects if item.type == 'MESH'],
            key=lambda item: item.name)
        base_colors = [
            list(color.color)
            for obj in objects
            for color in obj.data.color_attributes['Color'].data]
        scene.frame_set(100)
        depsgraph = bpy.context.evaluated_depsgraph_get()
        late_colors = [
            list(color.color)
            for obj in objects
            for color in obj.evaluated_get(depsgraph).data.color_attributes['Color'].data]
        pulse_attribute_count = sum(
            1 for obj in objects
            if obj.data.attributes.get('neversoft_psx_colour_pulse_channel') is not None)
        pulse_modifier_count = sum(
            1 for obj in objects for modifier in obj.modifiers
            if modifier.type == 'NODES' and modifier.node_group and
               modifier.node_group.get('neversoft_psx_colour_pulse_signature'))
        material = objects[0].data.materials[0]

        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump({
                'ObjectCount': len(objects),
                'PulseAttributeCount': pulse_attribute_count,
                'PulseModifierCount': pulse_modifier_count,
                'MaterialUsesPulseColor': bool(material.get('neversoft_psx_colour_pulse_material')),
                'BaseColors': base_colors,
                'LateColors': late_colors,
            }, stream)
        """;

    private const string RealPulseInspectionScript = """
        import bpy
        import json
        import os
        import sys

        report_path, *blend_paths = sys.argv[sys.argv.index('--') + 1:]
        reports = []
        for blend_path in blend_paths:
            bpy.ops.wm.open_mainfile(filepath=blend_path)
            scene = bpy.context.scene
            objects = sorted(
                [item for item in bpy.data.objects if item.get('neversoft_psx_colour_pulse')],
                key=lambda item: item.name)
            groups = [
                item for item in bpy.data.node_groups
                if item.get('neversoft_psx_colour_pulse_signature')]
            if not objects or not groups:
                raise RuntimeError(f'{blend_path} contains no native pulse graph')
            group = groups[0]

            has_mixed = False
            for obj in objects:
                attribute = obj.data.attributes['neversoft_psx_colour_pulse_channel']
                point_codes = [int(item.value) for item in attribute.data]
                corner_codes = [point_codes[loop.vertex_index] for loop in obj.data.loops]
                if any(
                        len({corner_codes[index] for index in polygon.loop_indices
                             if corner_codes[index] > 0}) > 1
                        for polygon in obj.data.polygons):
                    has_mixed = True
                    break

            base = {
                obj.name: [tuple(item.color) for item in obj.data.color_attributes['Color'].data]
                for obj in objects
            }

            def maximum_difference(frame):
                scene.frame_set(frame)
                depsgraph = bpy.context.evaluated_depsgraph_get()
                maximum = 0.0
                for obj in objects:
                    evaluated = obj.evaluated_get(depsgraph)
                    colors = evaluated.data.color_attributes['Color'].data
                    authored = base[obj.name]
                    if len(colors) != len(authored):
                        raise RuntimeError(f'{obj.name} changed Color domain length')
                    for index, item in enumerate(colors):
                        maximum = max(
                            maximum,
                            *(abs(float(item.color[c]) - authored[index][c]) for c in range(4)))
                return maximum

            materials = {}
            for obj in objects:
                for material in obj.data.materials:
                    if material is not None:
                        materials[material.as_pointer()] = material
            untextured = [
                material for material in materials.values()
                if material.get('neversoft_psx_colour_pulse_material') and
                   not any(node.bl_idname == 'ShaderNodeTexImage' for node in material.node_tree.nodes)
            ]
            alpha_linked = 0
            emission_linked = 0
            for material in untextured:
                principled = next(
                    node for node in material.node_tree.nodes
                    if node.bl_idname == 'ShaderNodeBsdfPrincipled')
                alpha_linked += int(principled.inputs['Alpha'].is_linked)
                emission = principled.inputs.get('Emission Color') or principled.inputs.get('Emission')
                emission_linked += int(bool(emission and emission.is_linked))

            frame_one_difference = maximum_difference(1)
            later_difference = maximum_difference(10)
            scene.frame_set(10)
            depsgraph = bpy.context.evaluated_depsgraph_get()
            channel_samples = {}
            has_alpha_only_untextured_change = False
            untextured_pointers = {material.as_pointer() for material in untextured}
            for obj in objects:
                evaluated = obj.evaluated_get(depsgraph)
                colors = evaluated.data.color_attributes['Color'].data
                attribute = obj.data.attributes['neversoft_psx_colour_pulse_channel']
                point_codes = [int(item.value) for item in attribute.data]
                corner_codes = [point_codes[loop.vertex_index] for loop in obj.data.loops]
                authored = base[obj.name]
                object_is_untextured = any(
                    material and material.as_pointer() in untextured_pointers
                    for material in obj.data.materials)
                for index, code in enumerate(corner_codes):
                    if code > 0 and code not in channel_samples:
                        channel_samples[code] = [float(value) for value in colors[index].color]
                    if object_is_untextured:
                        rgb_difference = max(
                            abs(float(colors[index].color[c]) - authored[index][c])
                            for c in range(3))
                        alpha_difference = abs(float(colors[index].color[3]) - authored[index][3])
                        if rgb_difference <= 1e-5 and alpha_difference > 1e-5:
                            has_alpha_only_untextured_change = True

            texture_wibble_objects = sum(
                1 for obj in objects
                if obj.data.attributes.get('neversoft_psx_wibble_enabled') is not None or
                   any(material and material.get('neversoft_psx_texture_wibble')
                       for material in obj.data.materials))

            reports.append({
                'Name': os.path.splitext(os.path.basename(blend_path))[0],
                'GroupCount': len(groups),
                'ChannelCount': int(group['neversoft_psx_colour_pulse_channels']),
                'GroupNodeCount': len(group.nodes),
                'PulseObjectCount': len(objects),
                'HasMixedChannelFace': has_mixed,
                'TextureWibbleObjectCount': texture_wibble_objects,
                'UntexturedPulseMaterialCount': len(untextured),
                'AlphaLinkedMaterialCount': alpha_linked,
                'EmissionLinkedMaterialCount': emission_linked,
                'UntexturedPulseRecipes': sorted({
                    str(material.get('neversoft_viewport_blend_hint', ''))
                    for material in untextured}),
                'HasAlphaOnlyUntexturedChange': has_alpha_only_untextured_change,
                'EffectiveFramesPerSecond': (
                    float(scene.render.fps) / float(scene.render.fps_base)),
                'ChannelSamples': [
                    {'Code': code, 'LaterColor': color}
                    for code, color in sorted(channel_samples.items())],
                'FrameOneMaxDifference': frame_one_difference,
                'LaterMaxDifference': later_difference,
            })

        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump(reports, stream)
        """;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nmt-psx-pulse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Blender can briefly retain handles during process teardown.
            }
        }
    }

    private static ModelDocument CreateTriangle(
        IReadOnlyList<Vector4> colors,
        IReadOnlyList<int> pulseCodes,
        IReadOnlyList<ModelColourPulseChannel> channels)
    {
        var document = new ModelDocument
        {
            Name = "psx_colour_pulse",
            SourceKind = ModelSourceKind.Psx
        };
        document.Materials.Add(new RenderMaterial { Name = "pulse__st1", AlphaMode = ModelAlphaMode.Blend });
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var vertices = new ModelVertex[3];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new ModelVertex(
                positions[i],
                Vector3.UnitZ,
                colors[i],
                Vector2.Zero)
            {
                ColourPulseChannel = pulseCodes[i]
            };
        }
        var mesh = new ModelMesh { Name = "triangle" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "triangle",
            MaterialIndex = 0,
            Vertices = vertices,
            Indices = [0, 1, 2]
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "triangle", MeshIndex = 0 });
        document.NativeMetadata.Add(new PsxColourPulseTableMetadata(channels));
        return document;
    }

    private static Vector4 EvaluateFrameZero(ModelColourPulseChannel channel, bool portable)
    {
        var index = (int)channel.InitialKeyIndex;
        var time = (int)channel.InitialAccumulator;
        for (var guard = 0; guard < byte.MaxValue; guard++)
        {
            var interval = channel.Intervals[index];
            if (interval == 0 || time < interval)
                break;
            time -= interval;
            index = (index + 1) % channel.PortableKeys.Count;
        }
        var amount = channel.Intervals[index] == 0
            ? 0f
            : Math.Clamp(time / (float)channel.Intervals[index], 0f, 1f);
        var keys = portable ? channel.PortableKeys : channel.PacketKeys;
        return Vector4.Lerp(keys[index], keys[(index + 1) % keys.Count], amount);
    }
}
