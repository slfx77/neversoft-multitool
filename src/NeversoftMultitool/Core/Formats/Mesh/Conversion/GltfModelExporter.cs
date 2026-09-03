using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

using GltfVertex = VertexBuilder<VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexEmpty>;
using GltfSkinnedVertex = VertexBuilder<VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexJoints4>;
using PsxOverbrightGltfVertex = VertexBuilder<VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexEmpty>;
using PsxOverbrightGltfSkinnedVertex =
    VertexBuilder<VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexJoints4>;
using PsxAnimatedGltfVertex = VertexBuilder<VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexEmpty>;
using PsxAnimatedGltfSkinnedVertex =
    VertexBuilder<VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexJoints4>;

public sealed class GltfModelExporter : IModelExporter
{
    private const float Ps2SubtractiveAlphaScale = 0.30f;

    public MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        return ExportGeneric(document, request);
    }

#pragma warning disable CA1822, S2325 // Preserve the public instance API used alongside Export.
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
#pragma warning restore CA1822, S2325

    private static MeshExportResult ExportGeneric(ModelDocument document, MeshExportRequest request)
    {
        var (model, triangles) = BuildGenericModel(document);
        Directory.CreateDirectory(request.OutputDirectory);
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
        var (cameraSkeletons, cameraNames) =
            AddPerspectiveCameras(scene, skeletonJoints, document);
        var totalTriangles = 0;

        var roots = document.Scenes.Count > 0
            ? document.Scenes.SelectMany(static s => s.RootNodeIndices).ToArray()
            : Enumerable.Range(0, document.Nodes.Count).ToArray();
        var visited = new HashSet<int>();
        foreach (var rootIndex in roots)
            totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, rootIndex,
                Matrix4x4.Identity, visited);

        if (roots.Length == 0)
        {
            for (var i = 0; i < document.Nodes.Count; i++)
                totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, i, Matrix4x4.Identity,
                    visited);
        }

        // Skeleton-only documents (no meshes) need explicit attachment of the joint
        // tree to the scene; AddSkinnedMesh would normally handle this. With no skin,
        // the synthetic root is the only way the joints (and their animation tracks)
        // make it into the output glTF.
        if (totalTriangles == 0 && skeletonRoots.Length > 0)
        {
            for (var i = 0; i < skeletonRoots.Length; i++)
            {
                // AddCamera already publishes this NodeBuilder armature. Do not
                // add a second empty scene instance for the same skeleton root.
                if (!cameraSkeletons.Contains(i))
                    scene.AddNode(skeletonRoots[i]);
            }
        }
        else if (totalTriangles == 0 && document.Animations.Count == 0 && cameraSkeletons.Count == 0)
        {
            return (null, 0);
        }

        var model = scene.ToGltf2();
        for (var i = 0; i < cameraNames.Count && i < model.LogicalCameras.Count; i++)
            model.LogicalCameras[i].Name = cameraNames[i];
        ApplyMorphAnimations(model, document);
        ApplySceneExtras(model, document);
        return (model, totalTriangles);
    }

    /// <summary>
    ///     Publishes morph-weight tracks. Unlike bone tracks these cannot be built
    ///     through SceneBuilder — the toolkit's node builder exposes no morph
    ///     property — so they are written onto the converted glTF, where the
    ///     weights sampler can address the mesh's targets directly.
    /// </summary>
    private static void ApplyMorphAnimations(ModelRoot model, ModelDocument document)
    {
        var morphing = document.Animations.Where(static a => a.MorphChannel != null).ToList();
        if (morphing.Count == 0)
            return;

        foreach (var animation in morphing)
        {
            var channel = animation.MorphChannel!;
            if ((uint)channel.MeshIndex >= (uint)document.Meshes.Count)
                continue;

            // SceneBuilder names the emitted node after the source mesh.
            var meshName = document.Meshes[channel.MeshIndex].Name;
            var node = model.LogicalNodes.FirstOrDefault(
                n => n.Mesh != null && string.Equals(n.Mesh.Name, meshName, StringComparison.Ordinal));
            var emitted = node?.Mesh?.Primitives.FirstOrDefault()?.MorphTargetsCount ?? 0;
            if (node?.Mesh == null || emitted != channel.TargetCount)
                continue;

            var keyframes = new Dictionary<float, SparseWeight8>(channel.KeyCount);
            for (var key = 0; key < channel.KeyCount; key++)
            {
                var weights = new float[channel.TargetCount];
                Array.Copy(channel.Weights, key * channel.TargetCount, weights, 0, channel.TargetCount);
                keyframes[channel.Times[key]] = SparseWeight8.Create(weights);
            }

            var gltfAnimation = model.LogicalAnimations
                                    .FirstOrDefault(a => string.Equals(a.Name, animation.Name, StringComparison.Ordinal))
                                ?? model.CreateAnimation(animation.Name);
            gltfAnimation.CreateMorphChannel(node, keyframes, channel.TargetCount, linear: true);
        }
    }

    private static (HashSet<int> Skeletons, List<string> Names) AddPerspectiveCameras(
        SceneBuilder scene,
        (NodeBuilder Node, Matrix4x4 InverseBindMatrix)[][] skeletonJoints,
        ModelDocument document)
    {
        var attachedSkeletons = new HashSet<int>();
        var names = new List<string>();
        foreach (var camera in document.PerspectiveCameras)
        {
            if (!ModelPerspectiveCameraValidation.IsValid(document, camera) ||
                (uint)camera.SkeletonIndex >= (uint)skeletonJoints.Length)
                continue;
            var joints = skeletonJoints[camera.SkeletonIndex];
            if ((uint)camera.BoneIndex >= (uint)joints.Length)
            {
                continue;
            }

            var builder = new CameraBuilder.Perspective(
                camera.AspectRatio,
                camera.VerticalFieldOfViewRadians,
                camera.ZNear,
                camera.ZFar)
            {
                Name = camera.Name
            };
            scene.AddCamera(builder, joints[camera.BoneIndex].Node);
            attachedSkeletons.Add(camera.SkeletonIndex);
            names.Add(camera.Name);
        }

        return (attachedSkeletons, names);
    }

    /// <summary>
    ///     Publishes the document's colour-pulse channel table as SCENE extras
    ///     (<c>neversoftColourPulseChannels</c>). Scene scope rather than
    ///     per-mesh: the table is shared by every pulsed mesh in the document,
    ///     and replicating a 60-pulse table across hundreds of level meshes
    ///     would add tens of megabytes of JSON.
    /// </summary>
    /// <summary>
    ///     Publishes document-scope render facts as SCENE extras: the
    ///     colour-pulse channel table and the PSX sky backdrop colour (the
    ///     engine's framebuffer clear — carried at scene scope because a
    ///     region can name one while owning no sky mesh to ride on).
    /// </summary>
    private static void ApplySceneExtras(SharpGLTF.Schema2.ModelRoot model, ModelDocument document)
    {
        if (model.DefaultScene == null)
            return;

        var extras = new System.Text.Json.Nodes.JsonObject();
        var backdrop = document.NativeMetadata.OfType<PsxSkyBackdropMetadata>().FirstOrDefault();
        if (backdrop != null)
            extras["neversoftSkyBackdrop"] = backdrop.SkyColor;

        ApplyColourPulseTable(extras, document);
        ApplyLevelLights(extras, document);
        if (extras.Count > 0)
            model.DefaultScene.Extras = extras;
    }

    /// <summary>
    ///     Publishes the zone's authored levellight nodes as SCENE extras
    ///     (<c>neversoftLevelLights</c>): position (export space), colour,
    ///     radii, exclusion flags, and the TOD/story gates. Data-only —
    ///     authored brightness is a runtime placeholder (the TOD scripts own
    ///     the live values), so consumers decide how to light with these.
    /// </summary>
    private static void ApplyLevelLights(
        System.Text.Json.Nodes.JsonObject extras, ModelDocument document)
    {
        var metadata = document.NativeMetadata
            .OfType<Ps2WorldzoneLevelLightsMetadata>().FirstOrDefault();
        if (metadata == null || metadata.Lights.Count == 0)
            return;

        var lights = new System.Text.Json.Nodes.JsonArray();
        foreach (var light in metadata.Lights)
        {
            var entry = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = QbKey.QbKey.TryResolve(light.NameChecksum) ?? $"0x{light.NameChecksum:X8}",
                ["position"] = new System.Text.Json.Nodes.JsonArray(
                    light.Position.X, light.Position.Y, light.Position.Z),
                ["color"] = new System.Text.Json.Nodes.JsonArray(
                    light.ColorR, light.ColorG, light.ColorB),
                ["brightness"] = light.Brightness,
                ["innerRadius"] = light.InnerRadius,
                ["outerRadius"] = light.OuterRadius
            };
            if (light.ExcludeLevel)
                entry["excludeLevel"] = true;
            if (light.ExcludeSkater)
                entry["excludeSkater"] = true;
            if (light.CreatedFromTod != 0)
                entry["todGroup"] = QbKey.QbKey.TryResolve(light.CreatedFromTod)
                                    ?? $"0x{light.CreatedFromTod:X8}";
            if (light.CreatedFromVariable != 0)
                entry["storyState"] = QbKey.QbKey.TryResolve(light.CreatedFromVariable)
                                      ?? $"0x{light.CreatedFromVariable:X8}";
            lights.Add(entry);
        }

        extras["neversoftLevelLights"] = lights;
    }

    private static void ApplyColourPulseTable(
        System.Text.Json.Nodes.JsonObject extras, ModelDocument document)
    {
        var table = document.NativeMetadata.OfType<PsxColourPulseTableMetadata>().FirstOrDefault();
        if (table == null || table.Channels.Count == 0)
            return;

        var channels = new System.Text.Json.Nodes.JsonArray();
        foreach (var channel in table.Channels)
        {
            // Everything goes in as float. SharpGLTF serializes extras through a
            // source-generated resolver that has no JsonTypeInfo for a boxed
            // System.Int32, so JsonArray.Add(int) throws at write time — only the
            // params-of-JsonNode constructor path is safe.
            // Everything goes in as float. SharpGLTF serializes extras through a
            // source-generated resolver that has no JsonTypeInfo for a boxed
            // System.Int32, so JsonArray.Add(int) throws at write time.
            var keys = new System.Text.Json.Nodes.JsonArray();
            var portable = new System.Text.Json.Nodes.JsonArray();
            var intervalNodes = new System.Text.Json.Nodes.JsonNode?[channel.Intervals.Count];
            for (var i = 0; i < channel.PacketKeys.Count; i++)
            {
                var packetKey = channel.PacketKeys[i];
                var portableKey = channel.PortableKeys[i];
                keys.Add(new System.Text.Json.Nodes.JsonArray(
                    packetKey.X, packetKey.Y, packetKey.Z, packetKey.W));
                portable.Add(new System.Text.Json.Nodes.JsonArray(
                    portableKey.X, portableKey.Y, portableKey.Z, portableKey.W));
                intervalNodes[i] = System.Text.Json.Nodes.JsonValue.Create((float)channel.Intervals[i]);
            }

            var intervals = new System.Text.Json.Nodes.JsonArray(intervalNodes);

            channels.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["keys"] = keys,
                ["portableKeys"] = portable,
                ["intervals"] = intervals,
                ["keyIndex"] = (float)channel.InitialKeyIndex,
                ["accumulator"] = (float)channel.InitialAccumulator
            });
        }

        extras["neversoftColourPulseChannels"] = channels;
    }

    private static void ApplyAnimations(
        (NodeBuilder Node, Matrix4x4 InverseBindMatrix)[][] skeletonJoints,
        IReadOnlyList<ModelAnimation> animations)
    {
        foreach (var animation in animations)
        {
            foreach (var channel in animation.Channels)
            {
                if ((uint)channel.SkeletonIndex >= (uint)skeletonJoints.Length)
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
        BuildSkeletonJointTrees(List<ModelSkeleton> skeletons)
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
            var syntheticRoot = new NodeBuilder($"{skeleton.Name}_root")
            {
                LocalMatrix = skeleton.RootTransform
            };

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
    private static List<int> TopologicalOrder(List<ModelBone> bones)
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

    private static IEnumerable<int> EnumerateRoots(List<ModelBone> bones)
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
        List<ModelBone> bones, bool[] emitted, Queue<int> queue, int parent)
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
                    : AddRigidMesh(scene, modelMesh, materials,
                        ComposeDrawOrderSeparation(modelMesh, worldTransform));
            }
        }

        foreach (var childIndex in node.ChildNodeIndices)
            totalTriangles += AddNodeRecursive(scene, document, materials, skeletonJoints, childIndex, worldTransform,
                visited);

        return totalTriangles;
    }

    private static bool IsSkinnedMesh(ModelMesh mesh)
    {
        return mesh.Primitives.Any(static primitive => primitive.Skin is not null);
    }

    /// <summary>
    ///     Composes the draw-order separation vector (BlendOffset) into the
    ///     transform the GLB consumes, for meshes publishing
    ///     <see cref="IMeshDrawOrderExtras" /> with a non-zero offset. The
    ///     offset is mesh-local and applied BEFORE the node transform —
    ///     exactly Blender's object-level application
    ///     (import_package.py <c>_apply_worldzone_blend_offset</c>:
    ///     <c>matrix_world.to_3x3() @ local</c>). renderOrder metadata alone
    ///     only resolves the SAME polygon re-submitted; 84.5% of PSX overlay
    ///     pairs are DIFFERENT polygons on a shared plane whose interpolated
    ///     depths dither under LEQUAL, so the GLB needs the rigid separation
    ///     too (2026-08-03). Deliberately composed HERE and not into
    ///     <c>ModelNode.Transform</c>: the .blend manifest serializes the node
    ///     transform and the importer adds BlendOffset again — composing
    ///     upstream would double-apply it and break the importer's documented
    ///     re-zero-to-authored contract. Mesh vertex data stays authored in
    ///     both outputs.
    /// </summary>
    private static Matrix4x4 ComposeDrawOrderSeparation(ModelMesh modelMesh, Matrix4x4 worldTransform)
    {
        var drawOrder = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<IMeshDrawOrderExtras>()
            .FirstOrDefault(static metadata =>
                metadata.DrawIndex >= 0 && SeparationOf(metadata).LengthSquared() > 1e-12f);
        return drawOrder == null
            ? worldTransform
            : Matrix4x4.CreateTranslation(SeparationOf(drawOrder)) * worldTransform;
    }

    private static Vector3 SeparationOf(IMeshDrawOrderExtras metadata)
    {
        return new Vector3(metadata.BlendOffsetX, metadata.BlendOffsetY, metadata.BlendOffsetZ);
    }

    private static int AddRigidMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        Matrix4x4 worldTransform)
    {
        if (HasTextureWibble(modelMesh))
            return AddPsxAnimatedRigidMesh(scene, modelMesh, materials, worldTransform);

        if (HasPsxPacketColor(modelMesh) || HasOutOfRangeVertexColor(modelMesh) || HasColourPulse(modelMesh))
            return AddPsxOverbrightRigidMesh(scene, modelMesh, materials, worldTransform);

        var mesh =
            new MeshBuilder<VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexEmpty>(modelMesh.Name);
        var totalTriangles = 0;
        foreach (var primitive in modelMesh.Primitives)
        {
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddTriangles(prim, primitive);
        }

        AddMorphTargets(mesh, modelMesh);
        ApplyMeshExtras(mesh, modelMesh);
        scene.AddRigidMesh(mesh, worldTransform);
        return totalTriangles;
    }

    /// <summary>
    ///     Publish the mesh's render facts into glTF mesh extras. Draw order:
    ///     the source hardware (PS1 ordering table, PS2 GS, DDM decal ranks)
    ///     resolves coplanar layer stacks by submission order; vertices export
    ///     at authored positions, so viewers need this to composite passes in
    ///     engine order (the in-app three.js viewer maps neversoftDrawIndex to
    ///     Object3D.renderOrder, which with LEQUAL depth testing reproduces
    ///     submission-order semantics exactly). Sky: PSX sky domes tag
    ///     <c>neversoftSky</c> so the viewer draws them first with no depth
    ///     writes and keeps them out of framing/ground queries. Billboards:
    ///     PSX sprite-vertex quads tag <c>neversoftAxialBillboard</c> (+ axis
    ///     and anchor, mesh-local glTF units) so the viewer can spin the baked
    ///     quad about its authored axis toward the camera each frame.
    /// </summary>
    private static void ApplyMeshExtras(
        SharpGLTF.BaseBuilder mesh,
        ModelMesh modelMesh,
        bool psxVertexCarriers = false)
    {
        var drawOrder = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<IMeshDrawOrderExtras>()
            .FirstOrDefault(static metadata => metadata.DrawIndex >= 0);
        var sky = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxSkyRenderMetadata>()
            .FirstOrDefault();
        var billboard = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxAxialBillboardMetadata>()
            .FirstOrDefault();
        var colourPulse = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxColourPulseMetadata>()
            .FirstOrDefault();
        var semiLift = modelMesh.Primitives
            .SelectMany(static primitive => primitive.NativeMetadata)
            .OfType<PsxSemiTransparentLiftMetadata>()
            .FirstOrDefault();
        var collisionGroups = BuildCollisionGroups(modelMesh);
        if (drawOrder == null && sky == null && billboard == null && colourPulse == null &&
            semiLift == null && collisionGroups == null && !psxVertexCarriers)
            return;

        var extras = new System.Text.Json.Nodes.JsonObject();
        if (drawOrder != null)
        {
            extras["neversoftDrawIndex"] = drawOrder.DrawIndex;
            extras["neversoftPassIndex"] = drawOrder.PassIndex;
            extras["neversoftOverlapGroup"] = drawOrder.OverlapGroup;
        }

        if (semiLift != null)
        {
            // Informational only: the lift is already baked into the vertex
            // positions. Recorded so a GLB→PSX importer can subtract it; no
            // viewer or importer consumes it (see PsxSemiTransparentLiftMetadata).
            extras["neversoftSemiTransparentLiftSteps"] = semiLift.Steps;
            extras["neversoftSemiTransparentLiftDirection"] = new System.Text.Json.Nodes.JsonArray(
                semiLift.DirectionX, semiLift.DirectionY, semiLift.DirectionZ);
        }

        if (sky != null)
        {
            extras["neversoftSky"] = true;
            extras["neversoftSkyLayer"] = sky.LayerIndex;
            if (sky.SkyColor is { } skyColor)
                extras["neversoftSkyColor"] = skyColor;
        }

        if (billboard != null)
        {
            extras["neversoftAxialBillboard"] = true;
            extras["neversoftBillboardAxis"] = new System.Text.Json.Nodes.JsonArray(
                billboard.AxisX, billboard.AxisY, billboard.AxisZ);
            extras["neversoftBillboardAnchor"] = new System.Text.Json.Nodes.JsonArray(
                billboard.AnchorX, billboard.AnchorY, billboard.AnchorZ);
        }

        if (colourPulse != null)
            extras["neversoftColourPulse"] = true;

        if (collisionGroups != null)
            extras["neversoftCollisionGroups"] = collisionGroups;

        if (psxVertexCarriers)
            extras["neversoftPsxVertexCarriers"] = 1;

        mesh.Extras = extras;
    }

    /// <summary>
    ///     Preserves the classification boundaries of PSX/RW inline collision
    ///     primitives. Both collision writers intentionally use one overlay
    ///     material, and <see cref="MeshBuilder{TMaterial,TvG,TvM,TvS}.UsePrimitive" />
    ///     consequently coalesces every classification into one glTF primitive.
    ///     These ordered, zero-based ranges address that merged triangle stream.
    /// </summary>
    private static System.Text.Json.Nodes.JsonArray? BuildCollisionGroups(ModelMesh modelMesh)
    {
        System.Text.Json.Nodes.JsonArray? groups = null;
        var triangleStart = 0;
        foreach (var primitive in modelMesh.Primitives)
        {
            var triangleCount = primitive.TriangleCount;
            var psx = primitive.NativeMetadata
                .OfType<PsxCollisionFlagsRenderMetadata>()
                .FirstOrDefault();
            var rw = primitive.NativeMetadata
                .OfType<RwBspCollisionFlagsRenderMetadata>()
                .FirstOrDefault();

            if (psx != null || rw != null)
            {
                var group = new System.Text.Json.Nodes.JsonObject
                {
                    ["triangleStart"] = triangleStart,
                    ["triangleCount"] = triangleCount,
                    ["collisionFlags"] = (int)(psx?.CollisionFlags ?? rw!.CollisionFlags)
                };
                if (psx != null)
                    group["loaderInvisible"] = psx.LoaderInvisible;

                groups ??= [];
                groups.Add(group);
            }

            triangleStart += triangleCount;
        }

        return groups;
    }

    private static int AddSkinnedMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints)
    {
        if (HasTextureWibble(modelMesh))
            return AddPsxAnimatedSkinnedMesh(scene, modelMesh, materials, skeletonJoints);

        if (HasPsxPacketColor(modelMesh) || HasOutOfRangeVertexColor(modelMesh) || HasColourPulse(modelMesh))
            return AddPsxOverbrightSkinnedMesh(scene, modelMesh, materials, skeletonJoints);

        var mesh =
            new MeshBuilder<VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexJoints4>(modelMesh.Name);
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

    private static int AddPsxAnimatedRigidMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        Matrix4x4 worldTransform)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexEmpty>(
            modelMesh.Name);
        var totalTriangles = 0;
        foreach (var primitive in modelMesh.Primitives)
        {
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddPsxAnimatedTriangles(prim, primitive);
        }

        // Wibbled sprite/overlay meshes still carry billboard/draw-order
        // metadata (THPS2 skny's sprite trees animate their bark texture).
        ApplyMeshExtras(mesh, modelMesh, psxVertexCarriers: true);
        scene.AddRigidMesh(mesh, worldTransform);
        return totalTriangles;
    }

    private static int AddPsxAnimatedSkinnedMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexJoints4>(
            modelMesh.Name);
        var totalTriangles = 0;
        var skeletonIndex = -1;
        foreach (var primitive in modelMesh.Primitives)
        {
            if (primitive.Skin is not { } skin ||
                (uint)skin.SkeletonIndex >= (uint)skeletonJoints.Count)
            {
                continue;
            }

            skeletonIndex = skin.SkeletonIndex;
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddPsxAnimatedSkinnedTriangles(prim, primitive, skin);
        }

        ApplyMeshExtras(mesh, modelMesh, psxVertexCarriers: true);
        if (totalTriangles > 0 && skeletonIndex >= 0)
            scene.AddSkinnedMesh(mesh, skeletonJoints[skeletonIndex]);

        return totalTriangles;
    }

    private static int AddPsxOverbrightRigidMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        Matrix4x4 worldTransform)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexEmpty>(
            modelMesh.Name);
        var totalTriangles = 0;
        foreach (var primitive in modelMesh.Primitives)
        {
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddPsxOverbrightTriangles(prim, primitive);
        }

        ApplyMeshExtras(mesh, modelMesh, psxVertexCarriers: true);
        scene.AddRigidMesh(mesh, worldTransform);
        return totalTriangles;
    }

    private static int AddPsxOverbrightSkinnedMesh(
        SceneBuilder scene,
        ModelMesh modelMesh,
        IReadOnlyList<MaterialBuilder> materials,
        IReadOnlyList<(NodeBuilder Node, Matrix4x4 InverseBindMatrix)[]> skeletonJoints)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexJoints4>(
            modelMesh.Name);
        var totalTriangles = 0;
        var skeletonIndex = -1;
        foreach (var primitive in modelMesh.Primitives)
        {
            if (primitive.Skin is not { } skin ||
                (uint)skin.SkeletonIndex >= (uint)skeletonJoints.Count)
            {
                continue;
            }

            skeletonIndex = skin.SkeletonIndex;
            var material = ResolveMaterial(primitive, materials);
            var prim = mesh.UsePrimitive(material);
            totalTriangles += AddPsxOverbrightSkinnedTriangles(prim, primitive, skin);
        }

        ApplyMeshExtras(mesh, modelMesh, psxVertexCarriers: true);
        if (totalTriangles > 0 && skeletonIndex >= 0)
            scene.AddSkinnedMesh(mesh, skeletonJoints[skeletonIndex]);

        return totalTriangles;
    }

    private static bool HasOutOfRangeVertexColor(ModelMesh mesh)
    {
        return mesh.Primitives.Any(static primitive =>
            primitive.Vertices.Any(static vertex =>
                vertex.Color.X is < 0f or > 1f ||
                vertex.Color.Y is < 0f or > 1f ||
                vertex.Color.Z is < 0f or > 1f ||
                vertex.Color.W is < 0f or > 1f));
    }

    private static bool HasPsxPacketColor(ModelMesh mesh)
    {
        return mesh.Primitives.Any(static primitive =>
            primitive.Vertices.Any(static vertex => vertex.PsxPacketColor.HasValue));
    }

    private static bool HasTextureWibble(ModelMesh mesh)
    {
        return mesh.Primitives.Any(static primitive =>
            primitive.Vertices.Any(static vertex => vertex.TextureWibble.HasValue));
    }

    /// <summary>
    ///     A pulsed mesh must reach a vertex struct that carries COLOR_1,
    ///     because that is where the per-vertex channel index rides. v6 files
    ///     emit no PS1 packet, so without this they would fall through to the
    ///     plain struct and lose the lane entirely.
    /// </summary>
    private static bool HasColourPulse(ModelMesh mesh)
    {
        return mesh.Primitives.Any(static primitive =>
            primitive.Vertices.Any(static vertex => vertex.ColourPulseChannel > 0));
    }

    private static MaterialBuilder ResolveMaterial(ModelPrimitive primitive, IReadOnlyList<MaterialBuilder> materials)
    {
        return primitive.MaterialIndex >= 0 && primitive.MaterialIndex < materials.Count
            ? materials[primitive.MaterialIndex]
            : new MaterialBuilder("default").WithUnlitShader().WithDoubleSide(true);
    }

    private static MaterialBuilder BuildMaterial(RenderMaterial material, List<ModelTexture> textures)
    {
        var builder = new MaterialBuilder(material.Name)
            .WithBaseColor(material.BaseColor)
            .WithDoubleSide(material.DoubleSided);

        if (material.Unlit)
        {
            builder.WithUnlitShader();
        }
        else
        {
            // glTF defaults metallicFactor to 1.0, which renders these
            // fixed-function game surfaces as rough metal — glossy in ways the
            // console never was. Lit materials are plain diffuse.
            builder.WithMetallicRoughness(0f, 1f);
        }

        if (material.TextureIndex is { } textureIndex &&
            (uint)textureIndex < (uint)textures.Count &&
            textures[textureIndex].PngBytes is { Length: > 0 } pngBytes)
        {
            var gltfPngBytes = ProcessTextureForPortableGltf(material, pngBytes);
            builder.WithChannelImage(KnownChannel.BaseColor, new MemoryImage(gltfPngBytes));
            var channel = builder.GetChannel(KnownChannel.BaseColor);
            var wrapS = ToTextureWrapMode(textures[textureIndex].WrapU);
            var wrapT = ToTextureWrapMode(textures[textureIndex].WrapV);
            if (textures[textureIndex].NearestFilter)
            {
                channel.Texture.WithSampler(
                    wrapS, wrapT,
                    TextureMipMapFilter.NEAREST, TextureInterpolationFilter.NEAREST);
            }
            else
            {
                channel.Texture.WithSampler(wrapS, wrapT);
            }
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

        // PS2 additive/subtractive bakes publish their class in material
        // extras: PS2 material names never match the PSX __st suffix the
        // viewer's additive path keys on, so correctly baked glow sheets
        // composited as source-alpha in-app (B11 / triage follow-up 4).
        if (material.AlphaMode == ModelAlphaMode.Blend)
        {
            foreach (var metadata in material.NativeMetadata)
            {
                if (metadata is not Ps2GsRenderMetadata { Alpha: { } alphaReg })
                    continue;

                var bakeClass = Ps2GeomRenderSemantics.ClassifyPortableBakeClass((byte)(alphaReg & 0xFF));
                if (bakeClass != "none")
                {
                    builder.Extras = new System.Text.Json.Nodes.JsonObject
                    {
                        ["neversoftBlendClass"] = bakeClass
                    };
                }

                // No unlit handling needed here: RenderMaterial.Unlit defaults
                // TRUE, so every PS2 material (subtractive bakes included)
                // already ships KHR_materials_unlit unless a writer opts into
                // lighting — a darkening layer never picks up specular sheen.

                break;
            }
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
        return wrap switch
        {
            ModelTextureWrap.ClampToEdge => TextureWrapMode.CLAMP_TO_EDGE,
            ModelTextureWrap.MirroredRepeat => TextureWrapMode.MIRRORED_REPEAT,
            _ => TextureWrapMode.REPEAT
        };
    }

    private static int AddTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexEmpty> prim,
        ModelPrimitive primitive)
    {
        ValidateCompleteTriangleIndices(primitive);
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

    /// <summary>
    ///     Emits a mesh's morph targets. The toolkit keys a delta by the base
    ///     vertex's GEOMETRY (position + normal), so two source vertices that
    ///     share both necessarily share a delta. That is exact when they never
    ///     move apart — which is what <see cref="ModelMorphTarget" /> producers
    ///     must guarantee — so a disagreement is a decode error rather than a
    ///     rounding artefact, and the mesh is left un-morphed rather than
    ///     silently tearing.
    /// </summary>
    private static void AddMorphTargets<TvM>(
        MeshBuilder<VertexPositionNormal, TvM, VertexEmpty> mesh, ModelMesh modelMesh)
        where TvM : struct, IVertexMaterial
    {
        var targetCount = modelMesh.Primitives.Count > 0
            ? modelMesh.Primitives[0].MorphTargets?.Count ?? 0
            : 0;
        if (targetCount == 0)
            return;
        if (modelMesh.Primitives.Any(p => (p.MorphTargets?.Count ?? 0) != targetCount))
            return;

        for (var target = 0; target < targetCount; target++)
        {
            var builder = mesh.UseMorphTarget(target);
            var assigned = new Dictionary<VertexPositionNormal, Vector3>();
            foreach (var primitive in modelMesh.Primitives)
            {
                var deltas = primitive.MorphTargets![target].PositionDeltas;
                for (var v = 0; v < primitive.Vertices.Length && v < deltas.Length; v++)
                {
                    var key = MakeVertex(primitive.Vertices[v]).Geometry;
                    if (assigned.TryGetValue(key, out var existing))
                    {
                        if (Vector3.DistanceSquared(existing, deltas[v]) > 1e-6f)
                            return;
                        continue;
                    }

                    assigned[key] = deltas[v];
                    builder.SetVertexDelta(key, new VertexGeometryDelta(deltas[v], Vector3.Zero, Vector3.Zero));
                }
            }
        }
    }

    private static GltfVertex MakeVertex(ModelVertex vertex)
    {
        return new GltfVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new HighPrecisionVertexColor1Texture1(vertex.Color, vertex.TexCoord));
    }

    private static int AddPsxAnimatedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexEmpty> prim,
        ModelPrimitive primitive)
    {
        ValidateCompleteTriangleIndices(primitive);
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
                MakePsxAnimatedVertex(primitive.Vertices[ia]),
                MakePsxAnimatedVertex(primitive.Vertices[ib]),
                MakePsxAnimatedVertex(primitive.Vertices[ic]));
            triangles++;
        }

        return triangles;
    }

    private static PsxAnimatedGltfVertex MakePsxAnimatedVertex(ModelVertex vertex)
    {
        return new PsxAnimatedGltfVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new PsxAnimatedVertexColor1Texture1(vertex));
    }

    private static int AddPsxOverbrightTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexEmpty> prim,
        ModelPrimitive primitive)
    {
        ValidateCompleteTriangleIndices(primitive);
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
                MakePsxOverbrightVertex(primitive.Vertices[ia]),
                MakePsxOverbrightVertex(primitive.Vertices[ib]),
                MakePsxOverbrightVertex(primitive.Vertices[ic]));
            triangles++;
        }

        return triangles;
    }

    private static PsxOverbrightGltfVertex MakePsxOverbrightVertex(ModelVertex vertex)
    {
        return new PsxOverbrightGltfVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new PsxOverbrightVertexColor1Texture1(vertex));
    }

    private static int AddSkinnedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, HighPrecisionVertexColor1Texture1, VertexJoints4> prim,
        ModelPrimitive primitive,
        ModelSkinBinding skin)
    {
        ValidateCompleteTriangleIndices(primitive);
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
            new HighPrecisionVertexColor1Texture1(vertex.Color, vertex.TexCoord),
            new VertexJoints4(
                (influences.Joint0, influences.Weight0),
                (influences.Joint1, influences.Weight1),
                (influences.Joint2, influences.Weight2),
                (influences.Joint3, influences.Weight3)));
    }

    private static int AddPsxAnimatedSkinnedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, PsxAnimatedVertexColor1Texture1, VertexJoints4> prim,
        ModelPrimitive primitive,
        ModelSkinBinding skin)
    {
        ValidateCompleteTriangleIndices(primitive);
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
                MakePsxAnimatedSkinnedVertex(primitive.Vertices[ia], skin.Influences[ia]),
                MakePsxAnimatedSkinnedVertex(primitive.Vertices[ib], skin.Influences[ib]),
                MakePsxAnimatedSkinnedVertex(primitive.Vertices[ic], skin.Influences[ic]));
            triangles++;
        }

        return triangles;
    }

    private static PsxAnimatedGltfSkinnedVertex MakePsxAnimatedSkinnedVertex(
        ModelVertex vertex,
        ModelBoneInfluences influences)
    {
        return new PsxAnimatedGltfSkinnedVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new PsxAnimatedVertexColor1Texture1(vertex),
            new VertexJoints4(
                (influences.Joint0, influences.Weight0),
                (influences.Joint1, influences.Weight1),
                (influences.Joint2, influences.Weight2),
                (influences.Joint3, influences.Weight3)));
    }

    private static int AddPsxOverbrightSkinnedTriangles(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, PsxOverbrightVertexColor1Texture1, VertexJoints4> prim,
        ModelPrimitive primitive,
        ModelSkinBinding skin)
    {
        ValidateCompleteTriangleIndices(primitive);
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
                MakePsxOverbrightSkinnedVertex(primitive.Vertices[ia], skin.Influences[ia]),
                MakePsxOverbrightSkinnedVertex(primitive.Vertices[ib], skin.Influences[ib]),
                MakePsxOverbrightSkinnedVertex(primitive.Vertices[ic], skin.Influences[ic]));
            triangles++;
        }

        return triangles;
    }

    private static void ValidateCompleteTriangleIndices(ModelPrimitive primitive)
    {
        if (primitive.Indices.Length % 3 != 0)
        {
            throw new InvalidDataException(
                $"Mesh primitive '{primitive.Name}' has {primitive.Indices.Length} indices; " +
                "triangle indices must contain complete triples.");
        }
    }

    private static PsxOverbrightGltfSkinnedVertex MakePsxOverbrightSkinnedVertex(
        ModelVertex vertex,
        ModelBoneInfluences influences)
    {
        return new PsxOverbrightGltfSkinnedVertex(
            new VertexPositionNormal(vertex.Position, vertex.Normal),
            new PsxOverbrightVertexColor1Texture1(vertex),
            new VertexJoints4(
                (influences.Joint0, influences.Weight0),
                (influences.Joint1, influences.Weight1),
                (influences.Joint2, influences.Weight2),
                (influences.Joint3, influences.Weight3)));
    }
}
