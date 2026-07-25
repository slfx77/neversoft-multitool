using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Shared ModelDocument mutation core: mesh/material/texture/node insertion,
///     triangle emission, and cross-format utilities used by the per-format
///     geometry writers.
/// </summary>
internal static class ModelDocumentGeometryAdapter
{
    /// <summary>
    ///     Optional per-vertex PS2 worldzone lighting model. Null by default so
    ///     worldzone exports pass source vertex colours through unchanged; callers
    ///     can provide a value to bake an experimental ambient + N·L_sun model.
    /// </summary>
    [SuppressMessage("Performance", "CA1810",
        Justification = "intentional global; thread-safety asserted by single-threaded IR build")]
    [ThreadStatic]
    internal static Ps2WorldzoneLighting? ActivePs2WorldzoneLighting;

    internal static ModelPrimitive? AddPrimitive(
        ModelMesh mesh,
        string name,
        int materialIndex,
        List<ModelVertex> vertices,
        List<int> indices,
        ModelSkinBinding? skin = null)
    {
        if (indices.Count == 0)
            return null;

        var primitive = new ModelPrimitive
        {
            Name = name,
            MaterialIndex = materialIndex,
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            Skin = skin
        };
        mesh.Primitives.Add(primitive);
        return primitive;
    }

    internal static void AddTriangle(
        List<ModelVertex> vertices,
        List<int> indices,
        ModelVertex a,
        ModelVertex b,
        ModelVertex c)
    {
        if (IsDegenerate(a.Position, b.Position, c.Position))
            return;

        var offset = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        indices.Add(offset);
        indices.Add(offset + 1);
        indices.Add(offset + 2);
    }

    internal static void AddSkinnedTriangle(
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences,
        ModelVertex va, ModelBoneInfluences ia,
        ModelVertex vb, ModelBoneInfluences ib,
        ModelVertex vc, ModelBoneInfluences ic)
    {
        if (IsDegenerate(va.Position, vb.Position, vc.Position))
            return;

        var offset = vertices.Count;
        vertices.Add(va);
        vertices.Add(vb);
        vertices.Add(vc);
        influences.Add(ia);
        influences.Add(ib);
        influences.Add(ic);
        indices.Add(offset);
        indices.Add(offset + 1);
        indices.Add(offset + 2);
    }

    internal static int? AddMesh(ModelDocument document, ModelMesh mesh)
    {
        if (mesh.Primitives.Count == 0)
            return null;

        var meshIndex = document.Meshes.Count;
        document.Meshes.Add(mesh);
        return meshIndex;
    }

    internal static void AddMeshNode(
        ModelDocument document,
        string name,
        ModelMesh mesh,
        Matrix4x4? transform = null)
    {
        var meshIndex = AddMesh(document, mesh);
        if (!meshIndex.HasValue)
            return;

        AddMeshNode(document, name, meshIndex.Value, transform);
    }

    internal static void AddMeshNode(
        ModelDocument document,
        string name,
        int meshIndex,
        Matrix4x4? transform = null)
    {
        if ((uint)meshIndex >= (uint)document.Meshes.Count)
            return;

        var nodeIndex = document.Nodes.Count;
        document.Nodes.Add(new ModelNode
        {
            Name = name,
            MeshIndex = meshIndex,
            Transform = transform ?? Matrix4x4.Identity
        });
        EnsureScene(document).RootNodeIndices.Add(nodeIndex);
    }

    private static ModelScene EnsureScene(ModelDocument document)
    {
        if (document.Scenes.Count == 0)
            document.Scenes.Add(new ModelScene { Name = document.Name });
        return document.Scenes[0];
    }

    internal static int AddMaterial(ModelDocument document, RenderMaterial material)
    {
        document.Materials.Add(material);
        return document.Materials.Count - 1;
    }

    internal static int AddTexture(
        ModelDocument document,
        string name,
        byte[] pngBytes,
        uint? checksum = null,
        ModelTextureWrap wrapU = ModelTextureWrap.Repeat,
        ModelTextureWrap wrapV = ModelTextureWrap.Repeat,
        bool distinguishChecksumVariantsByContent = false)
    {
        for (var i = 0; i < document.Textures.Count; i++)
        {
            var texture = document.Textures[i];
            if (checksum.HasValue &&
                texture.NativeChecksum == checksum &&
                (!distinguishChecksumVariantsByContent ||
                 (texture.WrapU == wrapU &&
                  texture.WrapV == wrapV &&
                  texture.PngBytes is { } existingBytes &&
                  existingBytes.AsSpan().SequenceEqual(pngBytes))))
                return i;
            if (!checksum.HasValue && string.Equals(texture.Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        document.Textures.Add(new ModelTexture
        {
            Name = name,
            PngBytes = pngBytes,
            NativeChecksum = checksum,
            WrapU = wrapU,
            WrapV = wrapV
        });
        return document.Textures.Count - 1;
    }

    internal static void FinalizeTriangleCount(ModelDocument document)
    {
        document.TriangleCount = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.TriangleCount);
    }

    internal static Vector3 NormalizeOrDefault(Vector3 value)
    {
        var length = value.Length();
        return length > 0.001f && float.IsFinite(length) ? value / length : Vector3.UnitY;
    }

    internal static bool IsDegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        const float epsilon = 1e-8f;
        if (Vector3.DistanceSquared(a, b) <= epsilon ||
            Vector3.DistanceSquared(b, c) <= epsilon ||
            Vector3.DistanceSquared(a, c) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b - a, c - a);
        return cross.LengthSquared() <= epsilon;
    }

    internal static (Vector3, Vector3, Vector3) SortedTriangleKey(Vector3 a, Vector3 b, Vector3 c)
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

    internal static string ResolveQbName(uint checksum, string fallback)
    {
        return QbKey.QbKey.TryResolve(checksum) ?? fallback;
    }

    internal static (int Width, int Height)? TryExtractPngDimensions(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < 24)
            return null;

        var width = BinaryPrimitives.ReadInt32BigEndian(pngBytes[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(pngBytes[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }
}
