using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Emits the collision surface used by PSX-lineage levels. These games
///     ray-cast the level SModel itself. Collision transforms the serialized
///     vertex words directly, so type-bit-4 sprite corners deliberately keep
///     their raw positions here instead of using the renderer's camera-facing
///     billboard expansion. Faces whose collision function class is exactly
///     one are rejected unconditionally by the runtime face walker.
/// </summary>
internal static class PsxInlineCollisionGeometryWriter
{
    internal static bool CanPopulate(PsxMeshFile level)
    {
        // The normal level writer uses raw mesh-local vertices plus object
        // offsets. Combined character assemblies instead resolve stitched
        // references through PsxCharacterMeshResolver and can move most face
        // corners to another body part. Until a collision runtime proves those
        // actor supers are level surfaces, emitting the raw topology would not
        // coincide with the rendered model and must fail closed.
        if (PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(level))
            return false;

        // A rejected declared face means the parsed topology is incomplete.
        // Never present a partial collision surface as authoritative.
        if (level.Meshes.Any(static mesh =>
                mesh.FaceReadInfos.Count != mesh.Faces.Count + mesh.InvisibleFaces.Count))
        {
            return false;
        }

        var hasUsableTriangle = false;
        foreach (var obj in level.Objects)
        {
            if (obj.MeshIndex >= level.Meshes.Count)
                return false;

            var mesh = level.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(level, obj));
            if (!ValidateFaces(mesh.Faces) || !ValidateFaces(mesh.InvisibleFaces))
                return false;

            bool ValidateFaces(IEnumerable<PsxFace> faces)
            {
                foreach (var face in faces)
                {
                    // The runtime rejects function class one before it reads
                    // any indices, so malformed/stale indices in that class do
                    // not invalidate an otherwise exact collision surface.
                    if (!ParticipatesInRuntimeCollision(face))
                        continue;

                    if (!ValidateTriangle(face.Index0, face.Index2, face.Index1))
                        return false;
                    if (face.IsQuad
                        && !ValidateTriangle(face.Index1, face.Index2, face.Index3))
                    {
                        return false;
                    }
                }

                return true;
            }

            bool ValidateTriangle(
                uint i0,
                uint i1,
                uint i2)
            {
                if (!TryPosition(mesh, i0, offset, out var a)
                    || !TryPosition(mesh, i1, offset, out var b)
                    || !TryPosition(mesh, i2, offset, out var c))
                {
                    return false;
                }

                if (!ModelDocumentGeometryAdapter.IsDegenerate(a, b, c))
                {
                    hasUsableTriangle = true;
                }
                return true;
            }
        }

