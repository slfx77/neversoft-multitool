using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Headless Blender guard for the standard PSX carrier layout. Each seed
///     starts a genuinely fresh Blender process because the importer bug this
///     replaces depended on Python hash iteration order.
/// </summary>
public sealed class PsxGltfBlenderCarrierRegressionTests
{
    private const int ProcessCount = 12;

    [Fact]
    public async Task Blender51_ImportsSoleCustomSemanticAndStandardCarriersAcrossFreshProcesses()
    {
        var blenderPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        if (string.IsNullOrWhiteSpace(blenderPath) || !File.Exists(blenderPath))
        {
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to Blender 5.1's blender.exe to run the PSX glTF carrier regression.");
        }

        using var temp = new TempDirectory();
        var glbPath = Path.Combine(temp.Path, "psx_carriers.glb");
        var scriptPath = Path.Combine(temp.Path, "inspect_carriers.py");
        var (glb, triangles) = new GltfModelExporter().BuildGlbBytes(CreateDocument());
        Assert.Equal(1, triangles);
        Assert.NotNull(glb);
        var testCancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(glbPath, glb, testCancellationToken);
        await File.WriteAllTextAsync(scriptPath, BlenderInspectionScript, testCancellationToken);

        for (var seed = 0; seed < ProcessCount; seed++)
        {
            var markerPath = Path.Combine(temp.Path, $"imported_{seed}.txt");
            using var process = StartBlender(
                blenderPath!, scriptPath, glbPath, markerPath, seed);
            var cancellationToken = testCancellationToken;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"Blender seed {seed} timed out.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(
                process.ExitCode == 0,
                $"Blender seed {seed} failed ({process.ExitCode}).\n{stdout}\n{stderr}");
            Assert.True(
                !(stdout + stderr).Contains("Traceback (most recent call last)", StringComparison.Ordinal),
                $"Blender seed {seed} emitted a traceback.\n{stdout}\n{stderr}");
            Assert.True(File.Exists(markerPath), $"Blender seed {seed} did not write its post-import marker.");
            Assert.Equal("PASS", await File.ReadAllTextAsync(markerPath, cancellationToken));
        }
    }

    private static Process StartBlender(
        string blenderPath,
        string scriptPath,
        string glbPath,
        string markerPath,
        int seed)
    {
        var startInfo = new ProcessStartInfo(blenderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--background");
        startInfo.ArgumentList.Add("--factory-startup");
        startInfo.ArgumentList.Add("--python");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(glbPath);
        startInfo.ArgumentList.Add(markerPath);
        startInfo.Environment["PYTHONHASHSEED"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Failed to start Blender.");
    }

    private static ModelDocument CreateDocument()
    {
        var wibble = new ModelTextureWibble(
            -4096,
            -2048,
            595,
            7,
            3,
            11,
            9,
            64,
            128);
        var vertices = new[]
        {
            new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        };
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = vertices[i] with
            {
                PsxPacketColor = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f),
                PsxPrimitiveFlags = Vector3.One,
                ColourPulseChannel = 37,
                TextureWibble = wibble
            };
        }

        var mesh = new ModelMesh { Name = "psx_carriers" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "psx_carriers",
            Vertices = vertices,
            Indices = [0, 1, 2]
        });
        var document = new ModelDocument { Name = "psx_carriers" };
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "psx_carriers", MeshIndex = 0 });
        return document;
    }

    private const string BlenderInspectionScript = """
import bpy
import pathlib
import sys

separator = sys.argv.index("--")
glb_path, marker_path = sys.argv[separator + 1:separator + 3]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
assert len(objects) == 1, f"expected one mesh, got {len(objects)}"
mesh = objects[0].data
assert mesh.get("neversoftPsxVertexCarriers") == 1
assert objects[0].get("neversoftPsxVertexCarriers") is None

psx_names = sorted(attr.name for attr in mesh.attributes if attr.name.startswith("_PSX_"))
assert psx_names == ["_PSX_COLOR_0"], psx_names
packet = mesh.attributes["_PSX_COLOR_0"]
assert packet.data_type == "FLOAT_COLOR" and packet.domain == "POINT"
packet_value = packet.data[0].color
expected_packet = (144 / 255, 119 / 255, 223 / 255, 1.0)
assert all(abs(packet_value[index] - expected_packet[index]) < 1e-6 for index in range(4)), packet_value[:]

assert [attr.name for attr in mesh.color_attributes] == ["Color", "Color.001", "_PSX_COLOR_0"]
flags = mesh.color_attributes["Color.001"]
assert flags.data_type == "BYTE_COLOR" and flags.domain == "CORNER"
flag_value = flags.data[0].color_srgb
assert all(abs(flag_value[index] - 1.0) < 1e-6 for index in range(3)), flag_value[:]
assert round(flag_value[3] * 255) == 37, flag_value[:]

assert [layer.name for layer in mesh.uv_layers] == [
    "UVMap", "UVMap.001", "UVMap.002", "UVMap.003"
]
velocity = mesh.uv_layers[1].data[0].uv
wave = mesh.uv_layers[2].data[0].uv
size = mesh.uv_layers[3].data[0].uv
assert abs(velocity.x - -4096) < 1e-4 and abs(velocity.y - -2048) < 1e-4, velocity[:]
assert abs(wave.x - 595) < 1e-4 and abs(wave.y - 0x73B9) < 1e-4, wave[:]
assert abs(size.x - 64) < 1e-4 and abs(size.y - 128) < 1e-4, size[:]

pathlib.Path(marker_path).write_text("PASS", encoding="utf-8")
""";

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"neversoft-psx-carrier-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup after an external Blender process.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup after an external Blender process.
            }
        }
    }
}
