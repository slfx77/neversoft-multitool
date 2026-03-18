using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Parsed PSX model file containing mesh geometry, objects, and texture references.
///     Supports versions 0x03 (Apocalypse/THPS1, uint32 fields), 0x04 (Spider-Man PS1/THPS2 PS1),
///     and 0x06 (DC/PC/Xbox, layout stubs). v4 and v6 use uint16 fields.
/// </summary>
public sealed class PsxMeshFile
{
    public required ushort Version { get; init; }
    public required List<PsxMeshObject> Objects { get; init; }
    public required List<PsxMesh> Meshes { get; init; }
    public required uint[] MeshNameHashes { get; init; }
    public required uint[] TextureHashes { get; init; }
    public Vector4[]? GouraudPalette { get; init; }
    public bool HasHierarchy { get; init; }
    public float ScaleDivisor { get; init; }
    public float TranslationDivisor { get; init; }

    /// <summary>
    ///     Parses a PSX file for mesh geometry.
    ///     Returns null if the file has no mesh data (texture-only library).
    /// </summary>
    public static PsxMeshFile? Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return Parse(reader);
    }

    /// <summary>
    ///     Parses a PSX file for mesh geometry from an existing reader.
    ///     Returns null if the file has no mesh data or is invalid.
    /// </summary>
    public static PsxMeshFile? Parse(BinaryReader reader)
    {
        var header = PsxMeshHeaderReader.Parse(reader);
        if (header == null)
            return null;

#pragma warning disable S125 // Sections of code should not be commented out (false positive: design comment)
        // Build mesh-to-first-object mapping for stitch vertex coordinate transforms.
        // Each type-2 (stitched) vertex references a type-1 vertex in a different mesh;
        // we need to convert from the source mesh's local space to the target mesh's local
        // space by accounting for their object position offsets.
#pragma warning restore S125
        var meshToObjectIndex = new int[header.MeshTopPointers.Length];
        Array.Fill(meshToObjectIndex, -1);
        for (var objectIndex = 0; objectIndex < header.Objects.Count; objectIndex++)
        {
            var meshIndex = header.Objects[objectIndex].MeshIndex;
            if (meshIndex < meshToObjectIndex.Length && meshToObjectIndex[meshIndex] == -1)
                meshToObjectIndex[meshIndex] = objectIndex;
        }

        var attachableVertices = PsxMeshGeometryReader.CollectAttachableVertices(
            reader,
            header.MeshTopPointers,
            header.Version,
            header.ScaleDivisor,
            header.HasHierarchy,
            header.Objects,
            meshToObjectIndex,
            header.TranslationDivisor);

        var meshes = new List<PsxMesh>(header.MeshTopPointers.Length);
        for (var meshIndex = 0; meshIndex < header.MeshTopPointers.Length; meshIndex++)
        {
            var objectOffset = Vector3.Zero;
            if (header.HasHierarchy && meshToObjectIndex[meshIndex] >= 0)
            {
                var obj = header.Objects[meshToObjectIndex[meshIndex]];
                objectOffset = new Vector3(
                    obj.X(header.TranslationDivisor),
                    obj.Y(header.TranslationDivisor),
                    obj.Z(header.TranslationDivisor));
            }

            reader.BaseStream.Seek(header.MeshTopPointers[meshIndex], SeekOrigin.Begin);
            meshes.Add(PsxMeshGeometryReader.ReadMesh(
                reader,
                header.Version,
                header.ScaleDivisor,
                header.TextureHashes,
                attachableVertices,
                objectOffset));
        }

        return new PsxMeshFile
        {
            Version = header.Version,
            Objects = header.Objects,
            Meshes = meshes,
            MeshNameHashes = header.MeshNameHashes,
            TextureHashes = header.TextureHashes,
            GouraudPalette = header.GouraudPalette,
            HasHierarchy = header.HasHierarchy,
            ScaleDivisor = header.ScaleDivisor,
            TranslationDivisor = header.TranslationDivisor
        };
    }

    /// <summary>
    ///     Parses only the header data (objects, mesh name hashes, texture hashes) without
    ///     reading mesh geometry. Use this when you need object positions and name hashes
    ///     but don't need vertex/face data (e.g. DDM world placement).
    ///     Returns null if the file is invalid or has no objects/meshes.
    /// </summary>
    public static PsxMeshFile? ParseHeaderOnly(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        var header = PsxMeshHeaderReader.Parse(reader);
        if (header == null)
            return null;

        return new PsxMeshFile
        {
            Version = header.Version,
            Objects = header.Objects,
            Meshes = [],
            MeshNameHashes = header.MeshNameHashes,
            TextureHashes = header.TextureHashes,
            GouraudPalette = header.GouraudPalette,
            HasHierarchy = header.HasHierarchy,
            ScaleDivisor = header.ScaleDivisor,
            TranslationDivisor = header.TranslationDivisor
        };
    }
}
