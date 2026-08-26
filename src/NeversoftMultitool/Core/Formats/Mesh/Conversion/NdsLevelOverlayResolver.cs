using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves cross-piece coplanar decals in a composited DS level.
///
///     Levels ship posters, signs and shadows as separate world-space pieces lying
///     EXACTLY on another piece's wall or floor plane — one Sk8land level carries 83
///     such piece-pair conflicts across 56 shared planes — and the hardware resolves
///     them purely by draw order: the display lists are DMA'd in manifest order and
///     equal depths keep the earlier pixel. A glTF viewer has no such order, so the
///     shared plane z-fights.
///
///     The resolution is the N64 port's proven size branch: for each same-facing
///     coplanar overlapping face pair from two different pieces, the SMALLER face is
///     the decal, and its vertices lift a fraction of a unit along the face normal.
///     Overlap is decided per face pair via in-plane bounds — never union bounds,
///     the mistake that chained the PS1's side-by-side sign panels into one floating
///     pile — and near-equal-size pairs are left alone rather than guessed at.
/// </summary>
internal static class NdsLevelOverlayResolver
{
    /// <summary>World units; ~80 raw 4.12 units, invisible at level scale.</summary>
    private const float Lift = 0.02f;

    /// <summary>Smaller must be under this fraction of larger to count as the decal.</summary>
    private const float SizeBranch = 0.9f;

    private readonly record struct PlaneKey(int Nx, int Ny, int Nz, int D);

    private sealed record Face(
        ModelPrimitive Primitive, int PieceTag, int I0, int I1, int I2,
        Vector3 Normal, float Area, Vector3 Min, Vector3 Max);

    /// <summary>Lifts decal faces in place. Returns the number of faces lifted.</summary>
    public static int Apply(ModelDocument document, IReadOnlyDictionary<ModelPrimitive, int> pieceOf)
    {
        var planes = new Dictionary<PlaneKey, List<Face>>();
        foreach (var mesh in document.Meshes)
        foreach (var primitive in mesh.Primitives)
        {
            if (!pieceOf.TryGetValue(primitive, out var piece))
                continue;
            for (var i = 0; i + 2 < primitive.Indices.Length; i += 3)
            {
                var face = Describe(primitive, piece, i);
                if (face == null)
                    continue;
                var key = KeyOf(face);
                if (!planes.TryGetValue(key, out var list))
                    planes[key] = list = [];
                list.Add(face);
            }
        }

        var liftedVertices = new HashSet<(ModelPrimitive, int)>();
        var liftedFaces = new HashSet<Face>();
        foreach (var list in planes.Values)
        {
            if (list.Count < 2)
                continue;
            for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                if (a.PieceTag == b.PieceTag)
                    continue;
                if (!Overlaps(a, b))
                    continue;

                var (decal, other) = a.Area <= b.Area ? (a, b) : (b, a);
                if (decal.Area > other.Area * SizeBranch)
                    continue;

                liftedFaces.Add(decal);
                foreach (var index in (ReadOnlySpan<int>)[decal.I0, decal.I1, decal.I2])
                {
                    if (!liftedVertices.Add((decal.Primitive, index)))
                        continue;
                    var vertex = decal.Primitive.Vertices[index];
                    decal.Primitive.Vertices[index] = vertex with
                    {
                        Position = vertex.Position + decal.Normal * Lift
                    };
                }
            }
        }

        return liftedFaces.Count;
    }

    private static Face? Describe(ModelPrimitive primitive, int piece, int at)
    {
        var i0 = primitive.Indices[at];
        var i1 = primitive.Indices[at + 1];
        var i2 = primitive.Indices[at + 2];
        var p0 = primitive.Vertices[i0].Position;
        var p1 = primitive.Vertices[i1].Position;
        var p2 = primitive.Vertices[i2].Position;
        var cross = Vector3.Cross(p1 - p0, p2 - p0);
        var length = cross.Length();
        if (length < 1e-9f)
            return null;
        var normal = cross / length;
        return new Face(primitive, piece, i0, i1, i2, normal, length * 0.5f,
            Vector3.Min(p0, Vector3.Min(p1, p2)), Vector3.Max(p0, Vector3.Max(p1, p2)));
    }

    private static PlaneKey KeyOf(Face face)
    {
        // The pieces are authored on the SAME plane, so exact keys bucket them;
        // rounding absorbs only the decode's float noise. The signed normal keeps
        // opposite-facing pairs (two-sided sheets) in different buckets, which
        // backface culling already resolves.
        var d = Vector3.Dot(face.Normal, face.Primitive.Vertices[face.I0].Position);
        return new PlaneKey(
            (int)MathF.Round(face.Normal.X * 1000),
            (int)MathF.Round(face.Normal.Y * 1000),
            (int)MathF.Round(face.Normal.Z * 1000),
            (int)MathF.Round(d * 100));
    }

    private static bool Overlaps(Face a, Face b)
    {
        return a.Min.X <= b.Max.X && b.Min.X <= a.Max.X
            && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y
            && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
    }
}