        return hasUsableTriangle;
    }

    /// <summary>
    ///     Appends a translucent, world-space surface grouped by exact source
    ///     collision flags. The runtime's unconditional non-collision face
    ///     class is omitted. Returns zero without mutating the document if any
    ///     declared face was rejected, a participating object/face reference
    ///     is out of range, or no nondegenerate collision triangle remains. Source
    ///     identity is deliberately enforced by the TRG-aware
    ///     collision-level gate before this geometry-only writer is called. In
    ///     particular, Apocalypse's <c>death.psx</c>/<c>war.psx</c> are
    ///     SpoolIn actor supers, while their <c>*_1</c> siblings are the
    ///     SpoolEnv collision-level regions.
    /// </summary>
    internal static int PopulateOverlay(ModelDocument document, PsxMeshFile level)
    {
        if (!CanPopulate(level))
            return 0;

        var groups = new SortedDictionary<FaceGroupKey, GeometryGroup>();
        var allTrianglesResolved = true;
        foreach (var obj in level.Objects)
        {
            if (obj.MeshIndex >= level.Meshes.Count)
                continue;

            var mesh = level.Meshes[obj.MeshIndex];
            var offset = PsxMeshSemantics.ToGltfPosition(
                PsxMeshSemantics.GetObjectOffset(level, obj));
            AddFaces(mesh.Faces, loaderInvisible: false);
            AddFaces(mesh.InvisibleFaces, loaderInvisible: true);

            void AddFaces(IEnumerable<PsxFace> faces, bool loaderInvisible)
            {
                foreach (var face in faces)
                {
                    if (!ParticipatesInRuntimeCollision(face))
                        continue;

                    var key = new FaceGroupKey(face.CollisionFlags, loaderInvisible);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new GeometryGroup();
                        groups.Add(key, group);
                    }

                    AddTriangle(group, face.Index0, face.Index2, face.Index1);
                    if (face.IsQuad)
                        AddTriangle(group, face.Index1, face.Index2, face.Index3);
                }
            }

            void AddTriangle(GeometryGroup group, uint i0, uint i1, uint i2)
            {
                if (!TryVertex(mesh, i0, offset, out var a)
                    || !TryVertex(mesh, i1, offset, out var b)
                    || !TryVertex(mesh, i2, offset, out var c))
                {
                    allTrianglesResolved = false;
                    return;
                }

                ModelDocumentGeometryAdapter.AddTriangle(
                    group.Vertices, group.Indices, a, b, c);
            }
        }

        // CanPopulate performs the complete preflight before any document
        // mutation. Keep this defensive check in case vertex resolution ever
        // becomes stateful between the two passes.
        if (!allTrianglesResolved)
            return 0;

        var triangleCount = groups.Values.Sum(static group => group.Indices.Count / 3);
        if (triangleCount == 0)
            return 0;

        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = "collision_overlay",
            BaseColor = new Vector4(1f, 0.28f, 0.04f, 0.38f),
            AlphaMode = ModelAlphaMode.Blend,
            DoubleSided = true,
            Unlit = true
        });

        var collisionMesh = new ModelMesh { Name = "collision_overlay" };
        foreach (var (key, group) in groups)
        {
            var suffix = key.LoaderInvisible ? "_invisible" : string.Empty;
            var primitive = ModelDocumentGeometryAdapter.AddPrimitive(
                collisionMesh,
                $"collision_flags_0x{key.CollisionFlags:X4}{suffix}",
                materialIndex,
                group.Vertices,
                group.Indices);
            primitive?.NativeMetadata.Add(new PsxCollisionFlagsRenderMetadata(
                key.CollisionFlags, key.LoaderInvisible));
        }

        ModelDocumentGeometryAdapter.AddMeshNode(
            document, "collision_overlay", collisionMesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return triangleCount;
    }

    private static bool TryVertex(
        PsxMesh mesh,
        uint index,
        Vector3 offset,
        out ModelVertex vertex)
    {
        vertex = default;
        if (!TryPosition(mesh, index, offset, out var position))
            return false;

        vertex = new ModelVertex(position, Vector3.UnitY, Vector4.One, Vector2.Zero);
        return true;
    }

    private static bool TryPosition(
        PsxMesh mesh,
        uint index,
        Vector3 offset,
        out Vector3 position)
    {
        position = default;
        if (index >= mesh.Vertices.Count)
            return false;

        var source = mesh.Vertices[(int)index];
        // The render path expands type-bit-4 vertices around their referenced
        // axis. M3dAsm_LineColijProcessVertices instead transforms every raw
        // SModel vertex identically, after the loader has left sprite X/Y
        // offsets untouched. X/Y/Z are already divided by the file's scale.
        var local = PsxMeshSemantics.ToGltfPosition(
            new Vector3(source.X, source.Y, source.Z));
        position = local + offset;
        return true;
    }

    /// <summary>
    ///     M3dAsm_LineColijProcessFaces rejects function class one before it
    ///     reads any vertex indices. Other raw bits are query-specific surface
    ///     classifications and are retained in primitive metadata.
    /// </summary>
    internal static bool ParticipatesInRuntimeCollision(PsxFace face) =>
        (face.CollisionFlags & 0x0003) != 0x0001;

    private readonly record struct FaceGroupKey(
        ushort CollisionFlags,
        bool LoaderInvisible)
        : IComparable<FaceGroupKey>
    {
        public int CompareTo(FaceGroupKey other)
        {
            var flags = CollisionFlags.CompareTo(other.CollisionFlags);
            return flags != 0 ? flags : LoaderInvisible.CompareTo(other.LoaderInvisible);
        }
    }

    private sealed class GeometryGroup
    {
        public List<ModelVertex> Vertices { get; } = [];
        public List<int> Indices { get; } = [];
    }
}

/// <summary>Preserves one PSX collision primitive's raw collision classification.</summary>
public sealed record PsxCollisionFlagsRenderMetadata(
    ushort CollisionFlags,
    bool LoaderInvisible)
    : NativeRenderMetadata("psx_collision_flags");
