using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static partial class ModelDocumentGeometryAdapter
{
    private static (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) ComputePsxFaceColors(
        ushort version,
        PsxFace face,
        Vector4[]? gouraudPalette)
    {
        if (face.IsGouraud && gouraudPalette != null && version != 0x06)
        {
            var c0 = face.R < gouraudPalette.Length ? gouraudPalette[face.R] : Vector4.One;
            var c1 = face.G < gouraudPalette.Length ? gouraudPalette[face.G] : Vector4.One;
            var c2 = face.B < gouraudPalette.Length ? gouraudPalette[face.B] : Vector4.One;
            var c3 = face.IsQuad && face.Mode < gouraudPalette.Length ? gouraudPalette[face.Mode] : c0;
            return (c0, c1, c2, c3);
        }

        var flat = face.IsGouraud
            ? Vector4.One
            : new Vector4(
                Math.Min(face.R / 128f, 1f),
                Math.Min(face.G / 128f, 1f),
                Math.Min(face.B / 128f, 1f),
                1f);
        return (flat, flat, flat, flat);
    }

    private static Vector3 ComputePsxVertexNormal(PsxMesh mesh, PsxFace face, uint vertexIndex)
    {
        var normalIndex = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? vertexIndex
            : face.NormalIndex;
        if (normalIndex >= mesh.Normals.Count)
            return Vector3.UnitY;

        var normal = mesh.Normals[(int)normalIndex];
        return NormalizeOrDefault(new Vector3(normal.X, -normal.Y, -normal.Z));
    }

    private static Vector2 ComputePsxTextureUv(
        ushort version,
        PsxFace face,
        int u,
        int v,
        int texWidth,
        int texHeight)
    {
        if (!face.IsTextured)
            return Vector2.Zero;

        // v6 (Spider-Man DC/PC port containers) stores UVs as u16/i16 pairs
        // in a fixed 512-texel normalized space instead of the PS1-era byte
        // texel coordinates that address the texture directly. The /512 is an
        // EMPIRICAL constant — established against the BLACKCAT.PSX locked
        // fixture and the DC/PC level sweeps (validator-clean, textures land
        // correctly) — not a decomp-verified contract; the DC exe is SH-4,
        // outside the MIPS decomp toolkit.
        return version == 0x06
            ? new Vector2(u / 512f, v / 512f)
            : new Vector2(u / (float)Math.Max(texWidth, 1), v / (float)Math.Max(texHeight, 1));
    }

    private static uint GetPsxFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static bool UsesCombinedPsxCharacterAssembly(PsxMeshFile psxFile)
    {
        // A HIER chunk alone is not a character marker: level files carry one
        // for their placed animated objects (THPS1-proto skdown/skvans) and
        // must stay on the per-object level path — routing them through the
        // combined skinned assembly scatters hundreds of meshes across bind
        // pivots ("dust" renders). IsSuperModel bounds the part count.
        return psxFile.HasStitchedReferences ||
               (psxFile.HasHierarchy && psxFile.IsSuperModel);
    }

    private static HashSet<int> BuildPsxLodVariantSet(PsxMeshFile psxFile)
    {
        return psxFile.Meshes
            .Select(static mesh => (int)mesh.LodNextMeshIndex)
            .Where(index => index != ushort.MaxValue && index < psxFile.Meshes.Count)
            .ToHashSet();
    }

    private static string ResolvePsxMeshName(PsxMeshFile psxFile, int meshIndex)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length ? psxFile.MeshNameHashes[meshIndex] : 0u;
        return ResolveQbName(nameHash, $"mesh_{meshIndex:X8}");
    }
}
