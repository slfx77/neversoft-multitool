using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     End-to-end pins for the exported colour-pulse data. The load-bearing one
///     is <see cref="Export_Frame0OfEveryChannel_EqualsTheBakedVertexColour" />:
///     a channel bound to the wrong pulse, or built with an incomplete dedup key,
///     still produces a plausible-looking animation, so only comparing frame 0
///     against the statically baked colour catches it. It already caught a real
///     off-by-one in the lane decode.
/// </summary>
public class PsxColourPulseExportTests
{
    private const string ProtoBuild = "Spider-Man (2000-2-18, PSX - Prototype)";

    private readonly TestPaths _paths = new();

    private static JsonDocument ParseGlbJson(byte[] glb)
    {
        var offset = 12;
        while (offset < glb.Length)
        {
            var length = BitConverter.ToInt32(glb, offset);
            var type = BitConverter.ToUInt32(glb, offset + 4);
            if (type == 0x4E4F534A)
                return JsonDocument.Parse(glb.AsMemory(offset + 8, length));
            offset += 8 + length;
        }

        throw new InvalidDataException("No JSON chunk in GLB");
    }

    private ModelDocument ParsePulsedBank()
    {
        var path = _paths.FindSampleFile(ProtoBuild, "l1a1_o.psx");
        Assert.SkipWhen(path is null, "l1a1_o.psx not present in Sample/Builds");

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path!),
            FileName = Path.GetFileName(path!),
            OutputStem = "l1a1_o",
            SourceKind = ModelSourceKind.Psx
        });
    }

    private byte[] ExportPulsedBank()
    {
        var (glb, _) = ModelExportService.BuildGlbBytes(ParsePulsedBank());
        Assert.NotNull(glb);
        return glb!;
    }

    /// <summary>
    ///     Real pulse-only viewer fixture. This is the exact shape that exposed
    ///     the shared-clock bug: pulse bindings are present, but no primitive
    ///     has UV-wibble data to keep the old clock moving.
    /// </summary>
    [CorpusFact]
    public void Export_L1A1ObjectBank_IsPulseOnlySurfaceAnimation()
    {
        var document = ParsePulsedBank();
        var primitives = document.Meshes.SelectMany(static mesh => mesh.Primitives).ToArray();
        var pulsedPrimitives = primitives
            .Where(static primitive => primitive.Vertices.Any(static vertex => vertex.ColourPulseChannel > 0))
            .ToArray();

        var channels = Assert.Single(document.NativeMetadata.OfType<PsxColourPulseTableMetadata>());
        Assert.Equal(6, channels.Channels.Count);
        Assert.Equal(15, pulsedPrimitives.Length);
        Assert.Equal(
            192,
            pulsedPrimitives.Sum(static primitive =>
                primitive.Vertices.Count(static vertex => vertex.ColourPulseChannel > 0)));
        Assert.DoesNotContain(
            primitives,
            static primitive => primitive.Vertices.Any(static vertex => vertex.TextureWibble.HasValue));
    }

    [CorpusFact]
    public void Export_PublishesTheChannelTableAsSceneExtras()
    {
        using var json = ParseGlbJson(ExportPulsedBank());
        var root = json.RootElement;
        var sceneIndex = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
        var scene = root.GetProperty("scenes")[sceneIndex];

        Assert.True(
            scene.TryGetProperty("extras", out var extras),
            "The scene carries no extras, so the pulse table never reached the GLB.");
        Assert.True(extras.TryGetProperty("neversoftColourPulseChannels", out var channels));

        // l1a1_o's bank ships six staggered pulses.
        Assert.Equal(6, channels.GetArrayLength());
        foreach (var channel in channels.EnumerateArray())
        {
            Assert.True(channel.GetProperty("keys").GetArrayLength() > 0);
            Assert.Equal(
                channel.GetProperty("keys").GetArrayLength(),
                channel.GetProperty("intervals").GetArrayLength());
            Assert.Equal(
                channel.GetProperty("keys").GetArrayLength(),
                channel.GetProperty("portableKeys").GetArrayLength());
        }
    }

    [CorpusFact]
    public void Export_TagsPulsedMeshesWithMeshExtras()
    {
        using var json = ParseGlbJson(ExportPulsedBank());

        var tagged = json.RootElement.GetProperty("meshes").EnumerateArray()
            .Count(mesh => mesh.TryGetProperty("extras", out var extras)
                           && extras.TryGetProperty("neversoftColourPulse", out var flag)
                           && flag.GetBoolean());

        Assert.True(tagged > 0, "No mesh carried neversoftColourPulse extras.");
    }

    /// <summary>
    ///     PSX primitives must expose at most the sole native custom semantic,
    ///     _PSX_COLOR_0. Flags, pulse binding, and wibble data use standard
    ///     COLOR/TEXCOORD carriers so Blender never sees a custom-name set to
    ///     mis-zip against append-ordered arrays.
    /// </summary>
    [CorpusFact]
    public void Export_AddsNoNewCustomVertexAttribute()
    {
        using var json = ParseGlbJson(ExportPulsedBank());

        var attributeNames = json.RootElement.GetProperty("meshes").EnumerateArray()
            .SelectMany(mesh => mesh.GetProperty("primitives").EnumerateArray())
            .SelectMany(primitive => primitive.GetProperty("attributes").EnumerateObject())
            .Select(attribute => attribute.Name)
            .Where(name => name.StartsWith('_'))
            .Distinct()
            .ToHashSet();

        Assert.DoesNotContain("_PSX_PULSE_0", attributeNames);
        Assert.Subset(
            new HashSet<string> { "_PSX_COLOR_0" },
            attributeNames);

        Assert.All(
            json.RootElement.GetProperty("meshes").EnumerateArray()
                .SelectMany(mesh => mesh.GetProperty("primitives").EnumerateArray()),
            primitive => Assert.InRange(
                primitive.GetProperty("attributes").EnumerateObject()
                    .Count(attribute => attribute.Name.StartsWith('_')),
                0,
                1));
    }

    [CorpusFact]
    public void Export_FlagsAndPulseAttributeIsNormalizedUshortVec4()
    {
        using var json = ParseGlbJson(ExportPulsedBank());
        var accessors = json.RootElement.GetProperty("accessors");

        foreach (var primitive in json.RootElement.GetProperty("meshes").EnumerateArray()
                     .SelectMany(mesh => mesh.GetProperty("primitives").EnumerateArray()))
        {
            if (!primitive.GetProperty("attributes").TryGetProperty("COLOR_1", out var index))
                continue;

            var accessor = accessors[index.GetInt32()];
            Assert.Equal("VEC4", accessor.GetProperty("type").GetString());
            Assert.Equal(5123, accessor.GetProperty("componentType").GetInt32());
            Assert.True(accessor.GetProperty("normalized").GetBoolean());
        }
    }

    /// <summary>
    ///     THE correctness pin. Every pulsed vertex's stored colour is the static
    ///     bake; evaluating its channel at frame 0 must reproduce it exactly. A
    ///     mis-keyed dedup, a wrong palette binding, or a bad lane decode all fail
    ///     here and nowhere else.
    /// </summary>
    [CorpusFact]
    public void Export_Frame0OfEveryChannel_EqualsTheBakedVertexColour()
    {
        var glb = ExportPulsedBank();
        var checkedVertices = PsxColourPulseGlbInspector.CheckFrameZero(glb, out var mismatches);

        Assert.True(checkedVertices > 0, "No pulsed vertices were exported at all.");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(10)));
    }

    [CorpusFact]
    public void Export_Frame0MatchesTheBake_AcrossEveryPulsedPsxFile()
    {
        Assert.SkipWhen(!_paths.HasSampleBuilds, "Sample/Builds not present");

        var parser = new MeshModelParser();
        var offenders = new List<string>();
        var pulsedFiles = 0;
        var totalVertices = 0;

        foreach (var file in Directory.EnumerateFiles(_paths.SampleBuildsDir!, "*.psx", SearchOption.AllDirectories))
        {
            PsxMeshFile? psx;
            try
            {
                // Texture-only .psx files parse to null — they carry no mesh.
                psx = PsxMeshFile.Parse(file);
            }
            catch
            {
                continue;
            }

            if (psx?.ColourPulses is not { Count: > 0 } || psx.Meshes.Count == 0)
                continue;

            byte[]? glb;
            try
            {
                var document = parser.Parse(new MeshImportRequest
                {
                    Source = new FileSystemAssetSource(file),
                    FileName = Path.GetFileName(file),
                    OutputStem = Path.GetFileNameWithoutExtension(file),
                    SourceKind = ModelSourceKind.Psx
                });
                (glb, _) = ModelExportService.BuildGlbBytes(document);
            }
            catch (Exception ex)
            {
                offenders.Add($"{Path.GetFileName(file)}: export threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (glb == null)
                continue;

            pulsedFiles++;
            totalVertices += PsxColourPulseGlbInspector.CheckFrameZero(glb, out var mismatches);
            foreach (var mismatch in mismatches.Take(3))
                offenders.Add($"{Path.GetFileName(file)}: {mismatch}");
        }

        Assert.True(pulsedFiles > 0, "No pulse-bearing .psx files were found.");
        Assert.True(offenders.Count == 0, $"{offenders.Count} mismatches:\n" + string.Join("\n", offenders.Take(15)));
        Assert.True(totalVertices > 0, "No pulsed vertices across the whole corpus.");
    }
}
