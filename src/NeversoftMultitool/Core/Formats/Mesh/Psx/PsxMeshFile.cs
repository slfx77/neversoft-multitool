using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Parsed PSX model file containing mesh geometry, objects, and texture references.
///     Supports versions 0x03 (Apocalypse/THPS1, uint32 fields), 0x04 (Spider-Man PS1/THPS2 PS1),
///     and 0x06 (DC/PC/Xbox, layout stubs). v4 and v6 use uint16 fields.
/// </summary>
public sealed class PsxMeshFile
{
    /// <summary>
    ///     Chunk tag <c>0x2A</c> — v1 hier/anim. Engine reads
    ///     <c>numBones × 24</c> bytes per bone per frame directly as an
    ///     <c>SMatrix</c> (3×3 s16 rotation + 3-vector s16 translation).
    /// </summary>
    public const uint HierChunkV1Tag = 0x2A;

    /// <summary>
    ///     Chunk tag <c>0x2C</c> — v2 hier/anim. Engine decompresses 6 per-bone
    ///     streams (Rx, Ry, Rz, Tx, Ty, Tz) via <c>DecompressStream</c> into
    ///     interleaved per-frame Euler+translation buffers.
    /// </summary>
    public const uint HierChunkV2Tag = 0x2C;

    public required ushort Version { get; init; }
    public PsxMeshFormatRevision FormatRevision { get; init; } = PsxMeshFormatRevision.Unknown;
    public required List<PsxMeshObject> Objects { get; init; }
    public required List<PsxMesh> Meshes { get; init; }
    public required uint[] MeshNameHashes { get; init; }
    public required uint[] TextureHashes { get; init; }
    public Vector4[]? GouraudPalette { get; init; }
    public IReadOnlyList<PsxColourPulse> ColourPulses { get; init; } = [];
    public bool HasHierarchy { get; init; }

    /// <summary>
    ///     True when the file is a super (character or small animated prop)
    ///     per <see cref="PsxMeshHeader.IsSuperModel" />. HIER-only level
    ///     files remain non-super and convert through the per-object level
    ///     path.
    /// </summary>
    public bool IsSuperModel { get; init; }

    public float ScaleDivisor { get; init; }
    public float TranslationDivisor { get; init; }

    /// <summary>
    ///     True when any mesh contains type-2 stitched-reference vertices —
    ///     the marker of a character super on files without a HIER table.
    ///     Always false for <see cref="ParseHeaderOnly(string)" /> results
    ///     (geometry is not read there).
    /// </summary>
    public bool HasStitchedReferences { get; init; }

    internal IReadOnlyList<PsxAttachmentVertex> AttachmentVertices { get; init; } = [];

    internal IReadOnlyDictionary<uint, PsxAttachmentVertex> AttachmentVertexMap { get; init; } =
        new Dictionary<uint, PsxAttachmentVertex>();

    internal IReadOnlyList<int> MeshToObjectIndex { get; init; } = [];

