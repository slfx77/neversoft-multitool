using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

using GltfVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;
using GltfSkinnedVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>;

public sealed class GltfModelExporter : IModelExporter
{
    private const float Ps2SubtractiveAlphaScale = 0.30f;

    public MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.OutputDirectory);

        return ExportGeneric(document, request);
    }

    public (byte[]? GlbBytes, int Triangles) BuildGlbBytes(ModelDocument document)
    {
        var (model, triangles) = BuildGenericModel(document);
        if (model == null)
            return (null, 0);

        if (triangles > 0)
            GltfNormalSmoother.SmoothNormals(model);
        using var ms = new MemoryStream();
        model.WriteGLB(ms);
        return (ms.ToArray(), triangles);
    }

    private static MeshExportResult ExportGeneric(ModelDocument document, MeshExportRequest request)
    {
        var (model, triangles) = BuildGenericModel(document);
        if (model == null)
            return MeshExportResult.Empty;

        var outputPath = Path.Combine(request.OutputDirectory, (request.OutputStem ?? document.Name) + ".glb");
        if (triangles > 0)
            GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(outputPath);
        return new MeshExportResult
        {
            OutputPaths = [outputPath],
            Triangles = triangles,
            MaterialCount = document.Materials.Count,
            TextureCount = document.Textures.Count
        };
    }

    private static (ModelRoot? Model, int Triangles) BuildGenericModel(ModelDocument document)
    {
        var scene = new SceneBuilder();
        var materials = document.Materials.Select(material => BuildMaterial(material, document.Textures)).ToArray();
        var (skeletonJoints, skeletonRoots) = BuildSkeletonJointTrees(document.Skeletons);
        ApplyAnimations(skeletonJoints, document.Animations);
        var totalTriangles = 0;

        var roots = document.Scenes.Count > 0
            ? document.Scenes.SelectMany(static s => s.RootNodeIndices).ToArray()
            : Enumerable.Range(0, document.Nodes.Count).ToArray();
        var visited = new HashSet<int>();
        foreach (var rootIndex in roots)
            totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, rootIndex, Matrix4x4.Identity, visited);

        if (roots.Length == 0)
        {
            for (var i = 0; i < document.Nodes.Count; i++)
                totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, i, Matrix4x4.Identity, visited);
        }

        // Skeleton-only documents (no meshes) need explicit attachment of the joint
        // tree to the scene; AddSkinnedMesh would normally handle this. With no skin,
        // the synthetic root is the only way the joints (and their animation tracks)
        // make it into the output glTF.
        if (totalTriangles == 0 && skeletonRoots.Length > 0)
        {
            foreach (var root in skeletonRoots)
                scene.AddNode(root);
        }
        else if (totalTriangles == 0 && document.Animations.Count == 0)
        {
            return (null, 0);
        }

        return (scene.ToGltf2(), totalTriangles);
    }

    private static void ApplyAnimations(
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints,
        IReadOnlyList<ModelAnimation> animations)
    {
        foreach (var animation in animations)
        {
            foreach (var channel in animation.Channels)
            {
                if ((uint)channel.SkeletonIndex >= (uint)skeletonJoints.Count)
                    continue;
                var joints = skeletonJoints[channel.SkeletonIndex];
                if ((uint)channel.BoneIndex >= (uint)joints.Length)
                    continue;
                var node = joints[channel.BoneIndex].Node;
                ApplyAnimationChannel(node, animation.Name, channel);
            }
        }
    }

    private static void ApplyAnimationChannel(NodeBuilder node, string animationName, ModelAnimationChannel channel)
    {
        var keyCount = channel.KeyCount;
        if (keyCount == 0)
            return;
        var isLinear = channel.Interpolation != ModelAnimationInterpolation.Step;

        switch (channel.Property)
        {
            case ModelAnimationProperty.Rotation:
            {
                var curve = node.UseRotation(animationName);
                for (var i = 0; i < keyCount; i++)
                {
                    var offset = i * 4;
                    curve.SetPoint(channel.Times[i],
                        new Quaternion(
                            channel.Values[offset],
                            channel.Values[offset + 1],
                            channel.Values[offset + 2],
                            channel.Values[offset + 3]),
                        isLinear);
                }
                break;
            }
            case ModelAnimationProperty.Translation:
            {
                var curve = node.UseTranslation(animationName);
                for (var i = 0; i < keyCount; i++)
                {
                    var offset = i * 3;
                    curve.SetPoint(channel.Times[i],
                        new Vector3(
                            channel.Values[offset],
                            channel.Values[offset + 1],
                            channel.Values[offset + 2]),
                        isLinear);
                }
                break;
            }
            case ModelAnimationProperty.Scale:
            {
                var curve = node.UseScale(animationName);
                for (var i = 0; i < keyCount; i++)
                {
                    var offset = i * 3;
                    curve.SetPoint(channel.Times[i],
                        new Vector3(
                            channel.Values[offset],
                            channel.Values[offset + 1],
                            channel.Values[offset + 2]),
                        isLinear);
                }
                break;
            }
        }
    }

    private static ((NodeBuilder Node, Matrix4x4 InverseBindMatrix)[][] Joints, NodeBuilder[] SyntheticRoots)
        BuildSkeletonJointTrees(IReadOnlyList<ModelSkeleton> skeletons)
    {
        var joints = new (NodeBuilder, Matrix4x4)[skeletons.Count][];
        var roots = new NodeBuilder[skeletons.Count];
        for (var skeletonIndex = 0; skeletonIndex < skeletons.Count; skeletonIndex++)
        {
            var skeleton = skeletons[skeletonIndex];
            var nodes = new NodeBuilder[skeleton.Bones.Count];
            var skeletonJoints = new (NodeBuilder, Matrix4x4)[skeleton.Bones.Count];
            // SharpGLTF's SkinnedTransformer requires all joints share a single
            // root in the scene graph. PSX skeletons can have several bones with
            // parentIndex == -1, so hang every orphan from a synthetic root
            // NodeBuilder. RW DFF / PS2 Scene skeletons have a single root and
            // are unaffected (the synthetic root just becomes their parent).
            var syntheticRoot = new NodeBuilder($"{skeleton.Name}_root");

            // Iterate parents-before-children — PSX character skeletons can
            // reference parent indices LARGER than the child's own (e.g.
            // hawk2.psx's HIER chunk has bone 1 → parent 2 → parent 3 → root),
            // which would otherwise silently re-parent the descendants to the
            // synthetic root and collapse the hierarchy.
            foreach (var i in TopologicalOrder(skeleton.Bones))
            {
                var bone = skeleton.Bones[i];
                var name = string.IsNullOrEmpty(bone.Name) ? $"bone_{i}" : bone.Name;
                var hasUsableParent = bone.ParentIndex >= 0
                                      && bone.ParentIndex < skeleton.Bones.Count
                                      && bone.ParentIndex != i
                                      && nodes[bone.ParentIndex] != null;
                nodes[i] = hasUsableParent
                    ? nodes[bone.ParentIndex].CreateNode(name)
                    : syntheticRoot.CreateNode(name);
                nodes[i].LocalMatrix = bone.LocalTransform;
                skeletonJoints[i] = (nodes[i], bone.InverseBindMatrix);
            }

            joints[skeletonIndex] = skeletonJoints;
            roots[skeletonIndex] = syntheticRoot;
        }

        return (joints, roots);
    }

    /// <summary>
    ///     Yields bone indices in parents-before-children order. Bones with
    ///     no in-list parent (root or self-parent) come first; their children
    ///     follow once the parent has been emitted. Cycles or unresolvable
    ///     references are appended at the end so every bone still appears
    ///     exactly once (they hang from the synthetic root in that case).
    /// </summary>
    private static IEnumerable<int> TopologicalOrder(IReadOnlyList<ModelBone> bones)
    {
        var count = bones.Count;
        var emitted = new bool[count];
        var order = new List<int>(count);

        var queue = new Queue<int>(EnumerateRoots(bones));
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            if (emitted[i]) continue;
            emitted[i] = true;
            order.Add(i);
            EnqueueChildren(bones, emitted, queue, i);
        }

        // Cycle survivors: any bone whose ancestor chain doesn't terminate at
        // a root falls through here and is treated as an orphan.
        for (var i = 0; i < count; i++)
            if (!emitted[i])
                order.Add(i);

        return order;
    }

    private static IEnumerable<int> EnumerateRoots(IReadOnlyList<ModelBone> bones)
    {
        var count = bones.Count;
        for (var i = 0; i < count; i++)
        {
            var p = bones[i].ParentIndex;
            if (p < 0 || p >= count || p == i)
                yield return i;
        }
    }

    private static void EnqueueChildren(
        IReadOnlyList<ModelBone> bones, bool[] emitted, Queue<int> queue, int parent)
    {
        for (var c = 0; c < bones.Count; c++)
        {
            if (!emitted[c] && bones[c].ParentIndex == parent && c != parent)
                queue.Enqueue(c);
        }
    }

    private static int AddNodeRecursive(
        SceneBuilder scene,
        ModelDocument document,
        IReadOnlyList<MaterialBuilder> materials,
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints,
        int nodeIndex,
        Matrix4x4 parentTransform,
        HashSet<int> visited)
    {
        if ((uint)nodeIndex >= (uint)document.Nodes.Count || !visited.Add(nodeIndex))
            return 0;

        var node = document.Nodes[nodeIndex];
        var worldTransform = node.Transform * parentTransform;
        var totalTriangles = 0;

        if (node.MeshIndex.HasValue)
        {
            var meshIndex = node.MeshIndex!.Value;
            if ((uint)meshIndex < (uint)document.Meshes.Count)
            {
                var modelMesh = document.Meshes[meshIndex];
                totalTriangles += IsSkinnedMesh(modelMesh)
                    ? AddSkinnedMesh(scene, modelMesh, materials, skeletonJoints)
                    : AddRigidMesh(scene, modelMesh, materials, worldTransform);
            }
        }

        foreach (var childIndex in node.ChildNodeIndices)
            totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, childIndex, worldTransform, visited);

        return totalTriangles;
    }

    private static bool IsSkinnedMesh(ModelMesh mesh)
    {
        foreach (var primitive in mesh.Primitives)
        {
            if (primitive.Skin is not null)
                return true;
        }

        return false;
    }

    private static int AddRigidMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        Matrix4x4 worldTransform)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(modelMesh.Name);
        var totalTriangles = 0;
        foreach (var primitive in modelMesh.Primitives)
        {
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddTriangles(prim, primitive);
        }

        scene.AddRigidMesh(mesh, worldTransform);
        return totalTriangles;
    }

    private static int AddSkinnedMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(modelMesh.Name);
        var totalTriangles = 0;
        var skeletonIndex = -1;
        foreach (var primitive in modelMesh.Primitives)
        {
            if (primitive.Skin is { } skin && (uint)skin.SkeletonIndex < (uint)skeletonJoints.Count)
            {
                skeletonIndex = skin.SkeletonIndex;
                var material = ResolveMaterial(primitive, materials);
                var prim = mesh.UsePrimitive(material);
                totalTriangles += AddSkinnedTriangles(prim, primitive, skin);
            }
        }

        if (totalTriangles > 0 && skeletonIndex >= 0)
            scene.AddSkinnedMesh(mesh, skeletonJoints[skeletonIndex]);

        return totalTriangles;
    }

    private static MaterialBuilder ResolveMaterial(ModelPrimitive primitive, IReadOnlyList<MaterialBuilder> materials)
    {
        return primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materials.Count
            ? materials[primitive.MaterialIndex]
            : new MaterialBuilder("default").WithUnlitShader().WithDoubleSide(true);
    }

    private static MaterialBuilder BuildMaterial(RenderMaterial material, IReadOnlyList<ModelTexture> textures)
    {
        var builder = new MaterialBuilder(material.Name)
            .WithBaseColor(material.BaseColor)
            .WithDoubleSide(material.DoubleSided);

        if (material.Unlit)
            builder.WithUnlitShader();

        if (material.TextureIndex is { } textureIndex &&
            (uint)textureIndex < (uint)textures.Count &&
            textures[textureIndex].PngBytes is { Length: > 0 } pngBytes)
        {
            var gltfPngBytes = ProcessTextureForPortableGltf(material, pngBytes);
            builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(gltfPngBytes));
            var channel = builder.GetChannel(KnownChannel.BaseColor);
            var wrapS = ToTextureWrapMode(textures[textureIndex].WrapU);
            var wrapT = ToTextureWrapMode(textures[textureIndex].WrapV);
            channel.Texture.WithSampler(wrapS, wrapT);
        }

        switch (material.AlphaMode)
        {
            case ModelAlphaMode.Mask:
                builder.WithAlpha(AlphaMode.MASK, material.AlphaCutoff);
                break;
            case ModelAlphaMode.Blend:
                builder.WithAlpha(AlphaMode.BLEND);
                break;
        }

        return builder;
    }

    private static byte[] ProcessTextureForPortableGltf(RenderMaterial material, byte[] pngBytes)
    {
        foreach (var metadata in material.NativeMetadata)
        {
            if (metadata is not Ps2GsRenderMetadata { Alpha: { } alpha })
                continue;

            var alphaBlend = (byte)(alpha & 0xFF);
            var aField = alphaBlend & 0x03;
            var bField = (alphaBlend >> 2) & 0x03;
            var cField = (alphaBlend >> 4) & 0x03;
            var dField = (alphaBlend >> 6) & 0x03;
            var fixScale = Math.Clamp(((alpha >> 32) & 0xFF) / 128f, 0f, 1f);
            var isAdditive = aField == 0 && bField == 2 && dField == 1 && cField is 0 or 2;
            var isSubtractive = aField == 2 && bField == 0 && dField == 1 && cField is 0 or 2;
            if (isAdditive)
            {
                var converted = MeshTextureHelper.ConvertAdditiveBlendTexture(pngBytes);
                return cField == 2
                    ? MeshTextureHelper.ScaleTextureAlpha(converted, fixScale)
                    : converted;
            }

            if (isSubtractive)
            {
                var converted = MeshTextureHelper.ConvertBlendTexture(pngBytes, 0, 0, 0);
                var scale = cField == 2 ? Ps2SubtractiveAlphaScale * fixScale : Ps2SubtractiveAlphaScale;
                return MeshTextureHelper.ScaleTextureAlpha(converted, scale);
            }
        }

        return pngBytes;
    }

    private static TextureWrapMode ToTextureWrapMode(ModelTextureWrap wrap)
    {
        return wrap == ModelTextureWrap.ClampToEdge
            ? TextureWrapMode.CLAMP_TO_EDGE
            : TextureWrapMode.REPEAT;
    }

    private static int AddTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        ModelPrimitive primitive)
    {
        var triangles = 0;
        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            var ia = primitive.Indices[i];
            var ib = primitive.Indices[i + 1];
            var ic = primitive.Indices[i + 2];
            if ((uint)ia >= (uint)primitive.Vertices.Length ||
                (uint)ib >= (uint)primitive.Vertices.Length ||
                (uint)ic >= (uint)primitive.Vertices.Length)
            {
                continue;
            }

            prim.AddTriangle(
                MakeVertex(primitive.Vertices[ia]),
                MakeVertex(primitive.Vertices[ib]),
                MakeVertex(primitive.Vertices[ic]));
            triangles++;
        }

        return triangles;
    }

    private static GltfVertex MakeVertex(ModelVertex vertex)
    {
        return new GltfVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new VertexColor1Texture1(vertex.Color, vertex.TexCoord));
    }

    private static int AddSkinnedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexJoints4> prim,
        ModelPrimitive primitive,
        ModelSkinBinding skin)
    {
        var triangles = 0;
        for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
        {
            var ia = primitive.Indices[i];
            var ib = primitive.Indices[i + 1];
            var ic = primitive.Indices[i + 2];
            if ((uint)ia >= (uint)primitive.Vertices.Length ||
                (uint)ib >= (uint)primitive.Vertices.Length ||
                (uint)ic >= (uint)primitive.Vertices.Length)
            {
                continue;
            }

            prim.AddTriangle(
                MakeSkinnedVertex(primitive.Vertices[ia], skin.Influences[ia]),
                MakeSkinnedVertex(primitive.Vertices[ib], skin.Influences[ib]),
                MakeSkinnedVertex(primitive.Vertices[ic], skin.Influences[ic]));
            triangles++;
        }

        return triangles;
    }

    private static GltfSkinnedVertex MakeSkinnedVertex(ModelVertex vertex, ModelBoneInfluences influences)
    {
        return new GltfSkinnedVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new VertexColor1Texture1(vertex.Color, vertex.TexCoord),
            new VertexJoints4(
                (influences.Joint0, influences.Weight0),
                (influences.Joint1, influences.Weight1),
                (influences.Joint2, influences.Weight2),
                (influences.Joint3, influences.Weight3)));
    }
}
