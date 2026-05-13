using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

using RIGID_VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;
using SKINNED_VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>;

/// <summary>
///     Writes parsed PSX mesh data to glTF 2.0 (.glb) files.
///     Texture embedding is handled via a callback to keep the mesh and texture pipelines decoupled.
/// </summary>
public static class PsxGltfWriter
{
    /// <summary>
    ///     Writes a parsed PSX file to a .glb file.
    /// </summary>
    /// <param name="psxFile">Parsed PSX mesh data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="textureProvider">Optional callback to resolve texture hashes to PNG bytes.</param>
    /// <param name="pshFile">Optional parsed PSH file for bone names in hierarchical models.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(PsxMeshFile psxFile, string outputPath,
        MeshChecksumTextureResolver? textureProvider = null, PshFile? pshFile = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = Build(psxFile, textureProvider, pshFile);
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) Build(PsxMeshFile psxFile,
        MeshChecksumTextureResolver? textureProvider = null, PshFile? pshFile = null)
    {
        var scene = new SceneBuilder();
        var materials = PsxGltfMaterialFactory.CreateContext(textureProvider);

        var totalTriangles = UsesCombinedCharacterAssembly(psxFile)
            ? WriteSkinned(psxFile, scene, materials, pshFile)
            : WriteFlat(psxFile, scene, materials);

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Writes non-hierarchical models (levels, standalone objects).
    ///     Each object is placed independently at its world-space position.
    /// </summary>
    private static int WriteFlat(
        PsxMeshFile psxFile,
        SceneBuilder scene,
        PsxGltfMaterialContext materials)
    {
        var totalTriangles = 0;

        for (var objIdx = 0; objIdx < psxFile.Objects.Count; objIdx++)
        {
            var obj = psxFile.Objects[objIdx];
            var (gltfMesh, triangles) = BuildObjectMesh(psxFile, obj, materials);
            if (gltfMesh == null) continue;

            totalTriangles += triangles;

            // PS1 is left-handed; glTF is right-handed Y-up. Negate Y and Z.
            var translation = new Vector3(obj.X(psxFile.TranslationDivisor), -obj.Y(psxFile.TranslationDivisor),
                -obj.Z(psxFile.TranslationDivisor));
            scene.AddRigidMesh(gltfMesh, Matrix4x4.CreateTranslation(translation));
        }

        return totalTriangles;
    }

    /// <summary>
    ///     Writes hierarchical/stitch-driven models (characters) as a combined skinned mesh.
    ///     Matches the Blender io_ns_psxtools approach: the mesh is assembled in object order,
    ///     stitched vertices resolve to their source body part, and each body part is weighted
    ///     100% to its matching object bone so the exported glTF includes a skeleton.
    /// </summary>
    private static int WriteSkinned(
        PsxMeshFile psxFile,
        SceneBuilder scene,
        PsxGltfMaterialContext materials,
        PshFile? pshFile)
    {
        var jointNodes = BuildCharacterJointNodes(psxFile, pshFile);
        var (gltfMesh, totalTriangles) = BuildCharacterSkinnedMesh(psxFile, materials);

        if (totalTriangles > 0)
            scene.AddSkinnedMesh(gltfMesh, Matrix4x4.Identity, jointNodes);

        return totalTriangles;
    }

    /// <summary>
    ///     Shared character mesh-construction loop used by both static (<see cref="WriteSkinned" />)
    ///     and animated (<see cref="WriteAnimated" />) paths. Walks the object list in order,
    ///     skips LOD variants, and weights each body part 100% to its matching joint.
    /// </summary>
    private static (MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> Mesh, int Triangles)
        BuildCharacterSkinnedMesh(PsxMeshFile psxFile, PsxGltfMaterialContext materials)
    {
        var lodVariants = BuildLodVariantSet(psxFile);
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>("combined_mesh");
        var totalTriangles = 0;

        for (var objIdx = 0; objIdx < psxFile.Objects.Count; objIdx++)
        {
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objIdx);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count) continue;
            if (lodVariants.Contains(meshIndex)) continue;

            var mesh = psxFile.Meshes[meshIndex];
            if (mesh.Faces.Count == 0) continue;

            totalTriangles += AddSkinnedObjectFaces(gltfMesh, psxFile, objIdx, meshIndex, mesh, materials);
        }

        return (gltfMesh, totalTriangles);
    }

    // ─── Animated writer (PSX character animations) ─────────────────────

    /// <summary>
    ///     Writes a parsed PSX character with one or more named animations to a
    ///     <c>.glb</c> file. Each animation becomes a switchable named glTF track;
    ///     bones with placeholder (all-zero) channels keep their bind pose.
    /// </summary>
    /// <param name="psxFile">Parsed PSX mesh data (must be a character / hierarchical model).</param>
    /// <param name="animations">Named animations to embed.</param>
    /// <param name="outputPath">Output <c>.glb</c> file path.</param>
    /// <param name="textureProvider">Optional callback to resolve texture hashes.</param>
    /// <param name="pshFile">Optional companion <c>.psh</c> for bone names.</param>
    /// <param name="fps">Frame rate used to convert per-frame samples to glTF time (default 30).</param>
    /// <returns>Total number of triangles emitted.</returns>
    public static int WriteAnimated(
        PsxMeshFile psxFile,
        IReadOnlyList<(string Name, PsxAnimation Animation)> animations,
        string outputPath,
        MeshChecksumTextureResolver? textureProvider = null,
        PshFile? pshFile = null,
        PsxAnimationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(psxFile);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var (model, triangles) = BuildAnimated(psxFile, animations, textureProvider, pshFile, options);
        if (triangles == 0) return 0;
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return triangles;
    }

    internal static (ModelRoot Model, int Triangles) BuildAnimated(
        PsxMeshFile psxFile,
        IReadOnlyList<(string Name, PsxAnimation Animation)> animations,
        MeshChecksumTextureResolver? textureProvider = null,
        PshFile? pshFile = null,
        PsxAnimationOptions? options = null)
    {
        if (animations.Count == 0)
            throw new ArgumentException("At least one animation required", nameof(animations));
        var opts = options ?? new PsxAnimationOptions();
        if (opts.Fps <= 0f)
            throw new ArgumentOutOfRangeException(nameof(options), "options.Fps must be positive.");

        var scene = new SceneBuilder();
        var materials = PsxGltfMaterialFactory.CreateContext(textureProvider);

        // Build skeleton with the same bind pose the static path uses.
        var jointNodes = BuildCharacterJointNodes(psxFile, pshFile);

        var (gltfMesh, totalTriangles) = BuildCharacterSkinnedMesh(psxFile, materials);

        // Add the skinned mesh with auto-IBM BEFORE attaching animation curves.
        // SharpGLTF captures the joints' bind-pose world transforms at this
        // moment to compute inverse-bind matrices. If we attach curves first,
        // the joints reflect the first keyframe and IBMs come out wrong.
        if (totalTriangles > 0)
            scene.AddSkinnedMesh(gltfMesh, Matrix4x4.Identity, jointNodes);

        // Now attach animation curves. They override LocalTransform per frame
        // but don't affect the IBMs we already baked.
        foreach (var (name, animation) in animations)
            ApplyPsxAnimation(jointNodes, psxFile, animation, name, opts);

        return (scene.ToGltf2(), totalTriangles);
    }

    /// <summary>
    ///     Attaches per-bone rotation and translation curves. Codec output is
    ///     already local-to-parent (the engine's chain accumulation in
    ///     Decomp_GetAnimTransform only affects its own render buffer, not the
    ///     stored stream), so values flow directly into glTF NodeBuilder; the
    ///     parent chain handles world composition. Placeholder tracks (all-zero
    ///     across all frames) are skipped to preserve bind pose.
    /// </summary>
    private static void ApplyPsxAnimation(
        NodeBuilder[] jointNodes,
        PsxMeshFile psxFile,
        PsxAnimation animation,
        string animationName,
        PsxAnimationOptions opts)
    {
        var boneCount = Math.Min(jointNodes.Length, animation.BoneCount);
        var translationDivisor = psxFile.TranslationDivisor;
        var fps = opts.Fps;
        var frameCount = animation.FrameCount;

        for (var b = 0; b < boneCount; b++)
        {
            if (!opts.SkipRotation && animation.IsRotationAnimated(b))
            {
                var rot = jointNodes[b].UseRotation(animationName);
                for (var f = 0; f < frameCount; f++)
                {
                    var psxR = animation.GetBoneRotation(b, f, opts.RotationCompose);
                    rot.SetPoint(f / fps, PsxToGltfRotation(psxR));
                }
            }

            if (!opts.SkipTranslation && animation.IsTranslationAnimated(b))
            {
                var trans = jointNodes[b].UseTranslation(animationName);
                for (var f = 0; f < frameCount; f++)
                {
                    var psxT = animation.GetBoneTranslation(b, f) / translationDivisor;
                    trans.SetPoint(f / fps, PsxMeshSemantics.ToGltfPosition(psxT));
                }
            }
        }
    }

    /// <summary>
    ///     Converts a PSX-frame quaternion to glTF-frame. Coordinate change is
    ///     <c>diag(1, -1, -1)</c> (negate Y, Z); under this involution the
    ///     equivalent glTF quaternion is simply (qx, -qy, -qz, qw).
    /// </summary>
    private static Quaternion PsxToGltfRotation(Quaternion psx)
    {
        return new Quaternion(psx.X, -psx.Y, -psx.Z, psx.W);
    }

    /// <summary>
    ///     Adds faces from a body part mesh to a combined skinned mesh.
    /// </summary>
    private static int AddSkinnedObjectFaces(
        MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> gltfMesh,
        PsxMeshFile psxFile, int objectIndex, int meshIndex, PsxMesh mesh, PsxGltfMaterialContext materials)
    {
        var triangles = 0;
        foreach (var face in mesh.Faces)
        {
            var (material, texDims) = PsxGltfMaterialFactory.ResolveFaceMaterial(face, materials);
            var prim = gltfMesh.UsePrimitive(material);
            triangles += AddFaceSkinned(prim, psxFile, objectIndex, meshIndex, mesh, face, psxFile.GouraudPalette,
                texDims);
        }

        return triangles;
    }

    private static int AddFaceSkinned(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexJoints4> prim,
        PsxMeshFile psxFile, int objectIndex, int meshIndex, PsxMesh mesh, PsxFace face, Vector4[]? gouraudPalette,
        (int Width, int Height) texDims)
    {
        var (c0, c1, c2, c3) = ComputeFaceColors(psxFile.Version, face, gouraudPalette);

        var v0 = MakeSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 0, c0, texDims);
        var v1 = MakeSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 1, c1, texDims);
        var v2 = MakeSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 2, c2, texDims);

        prim.AddTriangle(v0, v1, v2);
        var count = 1;

        if (face.IsQuad)
        {
            var v3 = MakeSkinnedVertex(psxFile, objectIndex, meshIndex, mesh, face, 3, c3, texDims);
            prim.AddTriangle(v1, v3, v2);
            count++;
        }

        return count;
    }

    private static SKINNED_VERTEX MakeSkinnedVertex(PsxMeshFile psxFile, int objectIndex, int meshIndex,
        PsxMesh mesh, PsxFace face,
        int vertexSlot, Vector4 color, (int Width, int Height) texDims)
    {
        var vertexIndex = GetFaceVertexIndex(face, vertexSlot);
        var texCoord = face.GetTextureCoordinate(vertexSlot);
        var resolvedVertex = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);
        var position = PsxMeshSemantics.ToGltfPosition(resolvedVertex.WorldPosition);
        var normalMesh = mesh;
        var normalVertexIndex = vertexIndex;
        if (resolvedVertex.UsedAttachment
            && resolvedVertex.AttachmentResolved
            && resolvedVertex.SourceMeshIndex >= 0
            && resolvedVertex.SourceMeshIndex < psxFile.Meshes.Count
            && resolvedVertex.SourceVertexIndex >= 0)
        {
            var candidateMesh = psxFile.Meshes[resolvedVertex.SourceMeshIndex];
            if (candidateMesh.HasPerVertexNormals
                && resolvedVertex.SourceVertexIndex < candidateMesh.VertexCount)
            {
                normalMesh = candidateMesh;
                normalVertexIndex = (uint)resolvedVertex.SourceVertexIndex;
            }
        }

        var normalVec = ComputeVertexNormal(normalMesh, face, normalVertexIndex);
        var uv = ComputeTextureUv(
            psxFile.Version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height);
        var jointIndex = resolvedVertex.SourceObjectIndex >= 0
            ? resolvedVertex.SourceObjectIndex
            : objectIndex;
        var skinning = new VertexJoints4((jointIndex, 1f));

        return new SKINNED_VERTEX(
            new VertexPositionNormal(position, normalVec),
            new VertexColor1Texture1(color, uv),
            skinning);
    }

    /// <summary>
    ///     Builds the set of mesh indices that are LOD variants (lower-detail duplicates).
    ///     Any mesh pointed to by another mesh's LodNextMeshIndex is a variant to skip.
    /// </summary>
    private static HashSet<int> BuildLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(m => (int)m.LodNextMeshIndex)
            .Where(idx => idx != ushort.MaxValue && idx < psxFile.Meshes.Count)
            .ToHashSet();
    }

    private static NodeBuilder[] BuildCharacterJointNodes(PsxMeshFile psxFile, PshFile? pshFile)
    {
        var jointNodes = new NodeBuilder[psxFile.Objects.Count];
        var skeletonRoot = new NodeBuilder("skeleton_root");
        var boneNames = pshFile;

        NodeBuilder EnsureNode(int objectIndex)
        {
            if (jointNodes[objectIndex] != null)
                return jointNodes[objectIndex];

            var obj = psxFile.Objects[objectIndex];
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            var boneName = boneNames?.GetBoneName(objectIndex)
                           ?? (meshIndex >= 0 ? ResolveMeshName(psxFile, meshIndex, $"bone_{objectIndex}") : null)
                           ?? $"bone_{objectIndex}";

            NodeBuilder node;
            if (obj.ParentIndex >= 0 && obj.ParentIndex < psxFile.Objects.Count)
            {
                node = EnsureNode(obj.ParentIndex).CreateNode(boneName);
                var localOffset =
                    PsxMeshSemantics.GetObjectOffset(obj, psxFile.TranslationDivisor) -
                    PsxMeshSemantics.GetObjectOffset(psxFile.Objects[obj.ParentIndex], psxFile.TranslationDivisor);
                node.LocalTransform = new AffineTransform(
                    null,
                    Quaternion.Identity,
                    PsxMeshSemantics.ToGltfPosition(localOffset));
            }
            else
            {
                node = skeletonRoot.CreateNode(boneName);
                node.LocalTransform = new AffineTransform(
                    null,
                    Quaternion.Identity,
                    PsxMeshSemantics.ToGltfPosition(
                        PsxMeshSemantics.GetObjectOffset(obj, psxFile.TranslationDivisor)));
            }

            jointNodes[objectIndex] = node;
            return node;
        }

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
            _ = EnsureNode(objectIndex);

        return jointNodes;
    }

    private static bool UsesCombinedCharacterAssembly(PsxMeshFile psxFile)
    {
        return psxFile.HasHierarchy
               || psxFile.AttachmentVertices.Count > 0
               || psxFile.Meshes.Any(mesh =>
                   mesh.Vertices.Any(vertex => PsxMeshSemantics.IsExactStitchedReference(vertex.Type)));
    }

    /// <summary>
    ///     Builds a glTF mesh for a single PSX object. Returns null if the object has no renderable geometry.
    ///     Uses obj.MeshIndex to select the mesh (confirmed by Ghidra decompilation of M3dInit_ParsePSX).
    /// </summary>
    private static (MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>? Mesh, int Triangles)
        BuildObjectMesh(PsxMeshFile psxFile, PsxMeshObject obj, PsxGltfMaterialContext materials)
    {
        var meshIndex = obj.MeshIndex;
        if (meshIndex >= psxFile.Meshes.Count)
            return (null, 0);

        var mesh = psxFile.Meshes[meshIndex];
        if (mesh.Faces.Count == 0)
            return (null, 0);

        var meshName = ResolveMeshName(psxFile, meshIndex, $"mesh_{meshIndex:X8}");
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(meshName);
        var triangles = 0;

        foreach (var face in mesh.Faces)
        {
            var (material, texDims) = PsxGltfMaterialFactory.ResolveFaceMaterial(face, materials);
            var prim = gltfMesh.UsePrimitive(material);
            triangles += AddFace(prim, psxFile.Version, mesh, face, psxFile.GouraudPalette, texDims.Width,
                texDims.Height);
        }

        return (gltfMesh, triangles);
    }

    private static string ResolveMeshName(PsxMeshFile psxFile, int meshIndex, string fallback)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length
            ? psxFile.MeshNameHashes[meshIndex]
            : 0u;
        return QbKey.QbKey.TryResolve(nameHash) ?? fallback;
    }

    private static int AddFace(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        ushort version, PsxMesh mesh, PsxFace face, Vector4[]? gouraudPalette,
        int texWidth = 256, int texHeight = 256)
    {
        var (c0, c1, c2, c3) = ComputeFaceColors(version, face, gouraudPalette);
        var texDims = (texWidth, texHeight);

        var v0 = MakeVertex(version, mesh, face, 0, c0, texDims);
        var v1 = MakeVertex(version, mesh, face, 1, c1, texDims);
        var v2 = MakeVertex(version, mesh, face, 2, c2, texDims);

        prim.AddTriangle(v0, v1, v2);
        var count = 1;

        if (face.IsQuad)
        {
            var v3 = MakeVertex(version, mesh, face, 3, c3, texDims);
            prim.AddTriangle(v1, v3, v2);
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Computes per-vertex colors for a face. For PS1 gouraud faces (v3/v4), R/G/B/Mode
    ///     are palette indices mapping to vertices 0/1/2/3. For DC/PC/Xbox (v6), the R/G/B
    ///     bytes are direct per-vertex color values (not palette indices), even when the
    ///     gouraud flag is set.
    /// </summary>
    private static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputeFaceColors(
        ushort version, PsxFace face, Vector4[]? gouraudPalette)
    {
        if (face.IsGouraud && gouraudPalette != null && version != 0x06)
        {
            var c0 = face.R < gouraudPalette.Length ? gouraudPalette[face.R] : Vector4.One;
            var c1 = face.G < gouraudPalette.Length ? gouraudPalette[face.G] : Vector4.One;
            var c2 = face.B < gouraudPalette.Length ? gouraudPalette[face.B] : Vector4.One;
            var c3 = face.IsQuad && face.Mode < gouraudPalette.Length
                ? gouraudPalette[face.Mode]
                : c0;
            return (c0, c1, c2, c3);
        }

        var flat = GetFlatFaceColor(face);
        return (flat, flat, flat, flat);
    }

    /// <summary>
    ///     Computes position, normal, and UV for a single vertex.
    ///     PS1 is left-handed; glTF is right-handed Y-up — Y and Z are negated.
    /// </summary>
    private static Vector3 ComputeVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        // M3dInit_ParsePSX: when normalCount == vertexCount + faceCount, the first
        // vertexCount normals are per-vertex (smooth shading). Use the vertex's own
        // normal when available; fall back to the per-face normal otherwise.
        var normal = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? mesh.Normals[(int)vertexIndex]
            : mesh.Normals[(int)face.NormalIndex];

        var normalVec = new Vector3(normal.X, -normal.Y, -normal.Z);
        var len = normalVec.Length();
        if (len > 0)
            return normalVec / len;

        // Degenerate source normal (all-zero or non-finite) — return a default unit normal so
        // glTF export does not reject the accessor. GltfNormalSmoother replaces these downstream.
        return Vector3.UnitY;
    }

    private static Vector2 ComputeTextureUv(
        ushort version, PsxFace face, int u, int v, int texWidth, int texHeight)
    {
        // PS1 UV coordinates are in pixel space (0..width-1, 0..height-1).
        if (!face.IsTextured)
        {
            return Vector2.Zero;
        }

        if (version == 0x06)
        {
            // The Blender plugin compensates for its own PC texture decode by rotating
            // the extracted image 180 degrees and then using negative UVs. Our extracted
            // Dreamcast/PC textures are already upright, so the equivalent glTF mapping
            // is the raw 512x512 page-space UV without the Blender negation.
            return new Vector2(u / 512f, v / 512f);
        }

        return new Vector2(
            u / (float)Math.Max(texWidth, 1),
            v / (float)Math.Max(texHeight, 1));
    }

    private static RIGID_VERTEX MakeVertex(ushort version, PsxMesh mesh, PsxFace face,
        int vertexSlot, Vector4 color,
        (int Width, int Height) texDims)
    {
        var vertexIndex = GetFaceVertexIndex(face, vertexSlot);
        var texCoord = face.GetTextureCoordinate(vertexSlot);
        var normalVec = ComputeVertexNormal(mesh, face, vertexIndex);
        var uv = ComputeTextureUv(version, face, texCoord.U, texCoord.V, texDims.Width, texDims.Height);
        var vert = mesh.Vertices[(int)vertexIndex];
        var position = new Vector3(vert.X, -vert.Y, -vert.Z);

        return new RIGID_VERTEX(
            new VertexPositionNormal(position, normalVec),
            new VertexColor1Texture1(color, uv));
    }

    private static uint GetFaceVertexIndex(PsxFace face, int vertexSlot)
    {
        return vertexSlot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(vertexSlot))
        };
    }

    private static Vector4 GetFlatFaceColor(PsxFace face)
    {
        // For non-gouraud faces, all vertices share the same flat color.
        // For gouraud faces without a palette, use white as fallback.
        if (face.IsGouraud)
            return Vector4.One;

        return new Vector4(
            Math.Min(face.R / 128f, 1f),
            Math.Min(face.G / 128f, 1f),
            Math.Min(face.B / 128f, 1f),
            1f);
    }
}
