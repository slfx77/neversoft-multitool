using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Rendering;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SkeletonRootTransformTests
{
    private static readonly Matrix4x4 FirstRootTransform =
        Matrix4x4.CreateRotationY(0.45f)
        * Matrix4x4.CreateTranslation(12f, -3f, 5f);

    private static readonly Matrix4x4 SecondRootTransform =
        Matrix4x4.CreateRotationX(-0.30f)
        * Matrix4x4.CreateTranslation(-7f, 4f, 19f);

    [Fact]
    public void ModelSkeleton_DefaultRootTransformIsIdentity()
    {
        var skeleton = new ModelSkeleton { Name = "identity" };

        Assert.Equal(Matrix4x4.Identity, skeleton.RootTransform);
    }

    [Fact]
    public void BuildGlbBytes_TwoSkinnedCopiesKeepDistinctRootsAndIdenticalLocalAnimation()
    {
        var document = CreateTwoCopyDocument();

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(2, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Equal(2, model.LogicalSkins.Count);

        var firstRoot = Assert.Single(model.LogicalNodes, static node => node.Name == "first_root");
        var secondRoot = Assert.Single(model.LogicalNodes, static node => node.Name == "second_root");
        AssertMatrixClose(FirstRootTransform, firstRoot.LocalMatrix);
        AssertMatrixClose(SecondRootTransform, secondRoot.LocalMatrix);

        var firstBone = Assert.Single(
            model.LogicalNodes,
            node => node.Name == "first_joint" && ReferenceEquals(node.VisualParent, firstRoot));
        var secondBone = Assert.Single(
            model.LogicalNodes,
            node => node.Name == "second_joint" && ReferenceEquals(node.VisualParent, secondRoot));
        AssertMatrixClose(Matrix4x4.Identity, firstBone.LocalMatrix);
        AssertMatrixClose(firstBone.LocalMatrix, secondBone.LocalMatrix);

        var skinnedNodes = model.LogicalNodes
            .Where(static node => node.Mesh != null && node.Skin != null)
            .ToArray();
        Assert.Equal(2, skinnedNodes.Length);
        var skinRoots = skinnedNodes.Select(static node =>
        {
            var (joint, _) = node.Skin!.GetJoint(0);
            return joint.VisualParent;
        }).ToArray();
        Assert.Contains(skinRoots, root => ReferenceEquals(root, firstRoot));
        Assert.Contains(skinRoots, root => ReferenceEquals(root, secondRoot));

        var bindScene = GlbModelLoader.Load(model, null, 0f);
        var centroids = bindScene.Submeshes.Select(static submesh =>
        {
            var sum = Vector3.Zero;
            for (var i = 0; i < submesh.Positions.Length; i += 3)
            {
                sum += new Vector3(
                    submesh.Positions[i],
                    submesh.Positions[i + 1],
                    submesh.Positions[i + 2]);
            }

            return sum / submesh.VertexCount;
        }).ToArray();
        var sourceCentroid = new Vector3(1f / 3f, 1f / 3f, 0f);
        var expectedFirstCentroid = Vector3.Transform(sourceCentroid, FirstRootTransform);
        var expectedSecondCentroid = Vector3.Transform(sourceCentroid, SecondRootTransform);
        Assert.Contains(centroids, centroid => Vector3.Distance(centroid, expectedFirstCentroid) < 1e-5f);
        Assert.Contains(centroids, centroid => Vector3.Distance(centroid, expectedSecondCentroid) < 1e-5f);

        var animation = Assert.Single(model.LogicalAnimations);
        var firstChannel = Assert.Single(
            animation.Channels,
            channel => ReferenceEquals(channel.TargetNode, firstBone));
        var secondChannel = Assert.Single(
            animation.Channels,
            channel => ReferenceEquals(channel.TargetNode, secondBone));
        var firstKeys = firstChannel.GetRotationSampler().GetLinearKeys().ToArray();
        var secondKeys = secondChannel.GetRotationSampler().GetLinearKeys().ToArray();
        Assert.Equal(firstKeys.Length, secondKeys.Length);
        for (var i = 0; i < firstKeys.Length; i++)
        {
            Assert.Equal(firstKeys[i].Key, secondKeys[i].Key, 6);
            AssertQuaternionClose(firstKeys[i].Value, secondKeys[i].Value);
        }

        // The same local key is evaluated under each skeleton's own synthetic
        // root, proving placement is isolated above (not baked into) the bone
        // animation channel.
        AssertMatrixClose(FirstRootTransform, firstBone.GetWorldMatrix(animation, 0f));
        AssertMatrixClose(SecondRootTransform, secondBone.GetWorldMatrix(animation, 0f));
    }

    [Fact]
    public void BlendManifest_SerializesEverySkeletonRootTransform()
    {
        var document = CreateTwoCopyDocument();
        using var package = new MemoryStream();

        BlendPackageWriter.Write(document, package, "two_copies.blend");

        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var skeletons = manifest.RootElement.GetProperty("Skeletons");
        Assert.Equal(2, skeletons.GetArrayLength());
        AssertMatrixClose(
            FirstRootTransform,
            ReadMatrix(skeletons[0].GetProperty("RootTransform")));
        AssertMatrixClose(
            SecondRootTransform,
            ReadMatrix(skeletons[1].GetProperty("RootTransform")));
    }

    [Fact]
    public void ExportBlend_SkinnedMeshesShareTheirPlacedArmatureWorlds()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var importerPath = Path.Combine(
            AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath)
            || !File.Exists(helperPath)
            || !File.Exists(importerPath))
        {
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to blender.exe (and ensure "
                + "BlenderExporter/import_package.py is copied beside the test binary) "
                + "to run this Blender placement regression.");
        }

        using var temp = new TempDirectory();
        var document = CreateTwoCopyDocument();
        var export = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            OutputStem = "placed_skeleton_copies",
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath
        });

        var report = InspectPlacedBlend(
            helperPath!, Assert.Single(export.OutputPaths), temp.Path);
        var sourceCentroid = new Vector3(1f / 3f, 1f / 3f, 0f);
        var expected = new Dictionary<string, Vector3>(StringComparer.Ordinal)
        {
            ["first_node"] = ToBlenderWorld(
                Vector3.Transform(sourceCentroid, FirstRootTransform)),
            ["second_node"] = ToBlenderWorld(
                Vector3.Transform(sourceCentroid, SecondRootTransform))
        };

        Assert.Equal(expected.Keys.Order(), report.Keys.Order());
        foreach (var (name, expectedCentroid) in expected)
        {
            var placed = report[name];
            AssertVectorClose(expectedCentroid, placed.Centroid, 2e-5f);
            AssertMatrixClose(placed.ArmatureWorld, placed.MeshWorld);
        }

        Assert.True(
            Vector3.Distance(report["first_node"].Centroid, report["second_node"].Centroid)
            > 1f,
            "Distinct skeleton roots must produce distinct placed mesh centroids.");
    }

    private static ModelDocument CreateTwoCopyDocument()
    {
        var document = new ModelDocument { Name = "placed_skeleton_copies" };
        document.Materials.Add(new RenderMaterial { Name = "mat", BaseColor = Vector4.One });

        AddCopy(document, "first", FirstRootTransform);
        AddCopy(document, "second", SecondRootTransform);

        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f);
        var values = new[]
        {
            0f, 0f, 0f, 1f,
            rotation.X, rotation.Y, rotation.Z, rotation.W
        };
        var animation = new ModelAnimation { Name = "local_turn" };
        for (var skeletonIndex = 0; skeletonIndex < 2; skeletonIndex++)
        {
            animation.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = skeletonIndex,
                BoneIndex = 0,
                Property = ModelAnimationProperty.Rotation,
                Times = [0f, 1f],
                Values = values.ToArray()
            });
        }

        document.Animations.Add(animation);
        return document;
    }

    private static void AddCopy(ModelDocument document, string name, Matrix4x4 rootTransform)
    {
        var skeletonIndex = document.Skeletons.Count;
        var skeleton = new ModelSkeleton
        {
            Name = name,
            RootTransform = rootTransform
        };
        skeleton.Bones.Add(new ModelBone
        {
            Name = $"{name}_joint",
            ParentIndex = -1,
            LocalTransform = Matrix4x4.Identity,
            InverseBindMatrix = Matrix4x4.Identity
        });
        document.Skeletons.Add(skeleton);

        var mesh = new ModelMesh { Name = $"{name}_mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = $"{name}_primitive",
            MaterialIndex = 0,
            Vertices =
            [
                new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
                new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
                new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
            ],
            Indices = [0, 1, 2],
            Skin = new ModelSkinBinding
            {
                SkeletonIndex = skeletonIndex,
                Influences =
                [
                    ModelBoneInfluences.Single(0),
                    ModelBoneInfluences.Single(0),
                    ModelBoneInfluences.Single(0)
                ]
            }
        });
        var meshIndex = document.Meshes.Count;
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = $"{name}_node",
            MeshIndex = meshIndex
        });
    }

    private static Matrix4x4 ReadMatrix(JsonElement element)
    {
        var values = element.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
        Assert.Equal(16, values.Length);
        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static void AssertQuaternionClose(Quaternion expected, Quaternion actual)
    {
        var distance = MathF.Min(
            QuaternionDistance(expected, actual),
            QuaternionDistance(expected, Quaternion.Negate(actual)));
        Assert.InRange(distance, 0f, 1e-6f);
    }

    private static float QuaternionDistance(Quaternion left, Quaternion right) =>
        MathF.Sqrt(
            MathF.Pow(left.X - right.X, 2)
            + MathF.Pow(left.Y - right.Y, 2)
            + MathF.Pow(left.Z - right.Z, 2)
            + MathF.Pow(left.W - right.W, 2));

    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = MatrixValues(expected);
        var actualValues = MatrixValues(actual);
        for (var i = 0; i < expectedValues.Length; i++)
            Assert.Equal(expectedValues[i], actualValues[i], 5);
    }

    private static float[] MatrixValues(Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44
    ];

    private static Vector3 ToBlenderWorld(Vector3 source) =>
        new(source.X, -source.Z, source.Y);

    private static void AssertVectorClose(
        Vector3 expected,
        Vector3 actual,
        float tolerance)
    {
        var distance = Vector3.Distance(expected, actual);
        Assert.True(distance <= tolerance,
            $"Expected {expected}, got {actual} (distance {distance}).");
    }

    private static Dictionary<string, PlacedBlendObject> InspectPlacedBlend(
        string helperPath,
        string blendPath,
        string tempDirectory)
    {
        var scriptPath = Path.Combine(tempDirectory, "inspect_placed_skeletons.py");
        var reportPath = Path.Combine(tempDirectory, "placed_skeletons.json");
        File.WriteAllText(scriptPath, InspectPlacedSkeletonsScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var argument in new[]
                 {
                     "-b", "--factory-startup", "--python", scriptPath, "--",
                     blendPath, reportPath
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), "Failed to start Blender for placed-rig inspection.");
        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(File.Exists(reportPath),
            $"Blender placement inspection produced no report (exit {process.ExitCode})."
            + $"{Environment.NewLine}{stderr}");

        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        return json.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => new PlacedBlendObject(
                ReadVector(property.Value.GetProperty("centroid")),
                ReadBlenderMatrix(property.Value.GetProperty("meshWorld")),
                ReadBlenderMatrix(property.Value.GetProperty("armatureWorld"))),
            StringComparer.Ordinal);
    }

    private static Vector3 ReadVector(JsonElement values) =>
        new(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle());

    private static Matrix4x4 ReadBlenderMatrix(JsonElement values)
    {
        var matrix = values.EnumerateArray().Select(static value => value.GetSingle()).ToArray();
        Assert.Equal(16, matrix.Length);
        return new Matrix4x4(
            matrix[0], matrix[4], matrix[8], matrix[12],
            matrix[1], matrix[5], matrix[9], matrix[13],
            matrix[2], matrix[6], matrix[10], matrix[14],
            matrix[3], matrix[7], matrix[11], matrix[15]);
    }

    private readonly record struct PlacedBlendObject(
        Vector3 Centroid,
        Matrix4x4 MeshWorld,
        Matrix4x4 ArmatureWorld);

    private const string InspectPlacedSkeletonsScript = """
import bpy, json, sys

argv = sys.argv[sys.argv.index("--") + 1:]
blend_path, report_path = argv[0], argv[1]
bpy.ops.wm.open_mainfile(filepath=blend_path)
scene = bpy.context.scene
scene.frame_set(0)
bpy.context.view_layer.update()
depsgraph = bpy.context.evaluated_depsgraph_get()
report = {}

for obj in scene.objects:
    if obj.type != "MESH" or obj.name not in ("first_node", "second_node"):
        continue
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    world = evaluated.matrix_world
    points = [world @ vertex.co for vertex in mesh.vertices]
    centroid = sum(points, points[0] * 0.0) / len(points)
    parent = obj.parent
    report[obj.name] = {
        "centroid": list(centroid),
        "meshWorld": [value for row in obj.matrix_world for value in row],
        "armatureWorld": [value for row in parent.matrix_world for value in row],
    }
    evaluated.to_mesh_clear()

with open(report_path, "w", encoding="utf-8") as handle:
    json.dump(report, handle)
""";

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtSkeletonRoot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Best-effort cleanup; Blender can briefly retain handles.
            }
        }
    }
}
