using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

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

    internal IReadOnlyList<PsxAttachmentVertex> AttachmentVertices { get; init; } = [];

    internal IReadOnlyDictionary<uint, PsxAttachmentVertex> AttachmentVertexMap { get; init; } =
        new Dictionary<uint, PsxAttachmentVertex>();

    internal IReadOnlyList<int> MeshToObjectIndex { get; init; } = [];

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
    ///     Parses a PSX file from an in-memory byte buffer.
    /// </summary>
    public static PsxMeshFile? Parse(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
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
        if (header.HasHierarchy)
        {
            // Hierarchical (character) models: mesh pointer N belongs to object N.
            // Extra mesh pointers beyond Objects.Count are LOD variants with no object.
            for (var objectIndex = 0; objectIndex < header.Objects.Count; objectIndex++)
            {
                if (objectIndex < meshToObjectIndex.Length)
                    meshToObjectIndex[objectIndex] = objectIndex;
            }
        }
        else
        {
            for (var objectIndex = 0; objectIndex < header.Objects.Count; objectIndex++)
            {
                var meshIndex = header.Objects[objectIndex].MeshIndex;
                if (meshIndex < meshToObjectIndex.Length && meshToObjectIndex[meshIndex] == -1)
                    meshToObjectIndex[meshIndex] = objectIndex;
            }
        }

        var attachmentVertices = PsxMeshGeometryReader.CollectAttachableVertices(
            reader,
            header.MeshTopPointers,
            header.Version,
            header.ScaleDivisor,
            header.Objects,
            meshToObjectIndex,
            header.TranslationDivisor);
        var attachmentVertexMap = attachmentVertices.ToDictionary(a => a.AttachmentIndex);

        var meshes = new List<PsxMesh>(header.MeshTopPointers.Length);
        var streamLength = reader.BaseStream.Length;
        for (var meshIndex = 0; meshIndex < header.MeshTopPointers.Length; meshIndex++)
        {
            var pointer = header.MeshTopPointers[meshIndex];
            PsxMesh? mesh = null;
            if (pointer < streamLength)
            {
                reader.BaseStream.Seek(pointer, SeekOrigin.Begin);
                try
                {
                    mesh = PsxMeshGeometryReader.ReadMesh(
                        reader,
                        header.Version,
                        header.ScaleDivisor,
                        header.TextureHashes,
                        attachmentVertexMap);
                }
                catch (EndOfStreamException)
                {
                    // Truncated mesh (e.g. SkHvn.psx on THPS2X Xbox) — substitute an empty mesh
                    // placeholder so object.MeshIndex references remain valid for downstream code.
                }
            }

            meshes.Add(mesh ?? new PsxMesh
            {
                Vertices = [],
                Normals = [],
                Faces = []
            });
        }

        foreach (var attachment in attachmentVertices)
        {
            if (attachment.MeshIndex >= meshes.Count) continue;
            if (attachment.VertexIndex >= meshes[attachment.MeshIndex].Vertices.Count) continue;

            meshes[attachment.MeshIndex].Vertices[attachment.VertexIndex].AttachmentIndex =
                attachment.AttachmentIndex;
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
            TranslationDivisor = header.TranslationDivisor,
            AttachmentVertices = attachmentVertices,
            AttachmentVertexMap = attachmentVertexMap,
            MeshToObjectIndex = meshToObjectIndex
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
