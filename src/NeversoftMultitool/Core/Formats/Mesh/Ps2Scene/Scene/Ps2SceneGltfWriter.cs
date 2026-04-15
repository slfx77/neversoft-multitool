using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes parsed PS2 scene data (MDL/SKIN) to glTF 2.0 (.glb) files.
///     Uses ADC-based triangle strip conversion: each vertex's IsStripRestart flag
///     controls whether a triangle is drawn (ADC=0 → draw, ADC=0x8000 → skip).
///     GEOM writing is handled by <see cref="Ps2GeomGltfWriter" />.
/// </summary>
public static class Ps2SceneGltfWriter
{
    /// <summary>
    ///     Delegate that resolves a texture checksum to PNG bytes for embedding in glTF.
    ///     Returns null if the texture cannot be resolved.
    /// </summary>
    public delegate byte[]? TextureProvider(uint textureChecksum);

    /// <summary>
    ///     Minimum FIX value (0-128 scale) at or above which FixBlend materials are treated
    ///     as OPAQUE instead of BLEND. 96/128 = 75% opacity. High-FIX materials are nearly
    ///     opaque; marking them BLEND causes z-sorting artifacts in glTF viewers that don't
    ///     depth-sort transparent geometry.
    /// </summary>
    internal const int FixBlendOpaqueThreshold = 96;

