using System.Numerics;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Loads a GLB file via SharpGLTF Schema2 API into a <see cref="RenderScene" />
///     ready for software rasterization. Handles both rigid and skinned meshes.
/// </summary>
internal static class GlbModelLoader
{
    public static RenderScene Load(string glbPath)
    {
        var model = ModelRoot.Load(glbPath);
        var scene = new RenderScene();

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh == null) continue;

            // Compute world transform for this node
            var worldMatrix = GetWorldTransform(node);

            // Check if this node uses skinning
            var skin = node.Skin;
            Matrix4x4[]? jointWorldTransforms = null;
            Matrix4x4[]? inverseBindMatrices = null;

            if (skin != null)
            {
                var jointCount = skin.JointsCount;
                jointWorldTransforms = new Matrix4x4[jointCount];
                inverseBindMatrices = new Matrix4x4[jointCount];
                for (var j = 0; j < jointCount; j++)
                {
                    var (jointNode, ibm) = skin.GetJoint(j);
                    jointWorldTransforms[j] = GetWorldTransform(jointNode);
                    inverseBindMatrices[j] = ibm;
                }
            }

            foreach (var prim in node.Mesh.Primitives)
            {
                var submesh = LoadPrimitive(prim, worldMatrix, skin,
                    jointWorldTransforms, inverseBindMatrices);
                if (submesh != null)
                {
                    scene.Submeshes.Add(submesh);
                    scene.ExpandBounds(submesh.Positions);
                }
            }
        }

        return scene;
    }

    private static RenderSubmesh? LoadPrimitive(MeshPrimitive prim, Matrix4x4 worldMatrix,
        Skin? skin, Matrix4x4[]? jointWorldTransforms, Matrix4x4[]? inverseBindMatrices)
    {
        var posAccessor = prim.GetVertexAccessor("POSITION");
        if (posAccessor == null) return null;

        var rawPositions = posAccessor.AsVector3Array();
        var vertexCount = rawPositions.Count;

        // Read optional attributes
        var normalAccessor = prim.GetVertexAccessor("NORMAL");
        var colorAccessor = prim.GetVertexAccessor("COLOR_0");
        var jointsAccessor = prim.GetVertexAccessor("JOINTS_0");
        var weightsAccessor = prim.GetVertexAccessor("WEIGHTS_0");

        // Read triangle indices
        var indexAccessor = prim.IndexAccessor;
        int[] triangles;
        if (indexAccessor != null)
        {
            var indices = indexAccessor.AsIndicesArray();
            triangles = new int[indices.Count];
            for (var i = 0; i < indices.Count; i++)
                triangles[i] = (int)indices[i];
        }
        else
        {
            // Non-indexed: sequential 0,1,2,3,4,5,...
            triangles = new int[vertexCount];
            for (var i = 0; i < vertexCount; i++)
                triangles[i] = i;
        }

        // Transform positions to world space
        var positions = new float[vertexCount * 3];

        if (skin != null && jointsAccessor != null && weightsAccessor != null &&
            jointWorldTransforms != null && inverseBindMatrices != null)
        {
            // Skinned mesh: apply joint transforms
            var joints = jointsAccessor.AsVector4Array();
            var weights = weightsAccessor.AsVector4Array();

            for (var i = 0; i < vertexCount; i++)
            {
                var pos = rawPositions[i];
                var j = joints[i];
                var w = weights[i];

                var skinned = Vector3.Zero;
                ApplyJointWeight(ref skinned, pos, (int)j.X, w.X,
                    jointWorldTransforms, inverseBindMatrices);
                ApplyJointWeight(ref skinned, pos, (int)j.Y, w.Y,
                    jointWorldTransforms, inverseBindMatrices);
                ApplyJointWeight(ref skinned, pos, (int)j.Z, w.Z,
                    jointWorldTransforms, inverseBindMatrices);
                ApplyJointWeight(ref skinned, pos, (int)j.W, w.W,
                    jointWorldTransforms, inverseBindMatrices);

                positions[i * 3] = skinned.X;
                positions[i * 3 + 1] = skinned.Y;
                positions[i * 3 + 2] = skinned.Z;
            }
        }
        else
        {
            // Rigid mesh: apply node world transform
            for (var i = 0; i < vertexCount; i++)
            {
                var pos = Vector3.Transform(rawPositions[i], worldMatrix);
                positions[i * 3] = pos.X;
                positions[i * 3 + 1] = pos.Y;
                positions[i * 3 + 2] = pos.Z;
            }
        }

        // Transform normals
        float[]? normals = null;
        if (normalAccessor != null)
        {
            var rawNormals = normalAccessor.AsVector3Array();
            normals = new float[vertexCount * 3];

            if (skin != null && jointsAccessor != null && weightsAccessor != null &&
                jointWorldTransforms != null && inverseBindMatrices != null)
            {
                // Skinned normals: apply same joint transforms as positions
                var joints = jointsAccessor.AsVector4Array();
                var weights = weightsAccessor.AsVector4Array();

                for (var i = 0; i < vertexCount; i++)
                {
                    var nrm = rawNormals[i];
                    var j = joints[i];
                    var w = weights[i];

                    var skinned = Vector3.Zero;
                    ApplyJointWeightNormal(ref skinned, nrm, (int)j.X, w.X,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyJointWeightNormal(ref skinned, nrm, (int)j.Y, w.Y,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyJointWeightNormal(ref skinned, nrm, (int)j.Z, w.Z,
                        jointWorldTransforms, inverseBindMatrices);
                    ApplyJointWeightNormal(ref skinned, nrm, (int)j.W, w.W,
                        jointWorldTransforms, inverseBindMatrices);

                    var len = skinned.Length();
                    if (len > 0.001f) skinned /= len;
                    normals[i * 3] = skinned.X;
                    normals[i * 3 + 1] = skinned.Y;
                    normals[i * 3 + 2] = skinned.Z;
                }
            }
            else
            {
                for (var i = 0; i < vertexCount; i++)
                {
                    var n = Vector3.TransformNormal(rawNormals[i], worldMatrix);
                    var len = n.Length();
                    if (len > 0.001f) n /= len;
                    normals[i * 3] = n.X;
                    normals[i * 3 + 1] = n.Y;
                    normals[i * 3 + 2] = n.Z;
                }
            }
        }

        // Read vertex colors (convert float [0-1] to byte [0-255])
        byte[]? vertexColors = null;
        if (colorAccessor != null)
        {
            var rawColors = colorAccessor.AsVector4Array();
            vertexColors = new byte[vertexCount * 4];
            for (var i = 0; i < vertexCount; i++)
            {
                var c = rawColors[i];
                vertexColors[i * 4] = (byte)Math.Clamp((int)(c.X * 255f), 0, 255);
                vertexColors[i * 4 + 1] = (byte)Math.Clamp((int)(c.Y * 255f), 0, 255);
                vertexColors[i * 4 + 2] = (byte)Math.Clamp((int)(c.Z * 255f), 0, 255);
                vertexColors[i * 4 + 3] = (byte)Math.Clamp((int)(c.W * 255f), 0, 255);
            }
        }

        // Read texture coordinates
        float[]? texCoords = null;
        var uvAccessor = prim.GetVertexAccessor("TEXCOORD_0");
        if (uvAccessor != null)
        {
            var rawUvs = uvAccessor.AsVector2Array();
            texCoords = new float[vertexCount * 2];
            for (var i = 0; i < vertexCount; i++)
            {
                texCoords[i * 2] = rawUvs[i].X;
                texCoords[i * 2 + 1] = rawUvs[i].Y;
            }
        }

        // Read material properties
        var material = prim.Material;
        var isDoubleSided = material?.DoubleSided ?? false;
        var baseColorR = 1f;
        var baseColorG = 1f;
        var baseColorB = 1f;
        var baseColorA = 1f;
        var alphaMode = 0; // OPAQUE
        var alphaCutoff = 0.5f;
        byte[]? textureData = null;
        var textureWidth = 0;
        var textureHeight = 0;

        if (material != null)
        {
            // Alpha mode
            alphaMode = material.Alpha switch
            {
                AlphaMode.MASK => 1,
                AlphaMode.BLEND => 1, // treat BLEND as MASK for now
                _ => 0
            };
            alphaCutoff = material.AlphaCutoff;

            // PBR base color factor + texture
            var pbr = material.FindChannel("BaseColor");
            if (pbr != null)
            {
                var factor = pbr.Value.Color;
                baseColorR = factor.X;
                baseColorG = factor.Y;
                baseColorB = factor.Z;
                baseColorA = factor.W;

                var tex = pbr.Value.Texture;
                if (tex != null)
                {
                    var imgContent = tex.PrimaryImage?.Content;
                    if (imgContent != null && !imgContent.Value.Content.IsEmpty)
                    {
                        using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(imgContent.Value.Content.Span);
                        textureWidth = img.Width;
                        textureHeight = img.Height;
                        textureData = new byte[textureWidth * textureHeight * 4];
                        img.CopyPixelDataTo(textureData);
                    }
                }
            }
        }

        return new RenderSubmesh
        {
            Positions = positions,
            Triangles = triangles,
            Normals = normals,
            VertexColors = vertexColors,
            IsDoubleSided = isDoubleSided,
            TexCoords = texCoords,
            TextureData = textureData,
            TextureWidth = textureWidth,
            TextureHeight = textureHeight,
            BaseColorR = baseColorR,
            BaseColorG = baseColorG,
            BaseColorB = baseColorB,
            BaseColorA = baseColorA,
            AlphaMode = alphaMode,
            AlphaCutoff = alphaCutoff
        };
    }

    private static void ApplyJointWeightNormal(ref Vector3 result, Vector3 normal,
        int jointIndex, float weight,
        Matrix4x4[] jointWorldTransforms, Matrix4x4[] inverseBindMatrices)
    {
        if (weight <= 0 || jointIndex < 0 || jointIndex >= jointWorldTransforms.Length)
            return;

        var skinMatrix = inverseBindMatrices[jointIndex] * jointWorldTransforms[jointIndex];
        result += Vector3.TransformNormal(normal, skinMatrix) * weight;
    }

    private static void ApplyJointWeight(ref Vector3 result, Vector3 position,
        int jointIndex, float weight,
        Matrix4x4[] jointWorldTransforms, Matrix4x4[] inverseBindMatrices)
    {
        if (weight <= 0 || jointIndex < 0 || jointIndex >= jointWorldTransforms.Length)
            return;

        // skinned_pos = sum(weight_i * jointWorld_i * IBM_i * vertex)
        var skinMatrix = inverseBindMatrices[jointIndex] * jointWorldTransforms[jointIndex];
        result += Vector3.Transform(position, skinMatrix) * weight;
    }

    private static Matrix4x4 GetWorldTransform(Node node)
    {
        var transform = node.LocalMatrix;
        var current = node.VisualParent;
        while (current != null)
        {
            transform *= current.LocalMatrix;
            current = current.VisualParent;
        }

        return transform;
    }
}
