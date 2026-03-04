using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Psx;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     Writes parsed RenderWare DFF (Clump) data to glTF 2.0 (.glb) files.
///     Used for THPS3 PS2 .SKN mesh files.
///     Supports both rigid meshes and skinned meshes (via Skin PLG bone data).
/// </summary>
public static class RwDffGltfWriter
{
    /// <summary>
    ///     Resolves a texture name to PNG bytes for embedding in glTF.
    ///     Returns null if the texture cannot be resolved.
    /// </summary>
    public delegate byte[]? TextureProvider(string textureName);

    /// <summary>
    ///     Writes a parsed DFF clump to a .glb file.
    ///     Automatically detects skinned meshes (Skin PLG) and outputs with skeleton.
    /// </summary>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(RwDffClump clump, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<string, MaterialBuilder>(StringComparer.OrdinalIgnoreCase);
        var totalTriangles = 0;

        // Build frame hierarchy as NodeBuilder tree
        var frameNodes = BuildFrameHierarchy(clump.Frames);

        // Check if any Atomic has skin data → skinned mesh path
        var skinnedAtomic = clump.Atomics.FirstOrDefault(a => a.SkinData != null);
        if (skinnedAtomic?.SkinData != null && frameNodes.Length >= skinnedAtomic.SkinData.NumBones)
        {
            totalTriangles = WriteSkinned(clump, frameNodes, skinnedAtomic.SkinData,
                materialCache, textureProvider, scene);
        }
        else
        {
            // Rigid mesh path: each Atomic links a frame to a geometry
            foreach (var atomic in clump.Atomics)
            {
                if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                    continue;

                var geometry = clump.Geometries[atomic.GeometryIndex];
                if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                    continue;

                var frameNode = atomic.FrameIndex >= 0 && atomic.FrameIndex < frameNodes.Length
                    ? frameNodes[atomic.FrameIndex]
                    : new NodeBuilder($"atomic_{atomic.GeometryIndex}");

                var gltfMesh = BuildMesh(geometry, materialCache, textureProvider,
                    $"geom_{atomic.GeometryIndex}");

                totalTriangles += geometry.Triangles.Length;
                scene.AddRigidMesh(gltfMesh, frameNode);
            }
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    /// <summary>
    ///     Writes skinned DFF mesh with skeleton to glTF.
    ///     Merges all skinned Atomics into a single mesh with joint/weight attributes.
    ///     Builds bone hierarchy from Skin PLG push/pop flags (frame hierarchy is flat in THPS3 DFF).
    /// </summary>
    private static int WriteSkinned(RwDffClump clump, NodeBuilder[] frameNodes,
        RwSkinData skinRef, Dictionary<string, MaterialBuilder> materialCache,
        TextureProvider? textureProvider, SceneBuilder scene)
    {
        // Build joint nodes from Skin PLG bone data.
        // The frame hierarchy in THPS3 DFF is flat (most bones are direct children of the pelvis)
        // with nearly identical world transforms. We CANNOT use it for joints because glTF requires
        // jointMatrix = IBM * globalNodeTransform = Identity at bind pose.
        // Instead: derive each joint's global bind-pose transform as inverse(IBM), then compute
        // local transforms from the bone hierarchy encoded in push/pop flags.
        var boneNodes = BuildBoneHierarchy(skinRef);
        var joints = new (NodeBuilder Joint, Matrix4x4 InverseBindMatrix)[skinRef.NumBones];
        for (var i = 0; i < skinRef.NumBones; i++)
            joints[i] = (boneNodes[i], skinRef.Bones[i].InverseBindMatrix);

        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>("skinned_mesh");
        var totalTriangles = 0;

        foreach (var atomic in clump.Atomics)
        {
            if (atomic.GeometryIndex < 0 || atomic.GeometryIndex >= clump.Geometries.Length)
                continue;

            var geometry = clump.Geometries[atomic.GeometryIndex];
            if (geometry.Vertices.Length == 0 || geometry.Triangles.Length == 0)
                continue;

            // Use this Atomic's own SkinData if available, otherwise fall back to reference
            var skin = atomic.SkinData ?? skinRef;

            // Group triangles by material
            var trisByMaterial = new Dictionary<int, List<RwTriangle>>();
            foreach (var tri in geometry.Triangles)
            {
                if (!trisByMaterial.TryGetValue(tri.MaterialIndex, out var list))
                {
                    list = [];
                    trisByMaterial[tri.MaterialIndex] = list;
                }

                list.Add(tri);
            }

            foreach (var (matIndex, triangles) in trisByMaterial)
            {
                var material = matIndex >= 0 && matIndex < geometry.Materials.Length
                    ? GetOrCreateMaterial(geometry.Materials[matIndex], materialCache, textureProvider)
                    : GetDefaultMaterial(materialCache);

                var prim = gltfMesh.UsePrimitive(material);

                foreach (var tri in triangles)
                {
                    var va = MakeSkinnedVertex(geometry, tri.V0, skin);
                    var vb = MakeSkinnedVertex(geometry, tri.V1, skin);
                    var vc = MakeSkinnedVertex(geometry, tri.V2, skin);
                    prim.AddTriangle(va, vb, vc);
                }

                totalTriangles += triangles.Count;
            }
        }

        if (totalTriangles > 0)
            scene.AddSkinnedMesh(gltfMesh, joints);

        return totalTriangles;
    }

    private static NodeBuilder[] BuildFrameHierarchy(RwFrame[] frames)
    {
        var nodes = new NodeBuilder[frames.Length];

        for (var i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            var name = $"frame_{i}";

            if (frame.ParentIndex >= 0 && frame.ParentIndex < i && nodes[frame.ParentIndex] != null)
            {
                nodes[i] = nodes[frame.ParentIndex].CreateNode(name);
            }
            else
            {
                nodes[i] = new NodeBuilder(name);
            }

            // Set local transform from frame matrix
            nodes[i].LocalMatrix = frame.LocalTransform;
        }

        return nodes;
    }

    /// <summary>
    ///     Builds bone hierarchy from Skin PLG push/pop flags and derives local transforms from IBMs.
    ///     Flags encode a pre-order DFS traversal: bit 1 (PUSH) = save parent before descending,
    ///     bit 0 (POP) = restore parent after this bone. Upper bits (e.g. 0x08) are ignored.
    ///     Each bone's bind-pose global transform = inverse(IBM). Local transforms are computed
    ///     relative to the parent so that jointMatrix = IBM * globalTransform = Identity at bind pose.
    /// </summary>
    private static NodeBuilder[] BuildBoneHierarchy(RwSkinData skin)
    {
        // Step 1: Reconstruct parent indices from push/pop flags (HAnim DFS encoding)
        var parentIndex = new int[skin.NumBones];
        var parentStack = new Stack<int>();
        var currentParent = -1;

        for (var i = 0; i < skin.NumBones; i++)
        {
            parentIndex[i] = currentParent;

            var flags = skin.Bones[i].Flags & 0x03; // mask to push/pop bits
            if ((flags & 0x02) != 0) // PUSH: save current parent before descending
                parentStack.Push(currentParent);

            currentParent = i; // this bone becomes the new parent

            if ((flags & 0x01) != 0) // POP: restore parent (end of branch)
                currentParent = parentStack.Count > 0 ? parentStack.Pop() : -1;
        }

        // Step 2: Compute global bind-pose transforms from IBMs
        var globalTransform = new Matrix4x4[skin.NumBones];
        for (var i = 0; i < skin.NumBones; i++)
        {
            if (!Matrix4x4.Invert(skin.Bones[i].InverseBindMatrix, out var bindPose))
                bindPose = Matrix4x4.Identity;
            globalTransform[i] = bindPose;
        }

        // Step 3: Z-up → Y-up rotation root (RW model data uses Z-up, glTF expects Y-up).
        // Parenting bones under this node makes all joint matrices = R instead of Identity,
        // rotating the entire skinned mesh from Z-up to Y-up without modifying vertex data.
        // Row-vector: (x,y,z) * R = (x, z, -y)
        var rotationRoot = new NodeBuilder("skeleton_root");
        rotationRoot.LocalMatrix = new Matrix4x4(
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1);

        // Step 4: Build NodeBuilder tree with correct local transforms
        var nodes = new NodeBuilder[skin.NumBones];
        for (var i = 0; i < skin.NumBones; i++)
        {
            var name = $"bone_{i}";
            var parent = parentIndex[i];

            if (parent >= 0 && parent < i && nodes[parent] != null)
            {
                nodes[i] = nodes[parent].CreateNode(name);
                // Row-vector convention: global = local * parentGlobal
                // So: local = global * inverse(parentGlobal)
                if (Matrix4x4.Invert(globalTransform[parent], out var invParent))
                    nodes[i].LocalMatrix = SanitizeAffine(globalTransform[i] * invParent);
                else
                    nodes[i].LocalMatrix = SanitizeAffine(globalTransform[i]);
            }
            else
            {
                // Root bones: parent under rotation node (local transform unchanged)
                nodes[i] = rotationRoot.CreateNode(name);
                nodes[i].LocalMatrix = SanitizeAffine(globalTransform[i]);
            }
        }

        return nodes;
    }

    /// <summary>
    ///     Forces column 4 of a matrix to (0,0,0,1) to ensure it's a valid affine transform.
    ///     Matrix inversion/multiplication can introduce floating-point drift in column 4,
    ///     especially with near-degenerate rotation matrices (e.g. Bird_A bone 0).
    /// </summary>
    private static Matrix4x4 SanitizeAffine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> BuildMesh(
        RwGeometry geometry,
        Dictionary<string, MaterialBuilder> materialCache,
        TextureProvider? textureProvider,
        string meshName)
    {
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(meshName);

        // Group triangles by material index
        var trisByMaterial = new Dictionary<int, List<RwTriangle>>();
        foreach (var tri in geometry.Triangles)
        {
            if (!trisByMaterial.TryGetValue(tri.MaterialIndex, out var list))
            {
                list = [];
                trisByMaterial[tri.MaterialIndex] = list;
            }

            list.Add(tri);
        }

        foreach (var (matIndex, triangles) in trisByMaterial)
        {
            var material = matIndex >= 0 && matIndex < geometry.Materials.Length
                ? GetOrCreateMaterial(geometry.Materials[matIndex], materialCache, textureProvider)
                : GetDefaultMaterial(materialCache);

            var prim = gltfMesh.UsePrimitive(material);

            foreach (var tri in triangles)
            {
                var va = MakeVertex(geometry, tri.V0);
                var vb = MakeVertex(geometry, tri.V1);
                var vc = MakeVertex(geometry, tri.V2);
                prim.AddTriangle(va, vb, vc);
            }
        }

        return gltfMesh;
    }

    private static VERTEX MakeVertex(RwGeometry geometry, int index)
    {
        var pos = index < geometry.Vertices.Length
            ? geometry.Vertices[index]
            : Vector3.Zero;

        var normal = Vector3.UnitY;
        if (geometry.Normals != null && index < geometry.Normals.Length)
        {
            var n = geometry.Normals[index];
            var len = n.Length();
            normal = len > 0.001f ? n / len : Vector3.UnitY;
        }

        // Vertex colors: RW uses 0-255 range, normalize to 0-1
        var color = Vector4.One;
        if (geometry.Colors != null && index < geometry.Colors.Length)
        {
            var c = geometry.Colors[index];
            color = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }

        // UVs: already V-flipped during parsing
        var uv = Vector2.Zero;
        if (geometry.UVs != null && index < geometry.UVs.Length)
            uv = geometry.UVs[index];

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(color, uv));
    }

    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>
        MakeSkinnedVertex(RwGeometry geometry, int index, RwSkinData skin)
    {
        var pos = index < geometry.Vertices.Length
            ? geometry.Vertices[index]
            : Vector3.Zero;

        var normal = Vector3.UnitY;
        if (geometry.Normals != null && index < geometry.Normals.Length)
        {
            var n = geometry.Normals[index];
            var len = n.Length();
            normal = len > 0.001f ? n / len : Vector3.UnitY;
        }

        var color = Vector4.One;
        if (geometry.Colors != null && index < geometry.Colors.Length)
        {
            var c = geometry.Colors[index];
            color = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }

        var uv = Vector2.Zero;
        if (geometry.UVs != null && index < geometry.UVs.Length)
            uv = geometry.UVs[index];

        // Joint/weight data from Skin PLG (4 influences per vertex)
        var baseIdx = index * 4;
        var skinning = baseIdx + 3 < skin.BoneIndices.Length
            ? new VertexJoints4(
                (skin.BoneIndices[baseIdx], skin.BoneWeights[baseIdx]),
                (skin.BoneIndices[baseIdx + 1], skin.BoneWeights[baseIdx + 1]),
                (skin.BoneIndices[baseIdx + 2], skin.BoneWeights[baseIdx + 2]),
                (skin.BoneIndices[baseIdx + 3], skin.BoneWeights[baseIdx + 3]))
            : new VertexJoints4((0, 1f));

        return new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(color, uv),
            skinning);
    }

    private static MaterialBuilder GetOrCreateMaterial(
        RwMaterial material,
        Dictionary<string, MaterialBuilder> cache,
        TextureProvider? textureProvider)
    {
        var key = material.TextureName ?? $"mat_{material.R}_{material.G}_{material.B}_{material.A}";

        if (cache.TryGetValue(key, out var existing))
            return existing;

        var builder = new MaterialBuilder(key)
            .WithUnlitShader()
            .WithDoubleSide(true);

        // Set base color from material color
        var baseColor = new Vector4(
            material.R / 255f,
            material.G / 255f,
            material.B / 255f,
            material.A / 255f);
        builder.WithBaseColor(baseColor);

        // Embed texture if available
        if (textureProvider != null && !string.IsNullOrEmpty(material.TextureName))
        {
            var pngBytes = textureProvider(material.TextureName);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);
            }
        }

        // Alpha handling
        if (material.A < 255)
            builder.WithAlpha(AlphaMode.BLEND);

        cache[key] = builder;
        return builder;
    }

    private static MaterialBuilder GetDefaultMaterial(Dictionary<string, MaterialBuilder> cache)
    {
        const string key = "__default__";
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var builder = new MaterialBuilder(key)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    ///     Builds a texture provider from an RW TXD file parsed via RwTxdFile.
    ///     Resolves texture names to PNG bytes using the parsed texture dictionary.
    /// </summary>
    public static TextureProvider BuildTxdTextureProvider(Ps2TexResult txdResult)
    {
        // Build name → texture lookup (RW TXD textures have Name set during parsing)
        var lookup = new Dictionary<string, Ps2Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var tex in txdResult.Textures)
        {
            if (tex.Pixels != null && tex.Name != null)
                lookup.TryAdd(tex.Name, tex);
        }

        return textureName =>
        {
            // Try exact match first, then strip file extension (TXD stores names without extension)
            if (!lookup.TryGetValue(textureName, out var tex))
            {
                var extIdx = textureName.LastIndexOf('.');
                if (extIdx <= 0 || !lookup.TryGetValue(textureName[..extIdx], out tex))
                    return null;
            }

            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels!);
        };
    }
}