    /// <summary>
    ///     Writes a parsed PS2 scene to a .glb file.
    /// </summary>
    /// <param name="ps2Scene">Parsed PS2 scene data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="textureProvider">Optional callback to resolve texture checksums to PNG bytes.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(Ps2Scene ps2Scene, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = Build(ps2Scene, textureProvider);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(Ps2Scene ps2Scene,
        TextureProvider? textureProvider = null)
    {
        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;
        var dedupByMaterial = new Dictionary<uint, HashSet<(Vector3, Vector3, Vector3)>>();

        foreach (var group in ps2Scene.MeshGroups)
        {
            if (group.Meshes.Count == 0) continue;

            var groupName = QbKey.QbKey.TryResolve(group.Checksum) ?? $"group_{group.Checksum:X8}";

            foreach (var mesh in group.Meshes)
            {
                if (mesh.Vertices.Length < 3) continue;

                if (!dedupByMaterial.TryGetValue(mesh.MaterialChecksum, out var dedup))
                {
                    dedup = new HashSet<(Vector3, Vector3, Vector3)>();
                    dedupByMaterial[mesh.MaterialChecksum] = dedup;
                }

                var gltfMesh = BuildMesh(groupName, mesh,
                    ps2Scene.Materials, materialCache, textureProvider, out var tris, dedup);
                if (tris == 0) continue;

                totalTriangles += tris;
                var node = new NodeBuilder(groupName);
                scene.AddRigidMesh(gltfMesh, node);
            }
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Writes a parsed PS2 scene with skeleton to a skinned .glb file.
    ///     Creates bone hierarchy, JOINTS_0/WEIGHTS_0 vertex attributes, and inverse bind matrices.
    /// </summary>
    public static int WriteSkinned(Ps2Scene ps2Scene, Ps2Skeleton skeleton, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = BuildSkinned(ps2Scene, skeleton, textureProvider);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    /// <summary>
    ///     Writes a skinned mesh with animation to a .glb file.
    /// </summary>
    internal static int WriteSkinnedAnimated(Ps2Scene ps2Scene, Ps2Skeleton skeleton,
        SkaAnimation animation, string outputPath, string? animationName = null,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = BuildSkinnedAnimated(ps2Scene, skeleton, animation,
            animationName, textureProvider);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) BuildSkinnedAnimated(
        Ps2Scene ps2Scene, Ps2Skeleton skeleton, SkaAnimation animation,
        string? animationName = null, TextureProvider? textureProvider = null)
    {
        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;

        // Build skeleton with animation channels
        var jointNodes = BuildJointNodes(skeleton);
        SkaGltfWriter.ApplyAnimation(jointNodes, skeleton, animation, animationName ?? "animation");

        // Build skinned mesh
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>("skinned_mesh");
        foreach (var mesh in ps2Scene.MeshGroups.SelectMany(g => g.Meshes))
        {
            if (mesh.Vertices.Length < 3) continue;
            var material = GetOrCreateMaterial(ps2Scene.Materials, mesh.MaterialChecksum,
                materialCache, textureProvider);
            var prim = gltfMesh.UsePrimitive(material);
            var tris = Ps2SceneGltfSkinningSupport.AddSkinnedTriangleStrip(prim, mesh.Vertices,
                mesh.StartsOnOddOutputSlot);
            totalTriangles += tris;
        }

        if (totalTriangles > 0)
        {
            var joints = new (NodeBuilder, Matrix4x4)[skeleton.Bones.Length];
            for (var i = 0; i < skeleton.Bones.Length; i++)
                joints[i] = (jointNodes[i], skeleton.Bones[i].InverseBindMatrix);
            scene.AddSkinnedMesh(gltfMesh, joints);
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>Build joint nodes from skeleton (shared between animated and static paths).</summary>
    private static NodeBuilder[] BuildJointNodes(Ps2Skeleton skeleton)
    {
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = QbKey.QbKey.TryResolve(bone.NameChecksum) ?? $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
                jointNodes[i] = new NodeBuilder(boneName);
            else
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);

            jointNodes[i].LocalTransform = new AffineTransform(
                null, bone.LocalRotation, bone.LocalTranslation);
        }
        return jointNodes;
    }

    internal static (ModelRoot Model, int Triangles) BuildSkinned(Ps2Scene ps2Scene,
        Ps2Skeleton skeleton, TextureProvider? textureProvider = null)
    {
        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;

        // Build skeleton hierarchy as NodeBuilder tree
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = QbKey.QbKey.TryResolve(bone.NameChecksum) ?? $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
            {
                // Root bone
                jointNodes[i] = new NodeBuilder(boneName);
            }
            else
            {
                // Child bone — create under parent
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);
            }

            // Set local transform from skeleton neutral pose
            jointNodes[i].LocalTransform = new AffineTransform(
                null, // scale
                bone.LocalRotation,
                bone.LocalTranslation);
        }

        // Build skinned mesh with VertexJoints4
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>("skinned_mesh");

        foreach (var mesh in ps2Scene.MeshGroups.SelectMany(g => g.Meshes))
        {
            if (mesh.Vertices.Length < 3) continue;

            var material = GetOrCreateMaterial(ps2Scene.Materials, mesh.MaterialChecksum,
                materialCache, textureProvider);
            var prim = gltfMesh.UsePrimitive(material);

            var tris = Ps2SceneGltfSkinningSupport.AddSkinnedTriangleStrip(prim, mesh.Vertices,
                mesh.StartsOnOddOutputSlot);
            totalTriangles += tris;
        }

        if (totalTriangles > 0)
            scene.AddSkinnedMesh(gltfMesh, Matrix4x4.Identity, jointNodes);

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Converts ADC-flagged triangle strips to individual triangles.
    ///     Shared between MDL/SKIN and GEOM pipelines.
    /// </summary>
    internal static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        Ps2Vertex[] verts,
        bool startsOnOddOutputSlot = false,
        HashSet<(Vector3, Vector3, Vector3)>? dedup = null,
        float maxTriangleEdgeLength = float.PositiveInfinity,
        bool resetOnRestart = false)
    {
        var count = 0;
        var stripStart = 0;
        var parityBias = startsOnOddOutputSlot ? 1 : 0;
        var lastWasRestart = false;

        for (var i = 0; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart)
            {
                // World-zone GEOM streams use restart-flagged vertices to seed a fresh strip.
                // PS2 ADC pattern: consecutive restart vertices [R, R, n, n, ...] prime a new
                // sub-strip. Only reset stripStart on the FIRST restart in a consecutive sequence
                // so the second restart vertex still counts toward the 2-vertex strip lead-in.
                if (resetOnRestart && !lastWasRestart)
                    stripStart = i;
                lastWasRestart = true;
                continue;
            }

            lastWasRestart = false;

            var localIndex = i - stripStart;
            if (localIndex < 2)
                continue;

            VERTEX va, vb, vc;
            Ps2Vertex pa, pb, pc;
            if (((localIndex + parityBias) & 1) == 0)
            {
                pa = verts[i - 2];
                pb = verts[i - 1];
                pc = verts[i];
                va = MakeVertex(verts[i - 2]);
                vb = MakeVertex(verts[i - 1]);
                vc = MakeVertex(verts[i]);
            }
            else
            {
                pa = verts[i - 1];
                pb = verts[i - 2];
                pc = verts[i];
                va = MakeVertex(verts[i - 1]);
                vb = MakeVertex(verts[i - 2]);
                vc = MakeVertex(verts[i]);
            }

            if (IsDegenerate(pa, pb, pc))
                continue;

            if (!float.IsPositiveInfinity(maxTriangleEdgeLength) &&
                MaxTriangleEdgeLength(pa, pb, pc) > maxTriangleEdgeLength)
            {
                continue;
            }

            if (dedup is not null)
            {
                var key = SortedTriangleKey(pa.Position, pb.Position, pc.Position);
                if (!dedup.Add(key))
                    continue;
            }

            prim.AddTriangle(va, vb, vc);
            count++;
        }

        return count;
    }

    private static float MaxTriangleEdgeLength(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        var ab = Vector3.Distance(a.Position, b.Position);
        var bc = Vector3.Distance(b.Position, c.Position);
        var ca = Vector3.Distance(c.Position, a.Position);
        return Math.Max(ab, Math.Max(bc, ca));
    }

    private static (Vector3, Vector3, Vector3) SortedTriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);

        static int Compare(Vector3 x, Vector3 y)
        {
            var cmp = x.X.CompareTo(y.X);
            if (cmp != 0) return cmp;
            cmp = x.Y.CompareTo(y.Y);
            return cmp != 0 ? cmp : x.Z.CompareTo(y.Z);
        }
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> BuildMesh(
        string name,
        Ps2Mesh mesh,
        List<Ps2Material> materials,
        Dictionary<uint, MaterialBuilder> materialCache,
        TextureProvider? textureProvider,
        out int triangleCount,
        HashSet<(Vector3, Vector3, Vector3)>? dedup = null)
    {
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
        var material = GetOrCreateMaterial(materials, mesh.MaterialChecksum, materialCache, textureProvider);
        var prim = gltfMesh.UsePrimitive(material);

        triangleCount = AddTriangleStrip(prim, mesh.Vertices, mesh.StartsOnOddOutputSlot, dedup);

        return gltfMesh;
    }

    internal static bool IsDegenerate(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        const float epsilon = 1e-8f;

        if (Vector3.DistanceSquared(a.Position, b.Position) <= epsilon ||
            Vector3.DistanceSquared(b.Position, c.Position) <= epsilon ||
            Vector3.DistanceSquared(a.Position, c.Position) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        return cross.LengthSquared() <= epsilon;
    }

    private static VERTEX MakeVertex(Ps2Vertex v)
    {
        var pos = v.Position;
        var normal = Vector3.UnitY;
        if (v.HasNormal)
        {
            var len = v.Normal.Length();
            normal = len > 0.001f ? v.Normal / len : Vector3.UnitY;
        }

        // Vertex colors: PS2 uses 0-128 range (128 = full bright), divide by 128
        var r = Math.Min(v.R / 128f, 1f);
        var g = Math.Min(v.G / 128f, 1f);
        var b = Math.Min(v.B / 128f, 1f);
        var a = Math.Min(v.A / 128f, 1f);

        // UV: flip V. PS2 GS samples bottom-up but our TEX pipeline flips
        // pixel data to top-down PNGs; mesh UVs remain in bottom-up space.
        var uv = v.HasUV ? new Vector2(v.U, 1f - v.V) : Vector2.Zero;

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(new Vector4(r, g, b, a), uv));
    }

    private static MaterialBuilder GetOrCreateMaterial(
        List<Ps2Material> materials,
        uint matChecksum,
        Dictionary<uint, MaterialBuilder> cache,
        TextureProvider? textureProvider)
    {
        if (cache.TryGetValue(matChecksum, out var existing))
            return existing;

        var mat = materials.FirstOrDefault(m => m.Checksum == matChecksum);
        var matName = mat != null
            ? QbKey.QbKey.TryResolve(mat.Checksum) ?? $"mat_{mat.Checksum:X8}"
            : $"mat_{matChecksum:X8}";

        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        // Embed texture if provider is available and material has a texture reference
        if (textureProvider != null && mat?.TextureChecksum is > 0)
        {
            var pngBytes = textureProvider(mat.TextureChecksum);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from material clamp flags.
                // PS2: 0=repeat, non-zero=clamp. glTF defaults to REPEAT.
                if (mat.ClampU || mat.ClampV)
                {
                    var wrapS = mat.ClampU
                        ? TextureWrapMode.CLAMP_TO_EDGE
                        : TextureWrapMode.REPEAT;
                    var wrapT = mat.ClampV
                        ? TextureWrapMode.CLAMP_TO_EDGE
                        : TextureWrapMode.REPEAT;
                    builder.GetChannel(KnownChannel.BaseColor)
                        .Texture
                        .WithSampler(wrapS, wrapT);
                }
            }
        }

        // Alpha handling (from THUG mesh.cpp line 402):
        // PS2 GS always runs alpha test: PackTEST(1,AGEQUAL,Aref,KEEP,0,0,1,ZGEQUAL)
        //   Aref=0: alpha >= 0 always passes → truly OPAQUE.
        //   Aref=1: discards alpha=0 pixels → MASK cutout (fences, foliage, etc.)
        //   Aref>=2: higher-threshold cutout → MASK with visible cutoff.
        // MATFLAG_TRANSPARENT + RegALPHA: true blending (glass, shadows, ghosts).
        // RegALPHA FIXED_BLEND: (Cs-Cd)*FIX/128+Cd → apply FIX/128 as material opacity.
        if (mat != null)
            ApplyAlphaMode(builder, mat);

        cache[matChecksum] = builder;
        return builder;
    }

    /// <summary>
    ///     Apply the correct glTF alpha mode based on PS2 material flags and GS ALPHA register.
    /// </summary>
    private static void ApplyAlphaMode(MaterialBuilder builder, Ps2Material mat)
    {
        var isTransparent = (mat.Flags & (uint)Ps2MaterialFlags.Transparent) != 0;
        if (!isTransparent)
        {
            if (mat.AlphaRef >= 1)
                builder.WithAlpha(AlphaMode.MASK, mat.AlphaRef / 255f);
            return;
        }

        // GS ALPHA blend equation is identity (A==B → numerator zero → output = source).
        // These materials use the Transparent flag for alpha-tested texture cutout (hair,
        // clothing edges) but the blend equation itself is opaque. Use MASK(0.5) to get
        // correct z-ordering while preserving alpha-tested cutout behavior.
        if (mat.IsOpaqueBlend)
        {
            builder.WithAlpha(AlphaMode.MASK);
            return;
        }

        // Check if this is a high-opacity fixed-blend that should be OPAQUE.
        var fixedOpacity = mat.FixedBlendOpacity;
        if (fixedOpacity.HasValue && fixedOpacity.Value >= FixBlendOpaqueThreshold / 128f)
            return;

        builder.WithAlpha(AlphaMode.BLEND);

        // Apply fixed-blend opacity from RegALPHA if available.
        // E.g., ghost models use FIX=50 → 39% opacity.
        if (fixedOpacity.HasValue)
            builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixedOpacity.Value));
    }
}
