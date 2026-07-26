using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     End-to-end guard for the v1.3.4 PSX Blender-export fixes in
///     <c>BlenderExporter/import_package.py</c>: grounded-animation persistence
///     (all clips survive), metres-normalising import scale, and the
///     limb-stretch (double-translation) correction. Runs Blender headlessly on
///     a synthetic PSX skinned+animated document and re-opens the saved .blend
///     to measure the results.
///
///     Gated on <c>NEVERSOFT_BLENDER_HELPER</c> (path to blender.exe) so it
///     self-skips in CI and default local runs; set it to exercise the fixes.
/// </summary>
public sealed class PsxBlendExportRegressionTests
{
    private const int AnimationCount = 3;

    // The child bone's bind offset from the root, in PSX model units. Large
    // enough that (a) the import downscale is measurable and (b) the
    // double-translation bug would offset the mesh by more than its own size.
    private static readonly Vector3 ChildLocalBind = new(0f, 100f, 0f);

    [Fact]
    public void Export_Blend_Psx_PersistsAllAnimations_Scales_AndUnstretchesLimbs()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(scriptPath))
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to blender.exe (and ensure BlenderExporter/import_package.py " +
                "is copied next to the test binary) to run this Blender round-trip smoke test.");

        using var temp = new TempDirectory();
        var document = CreatePsxSkinnedAnimatedDocument();

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Blend,
                BlenderHelperPath = helperPath
            });

        var blendPath = Assert.Single(result.OutputPaths);
        Assert.True(File.Exists(blendPath));

        var report = InspectBlend(helperPath!, blendPath, temp.Path);

        // Bug 3 — every selected animation must persist in the saved .blend
        // (previously only the last-assigned action survived the save).
        Assert.Equal(AnimationCount, report.Actions);

        // Bug 2 — PSX model units (~100) must be downscaled to a metres-sized
        // rig on import (the ~80-unit mesh becomes ~1.6, not ~80).
        Assert.True(report.BindMax < 5.0,
            $"Expected the PSX import downscale (bind max ~1.6), got {report.BindMax:F3}.");

        // Bug 1 — an animation that reproduces the BIND pose must leave the mesh
        // where it rests. The double-translation bug offset it by the child's
        // bind local translation (~2 units after scaling, larger than the mesh
        // itself); the fix keeps posed == bind.
        var offset = Distance(report.PosedCenter, report.BindCenter);
        Assert.True(offset < 0.2 * report.BindMax,
            $"Limb stretch: a bind-reproducing clip moved the mesh {offset:F3} " +
            $"(bind size {report.BindMax:F3}); expected ~0.");
    }

    private static ModelDocument CreatePsxSkinnedAnimatedDocument()
    {
        var document = new ModelDocument
        {
            Name = "psx_super",
            SourceKind = ModelSourceKind.Psx
        };
        document.Materials.Add(new RenderMaterial { Name = "mat", BaseColor = Vector4.One });

        // Root at origin; child offset by ChildLocalBind. InverseBindMatrix is
        // the inverse of the accumulated world bind (translation-only, as PSX
        // BuildPsxSkeleton emits).
        var skeleton = new ModelSkeleton { Name = "skeleton" };
        skeleton.Bones.Add(new ModelBone
        {
            Name = "root",
            ParentIndex = -1,
            LocalTransform = Matrix4x4.Identity,
            InverseBindMatrix = Matrix4x4.Identity
        });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "child",
            ParentIndex = 0,
            LocalTransform = Matrix4x4.CreateTranslation(ChildLocalBind),
            InverseBindMatrix = Matrix4x4.CreateTranslation(-ChildLocalBind)
        });
        document.Skeletons.Add(skeleton);

        // A quad at the child's bind location spanning ~80 units, weighted 100%
        // to the child so it tracks the child bone exactly.
        var verts = new[]
        {
            new ModelVertex(new Vector3(0f, 100f, 0f), Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new ModelVertex(new Vector3(80f, 100f, 0f), Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new ModelVertex(new Vector3(80f, 180f, 0f), Vector3.UnitZ, Vector4.One, Vector2.One),
            new ModelVertex(new Vector3(0f, 180f, 0f), Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        };
        var childInfluence = new ModelBoneInfluences(1, 0, 0, 0, 1f, 0f, 0f, 0f);
        var influences = new[] { childInfluence, childInfluence, childInfluence, childInfluence };

        var mesh = new ModelMesh { Name = "mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "prim",
            MaterialIndex = 0,
            Vertices = verts,
            Indices = [0, 1, 2, 0, 2, 3],
            Skin = new ModelSkinBinding { SkeletonIndex = 0, Influences = influences }
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode { Name = "node", MeshIndex = 0, Transform = Matrix4x4.Identity });

        // N animations, each reproducing the bind pose on the child bone: a
        // Translation channel whose value IS the child's bind local translation
        // (glTF absolute-local semantics) plus an identity Rotation. Reproducing
        // the bind must not move the mesh — the pre-fix double-count did.
        for (var a = 0; a < AnimationCount; a++)
        {
            var anim = new ModelAnimation { Name = $"clip_{a}" };
            anim.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = 0,
                BoneIndex = 1,
                Property = ModelAnimationProperty.Translation,
                Times = [0f, 1f],
                Values =
                [
                    ChildLocalBind.X, ChildLocalBind.Y, ChildLocalBind.Z,
                    ChildLocalBind.X, ChildLocalBind.Y, ChildLocalBind.Z
                ]
            });
            anim.Channels.Add(new ModelAnimationChannel
            {
                SkeletonIndex = 0,
                BoneIndex = 1,
                Property = ModelAnimationProperty.Rotation,
                Times = [0f, 1f],
                Values = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f] // identity quaternion (X,Y,Z,W)
            });
            document.Animations.Add(anim);
        }

        return document;
    }

    private static BlendReport InspectBlend(string helperPath, string blendPath, string tempDir)
    {
        var scriptPath = Path.Combine(tempDir, "inspect_blend.py");
        var reportPath = Path.Combine(tempDir, "report.json");
        File.WriteAllText(scriptPath, InspectScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        foreach (var arg in new[] { "-b", "--factory-startup", "--python", scriptPath, "--", blendPath, reportPath })
            process.StartInfo.ArgumentList.Add(arg);

        Assert.True(process.Start(), "Failed to start Blender for .blend inspection.");
        var stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.True(File.Exists(reportPath),
            $"Blender inspection produced no report (exit {process.ExitCode}).{Environment.NewLine}{stderr}");

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = doc.RootElement;
        return new BlendReport(
            root.GetProperty("actions").GetInt32(),
            root.GetProperty("bindMax").GetSingle(),
            ReadVector(root.GetProperty("bindCenter")),
            ReadVector(root.GetProperty("posedCenter")));
    }

    private static Vector3 ReadVector(JsonElement array) =>
        new(array[0].GetSingle(), array[1].GetSingle(), array[2].GetSingle());

    private static float Distance(Vector3 a, Vector3 b) => (a - b).Length();

    private readonly record struct BlendReport(int Actions, float BindMax, Vector3 BindCenter, Vector3 PosedCenter);

    // Opens the saved .blend and reports: action count, the raw (bind) mesh
    // bounds, and the depsgraph-evaluated (posed) mesh centre for clip_0 — all
    // in Blender world space. Writes JSON to argv[1].
    private const string InspectScript = """
import bpy, sys, json

argv = sys.argv[sys.argv.index("--") + 1:]
blend_path, out_path = argv[0], argv[1]
bpy.ops.wm.open_mainfile(filepath=blend_path)
scene = bpy.context.scene


def bounds(evaluated):
    lo, hi = [1e30] * 3, [-1e30] * 3
    dg = bpy.context.evaluated_depsgraph_get() if evaluated else None
    for obj in scene.objects:
        if obj.type != "MESH":
            continue
        src = obj.evaluated_get(dg) if evaluated else obj
        me = src.to_mesh() if evaluated else src.data
        mw = src.matrix_world
        for v in me.vertices:
            w = mw @ v.co
            for i in range(3):
                lo[i] = min(lo[i], w[i]); hi[i] = max(hi[i], w[i])
        if evaluated:
            src.to_mesh_clear()
    return lo, hi


blo, bhi = bounds(False)
bind_center = [(blo[i] + bhi[i]) / 2.0 for i in range(3)]
bind_max = max(bhi[i] - blo[i] for i in range(3))

arm = next((o for o in scene.objects if o.type == "ARMATURE"), None)
act = next((a for a in bpy.data.actions if "clip_0" in a.name), None)
if arm and act:
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = act
    try:
        if hasattr(act, "slots") and len(act.slots):
            ad.action_slot = act.slots[0]
    except Exception:
        pass
scene.frame_set(12)
plo, phi = bounds(True)
posed_center = [(plo[i] + phi[i]) / 2.0 for i in range(3)]

with open(out_path, "w") as f:
    json.dump({
        "actions": len(bpy.data.actions),
        "bindMax": bind_max,
        "bindCenter": bind_center,
        "posedCenter": posed_center,
    }, f)
""";

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtPsxBlend_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