    /// <summary>
    ///     Parses a PSX file for mesh geometry.
    ///     Returns null if the file has no mesh data (texture-only library).
    ///     <paramref name="bakeColourPulses" /> stays true for every file
    ///     class — the engine ticks pulses for env, bank, and items regions
    ///     alike (see <see cref="PsxSurfaceAnimationReader" />); the flag
    ///     exists for diagnostics that need the raw serialized palette.
    /// </summary>
    public static PsxMeshFile? Parse(string filePath, bool bakeColourPulses = true)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return Parse(reader, bakeColourPulses);
    }

    /// <summary>
    ///     Parses a PSX file from an in-memory byte buffer.
    /// </summary>
    public static PsxMeshFile? Parse(byte[] data, bool bakeColourPulses = true)
    {
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        return Parse(reader, bakeColourPulses);
    }

    /// <summary>
    ///     Parses a PSX file for mesh geometry from an existing reader.
    ///     Returns null if the file has no mesh data or is invalid.
    /// </summary>
    public static PsxMeshFile? Parse(BinaryReader reader, bool bakeColourPulses = true)
    {
        // The header grammar is endian-parameterized (the N64 ports re-encode
        // it); every caller of this overload is a little-endian PS1-era file.
        // The wrapper shares the reader, so the geometry pass below resumes at
        // the position the header pass left.
        var header = PsxMeshHeaderReader.Parse(new EndianBinaryReader(reader, bigEndian: false));
        if (header == null)
            return null;

#pragma warning disable S125 // Sections of code should not be commented out (false positive: design comment)
        // Build mesh-to-first-object mapping for stitch vertex coordinate transforms.
        // Each type-2 (stitched) vertex references a type-1 vertex in a different mesh;
        // we need to convert from the source mesh's local space to the target mesh's local
        // space by accounting for their object position offsets.
#pragma warning restore S125
        var meshToObjectIndex = BuildMeshToObjectIndex(header);

        var attachmentVertices = PsxMeshGeometryReader.CollectAttachableVertices(
            reader,
            header.MeshTopPointers,
            header.Version,
            header.ScaleDivisor,
            header.Objects,
            meshToObjectIndex,
            header.TranslationDivisor,
            header.HasMeshLodField);
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
                        attachmentVertexMap,
                        header.HasMeshLodField);
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

        var hasStitchedReferences = ApplyPositionalBindingIfStitchedSuper(
            header, meshes, meshToObjectIndex, attachmentVertices);
        var surfaceAnimation = PsxSurfaceAnimationReader.ApplyStaticFrame(
            reader, header.Version, header.Objects, meshes, header.GouraudPalette,
            bakeColourPulses);

        return new PsxMeshFile
        {
            Version = header.Version,
            FormatRevision = header.FormatRevision,
            Objects = header.Objects,
            Meshes = meshes,
            MeshNameHashes = header.MeshNameHashes,
            TextureHashes = header.TextureHashes,
            GouraudPalette = surfaceAnimation.Palette,
            ColourPulses = surfaceAnimation.ColourPulses,
            HasHierarchy = header.HasHierarchy,
            IsSuperModel = header.IsSuperModel,
            ScaleDivisor = header.ScaleDivisor,
            TranslationDivisor = header.TranslationDivisor,
            HasStitchedReferences = hasStitchedReferences,
            AttachmentVertices = attachmentVertices,
            AttachmentVertexMap = attachmentVertexMap,
            MeshToObjectIndex = meshToObjectIndex
        };
    }

    /// <summary>
    ///     Walks the tagged-chunk chain that starts at the offset stored in
    ///     <c>data[4]</c> (the engine's <c>metaTop</c>) looking for the
    ///     hierarchy/anim chunk. Per <c>ProcessNewPSX</c> (SPOOL.cpp:884-928)
    ///     two tags both set the engine's <c>pAnimFile</c>: <c>0x2A</c> (v1
    ///     uncompressed direct-matrix SMatrix per bone per frame) and
    ///     <c>0x2C</c> (v2 DecompressStream-compressed Euler+translation).
    ///     When multiple matching chunks are present (rare but real — some
    ///     prototype PSX files carry both a placeholder block and the real
    ///     anim data) the engine overwrites <c>pAnimFile</c> with each, so
    ///     this returns the <b>last</b> matching chunk to mirror engine
    ///     semantics.
    ///     Returns <c>true</c> on success; <paramref name="chunkTag" /> identifies
    ///     the variant and <paramref name="chunkDataOffset" /> points to the
    ///     chunk data (engine equivalent of <c>pAnimFile</c>). Returns false on
    ///     malformed files or files without an anim/hier chunk (e.g. texture-only
    ///     libraries).
    /// </summary>
    public static bool TryGetAnimChunkTag(byte[] data, out uint chunkTag, out int chunkDataOffset)
    {
        chunkTag = 0;
        chunkDataOffset = -1;
        if (data.Length < 8) return false;

        var metaTop = BitConverter.ToInt32(data, 4);
        if (metaTop is < 8 || metaTop + 8 > data.Length)
            return false;

        var cursor = metaTop;
        var safety = 0;
        var found = false;
        while (cursor + 8 <= data.Length)
        {
            if (++safety > 256) return found;
            var tag = BitConverter.ToUInt32(data, cursor);
            if (tag == 0xFFFFFFFFu) break;
            var size = BitConverter.ToUInt32(data, cursor + 4);
            var dataOffset = cursor + 8;
            if (size > (uint)data.Length || dataOffset + size > data.Length)
                return found;

            if (tag is HierChunkV1Tag or HierChunkV2Tag)
            {
                chunkTag = tag;
                chunkDataOffset = dataOffset;
                found = true;
            }

            cursor = dataOffset + (int)size;
        }

        return found;
    }

    /// <summary>
    ///     Returns the byte offset immediately past the last mesh block in
    ///     <paramref name="data" />, i.e. where any post-mesh content (animation
    ///     packets, hierarchy/anim streams, etc.) would begin. Returns -1 if
    ///     the file is invalid or has no meshes.
    ///     Strategy: parse all meshes, then take the highest <c>MeshTopPointer</c>,
    ///     seek to it, re-parse that single mesh, and return <c>reader.BaseStream.Position</c>
    ///     — which is where that mesh's serialized data ends.
    /// </summary>
    public static long GetMeshBlockEnd(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        var header = PsxMeshHeaderReader.Parse(new EndianBinaryReader(reader, bigEndian: false));
        if (header == null || header.MeshTopPointers.Length == 0) return -1;

        var attachmentVertices = PsxMeshGeometryReader.CollectAttachableVertices(
            reader,
            header.MeshTopPointers,
            header.Version,
            header.ScaleDivisor,
            header.Objects,
            BuildMeshToObjectIndex(header),
            header.TranslationDivisor,
            header.HasMeshLodField);
        var attachmentVertexMap = attachmentVertices.ToDictionary(a => a.AttachmentIndex);

        var lastEnd = 0L;
        foreach (var pointer in header.MeshTopPointers)
        {
            if (pointer >= data.Length) continue;
            reader.BaseStream.Seek(pointer, SeekOrigin.Begin);
            try
            {
                PsxMeshGeometryReader.ReadMesh(
                    reader,
                    header.Version,
                    header.ScaleDivisor,
                    header.TextureHashes,
                    attachmentVertexMap,
                    header.HasMeshLodField);
            }
            catch (EndOfStreamException)
            {
                /* truncated mesh; ignore */
            }

            if (reader.BaseStream.Position > lastEnd)
                lastEnd = reader.BaseStream.Position;
        }

        return lastEnd;
    }

    /// <summary>
    ///     Flat stitched supers bind parts positionally (ppModels[part] 1:1 —
    ///     see PsxMeshSemantics.UsesCharacterObjectOrder). Stitch presence is
    ///     only known once the meshes are parsed, so this redoes the
    ///     provisional obj.MeshIndex-based mapping and attachment placement in
    ///     part order. Returns whether any stitched-reference vertex exists.
    /// </summary>
    private static bool ApplyPositionalBindingIfStitchedSuper(
        PsxMeshHeader header,
        List<PsxMesh> meshes,
        int[] meshToObjectIndex,
        List<PsxAttachmentVertex> attachmentVertices)
    {
        var hasStitchedReferences = meshes.Any(static mesh =>
            mesh.Vertices.Any(static vertex =>
                PsxMeshSemantics.IsExactStitchedReference(vertex.Type)));

        if (header.HasHierarchy || !hasStitchedReferences
                                || header.Objects.Count != meshes.Count)
        {
            return hasStitchedReferences;
        }

        for (var i = 0; i < meshToObjectIndex.Length; i++)
            meshToObjectIndex[i] = i < header.Objects.Count ? i : -1;

        foreach (var attachment in attachmentVertices)
        {
            if (attachment.MeshIndex >= header.Objects.Count) continue;
            attachment.ObjectIndex = attachment.MeshIndex;
            attachment.WorldPosition = attachment.LocalPosition +
                                       PsxMeshSemantics.GetObjectOffset(
                                           header.Objects[attachment.MeshIndex],
                                           header.TranslationDivisor);
        }

        return true;
    }

    private static int[] BuildMeshToObjectIndex(PsxMeshHeader header)
    {
        var meshToObjectIndex = new int[header.MeshTopPointers.Length];
        Array.Fill(meshToObjectIndex, -1);
        if (header.HasHierarchy)
        {
            for (var i = 0; i < header.Objects.Count; i++)
                if (i < meshToObjectIndex.Length)
                    meshToObjectIndex[i] = i;
        }
        else
        {
            for (var i = 0; i < header.Objects.Count; i++)
            {
                var meshIndex = header.Objects[i].MeshIndex;
                if (meshIndex < meshToObjectIndex.Length && meshToObjectIndex[meshIndex] == -1)
                    meshToObjectIndex[meshIndex] = i;
            }
        }

        return meshToObjectIndex;
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
        return ParseHeaderOnly(stream, bigEndian: false);
    }

    /// <summary>
    ///     <see cref="ParseHeaderOnly(string)" /> overload that consumes an
    ///     in-memory byte buffer — useful when the bytes are already loaded
    ///     for other purposes (e.g. <see cref="GetMeshBlockEnd" />).
    /// </summary>
    public static PsxMeshFile? ParseHeaderOnly(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        return ParseHeaderOnly(stream, bigEndian: false);
    }

    /// <summary>
    ///     Header-only parse in a declared byte order. The N64 ports keep this
    ///     container field for field and re-encode it big-endian, so the same
    ///     grammar serves both — see <see cref="PsxN64ShellFile" />. Only the
    ///     HEADER is endian-parameterized; the geometry pass is PS1-only,
    ///     because the ports replaced geometry outright rather than re-encoding
    ///     it.
    /// </summary>
    internal static PsxMeshFile? ParseHeaderOnly(byte[] data, bool bigEndian, bool hasGeometry = true)
    {
        using var stream = new MemoryStream(data, false);
        return ParseHeaderOnly(stream, bigEndian, hasGeometry);
    }

    private static PsxMeshFile? ParseHeaderOnly(Stream stream, bool bigEndian, bool hasGeometry = true)
    {
        using var reader = new EndianBinaryReader(stream, bigEndian);
        var header = PsxMeshHeaderReader.Parse(reader, hasGeometry);
        if (header == null)
            return null;

        return new PsxMeshFile
        {
            Version = header.Version,
            FormatRevision = header.FormatRevision,
            Objects = header.Objects,
            Meshes = [],
            MeshNameHashes = header.MeshNameHashes,
            TextureHashes = header.TextureHashes,
            GouraudPalette = header.GouraudPalette,
            HasHierarchy = header.HasHierarchy,
            IsSuperModel = header.IsSuperModel,
            ScaleDivisor = header.ScaleDivisor,
            TranslationDivisor = header.TranslationDivisor
        };
    }
}
