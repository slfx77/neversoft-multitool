using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// Parsed PSX model file containing mesh geometry, objects, and texture references.
/// Supports versions 0x03 (Apocalypse/THPS1, uint32 fields), 0x04 (Spider-Man PS1/THPS2 PS1),
/// and 0x06 (DC/PC/Xbox, layout stubs). v4 and v6 use uint16 fields.
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
    /// Parses a PSX file for mesh geometry.
    /// Returns null if the file has no mesh data (texture-only library).
    /// </summary>
    public static PsxMeshFile? Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return Parse(reader);
    }

    /// <summary>
    /// Parses a PSX file for mesh geometry from an existing reader.
    /// Returns null if the file has no mesh data or is invalid.
    /// </summary>
    public static PsxMeshFile? Parse(BinaryReader reader)
    {
        var header = ParseHeader(reader);
        if (header == null)
            return null;

        // Build mesh→first-object mapping for stitch vertex coordinate transforms.
        // Each type-2 (stitched) vertex references a type-1 vertex in a DIFFERENT mesh;
        // we need to convert from the source mesh's local space to the target mesh's local
        // space by accounting for their object position offsets.
        var meshToObjIdx = new int[header.MeshTopPointers.Length];
        Array.Fill(meshToObjIdx, -1);
        for (var oi = 0; oi < header.Objects.Count; oi++)
        {
            var mi = header.Objects[oi].MeshIndex;
            if (mi < meshToObjIdx.Length && meshToObjIdx[mi] == -1)
                meshToObjIdx[mi] = oi;
        }

        // First pass: collect type-1 (attachable) vertices across all meshes.
        // Store WORLD positions (local + object offset) so type-2 vertices in other
        // meshes can be correctly positioned regardless of their different object offsets.
        var attachableVertices = CollectAttachableVertices(reader, header.MeshTopPointers,
            header.Version, header.ScaleDivisor, header.HasHierarchy,
            header.Objects, meshToObjIdx, header.TranslationDivisor);

        // Second pass: read meshes with type-2 vertex resolution
        var meshes = new List<PsxMesh>((int)header.MeshTopPointers.Length);
        for (var i = 0; i < header.MeshTopPointers.Length; i++)
        {
            // Compute this mesh's object offset for converting type-2 world positions to local
            var objectOffset = Vector3.Zero;
            if (header.HasHierarchy && meshToObjIdx[i] >= 0)
            {
                var obj = header.Objects[meshToObjIdx[i]];
                objectOffset = new Vector3(
                    obj.X(header.TranslationDivisor),
                    obj.Y(header.TranslationDivisor),
                    obj.Z(header.TranslationDivisor));
            }

            reader.BaseStream.Seek(header.MeshTopPointers[i], SeekOrigin.Begin);
            meshes.Add(ReadMesh(reader, header.Version, header.ScaleDivisor, header.TextureHashes,
                attachableVertices, objectOffset));
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
        };
    }

    /// <summary>
    /// Parses only the header data (objects, mesh name hashes, texture hashes) without
    /// reading mesh geometry. Use this when you need object positions and name hashes
    /// but don't need vertex/face data (e.g. DDM world placement).
    /// Returns null if the file is invalid or has no objects/meshes.
    /// </summary>
    public static PsxMeshFile? ParseHeaderOnly(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        var header = ParseHeader(reader);
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
            TranslationDivisor = header.TranslationDivisor,
        };
    }

    private sealed class PsxHeader
    {
        public required ushort Version { get; init; }
        public required List<PsxMeshObject> Objects { get; init; }
        public required uint[] MeshTopPointers { get; init; }
        public required uint[] MeshNameHashes { get; init; }
        public required uint[] TextureHashes { get; init; }
        public Vector4[]? GouraudPalette { get; init; }
        public bool HasHierarchy { get; init; }
        public float ScaleDivisor { get; init; }
        public float TranslationDivisor { get; init; }
    }

    /// <summary>
    /// Reads the PSX file header: objects, mesh pointers, tagged chunks, name hashes,
    /// and texture hashes. Does NOT read mesh geometry data.
    /// </summary>
    private static PsxHeader? ParseHeader(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version is not (0x03 or 0x04 or 0x06))
            return null;

        var magic = reader.ReadUInt16();
        if (magic != 0x0002)
            return null;

        var metaTop = reader.ReadUInt32();
        var objectCount = reader.ReadUInt32();

        if (objectCount == 0)
            return null; // Texture-only library

        // Read objects (36 bytes each)
        var objects = new List<PsxMeshObject>((int)objectCount);
        for (uint i = 0; i < objectCount; i++)
        {
            objects.Add(ReadObject(reader));
        }

        var meshCount = reader.ReadUInt32();
        if (meshCount == 0)
            return null;

        // Read mesh top pointers
        var meshTopPointers = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
        {
            meshTopPointers[i] = reader.ReadUInt32();
        }

        // Seek to tagged chunks at metaTop
        reader.BaseStream.Seek(metaTop, SeekOrigin.Begin);

        var hierarchyParents = ReadTaggedChunks(reader, objectCount, out var hasHierarchy,
            out var gouraudPalette);

        // Read mesh name hashes
        var meshNameHashes = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
        {
            meshNameHashes[i] = reader.ReadUInt32();
        }

        // Read texture hashes
        var textureHashCount = reader.ReadUInt32();
        var textureHashes = new uint[textureHashCount];
        for (uint i = 0; i < textureHashCount; i++)
        {
            textureHashes[i] = reader.ReadUInt32();
        }

        // psxprev uses a base scale divisor of 2.25, multiplied by 16 for hierarchical models.
        // This produces correctly-proportioned output matching psxprev's OBJ/DAE exports.
        const float baseScale = 2.25f;
        var scaleDivisor = hasHierarchy ? baseScale * 16f : baseScale;

        // Apply hierarchy parents to objects
        if (hierarchyParents != null)
        {
            for (var i = 0; i < Math.Min(hierarchyParents.Length, objects.Count); i++)
            {
                objects[i].ParentIndex = hierarchyParents[i] != i ? (int)hierarchyParents[i] : -1;
            }
        }

        return new PsxHeader
        {
            Version = version,
            Objects = objects,
            MeshTopPointers = meshTopPointers,
            MeshNameHashes = meshNameHashes,
            TextureHashes = textureHashes,
            GouraudPalette = gouraudPalette,
            HasHierarchy = hasHierarchy,
            ScaleDivisor = scaleDivisor,
            TranslationDivisor = baseScale,
        };
    }

    private static PsxMeshObject ReadObject(BinaryReader reader)
    {
        var flags = reader.ReadUInt32();
        var rawX = reader.ReadInt32();
        var rawY = reader.ReadInt32();
        var rawZ = reader.ReadInt32();
        reader.ReadUInt32(); // unk1
        reader.ReadUInt16(); // unk2
        var meshIndex = reader.ReadUInt16();
        reader.ReadInt16(); // tx
        reader.ReadInt16(); // ty
        reader.ReadUInt32(); // unk3
        reader.ReadUInt32(); // paletteTop

        return new PsxMeshObject
        {
            Flags = flags,
            RawX = rawX,
            RawY = rawY,
            RawZ = rawZ,
            MeshIndex = meshIndex,
        };
    }

    private static ushort[]? ReadTaggedChunks(BinaryReader reader, uint objectCount,
        out bool hasHierarchy, out Vector4[]? gouraudPalette)
    {
        const uint TagStop = 0xFFFFFFFF;
        const uint TagHIER = (uint)'H' | ((uint)'I' << 8) | ((uint)'E' << 16) | ((uint)'R' << 24);
        const uint TagRGBs = (uint)'R' | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'s' << 24);

        hasHierarchy = false;
        gouraudPalette = null;
        ushort[]? hierarchyParents = null;

        var tag = reader.ReadUInt32();
        var chunkCount = 0;
        while (tag != TagStop)
        {
            var length = reader.ReadUInt32();
            var chunkStart = reader.BaseStream.Position;

            if (tag == TagHIER)
            {
                hasHierarchy = true;
                var count = Math.Min(length / 2, objectCount);
                hierarchyParents = new ushort[count];
                for (uint i = 0; i < count; i++)
                {
                    hierarchyParents[i] = reader.ReadUInt16();
                }
            }
            else if (tag == TagRGBs)
            {
                var count = Math.Min(length / 4, 256u);
                gouraudPalette = new Vector4[count];
                var specialStarted = false;
                for (uint i = 0; i < count; i++)
                {
                    var r = reader.ReadByte();
                    var g = reader.ReadByte();
                    var b = reader.ReadByte();
                    reader.ReadByte(); // pad

                    if (r == 0 && g == 0 && b == 0)
                    {
                        // psxprev workaround: second+ black entries become grey
                        // (surface has special color properties not yet understood)
                        gouraudPalette[i] = specialStarted
                            ? new Vector4(0.5f, 0.5f, 0.5f, 1f)
                            : new Vector4(0f, 0f, 0f, 1f);
                        specialStarted = true;
                    }
                    else
                    {
                        gouraudPalette[i] = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    }
                }
            }

            reader.BaseStream.Seek(chunkStart + length, SeekOrigin.Begin);
            tag = reader.ReadUInt32();

            if (++chunkCount > 16)
                break;
        }

        return hierarchyParents;
    }

    /// <summary>
    /// First pass: scans all meshes to collect type-1 (attachable) vertices.
    /// These are joint anchor vertices at body part boundaries. Each type-1 vertex
    /// gets a file-wide sequential index used by type-2 vertices for stitching.
    /// For hierarchical models (characters), positions are stored in WORLD space
    /// (local + object offset) so type-2 vertices in other meshes can be correctly
    /// placed regardless of their different object offsets.
    /// Confirmed by Ghidra decompilation: M3dInit_ParsePSX iterates meshes in order,
    /// incrementing a global counter for type-1 vertices. The stitch buffer is
    /// populated at render time by M3dAsm_TransformAndOutcodeSuperVertices.
    /// </summary>
    private static Dictionary<uint, Vector3> CollectAttachableVertices(
        BinaryReader reader, uint[] meshTopPointers, ushort version, float scaleDivisor,
        bool hasHierarchy, List<PsxMeshObject> objects, int[] meshToObjIdx,
        float translationDivisor)
    {
        var attachableVertices = new Dictionary<uint, Vector3>();
        uint attachmentIndex = 0;

        for (var i = 0; i < meshTopPointers.Length; i++)
        {
            // For hierarchical models, get the object offset for this mesh
            var objectOffset = Vector3.Zero;
            if (hasHierarchy && meshToObjIdx[i] >= 0)
            {
                var obj = objects[meshToObjIdx[i]];
                objectOffset = new Vector3(
                    obj.X(translationDivisor),
                    obj.Y(translationDivisor),
                    obj.Z(translationDivisor));
            }

            reader.BaseStream.Seek(meshTopPointers[i], SeekOrigin.Begin);

            // Read mesh header (same structure as ReadMesh)
            // v3 uses uint32 header fields; v4/v6 use uint16.
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16(); // meshFlags
            var vertexCount = version == 0x03 ? reader.ReadUInt32() : (uint)reader.ReadUInt16();
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16(); // normalCount
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16(); // faceCount

            // Skip radius (4 bytes) + bounding box (12 bytes)
            reader.ReadBytes(16);

            // v4/v6 always have LOD; v3 varies (probe required)
            if (version != 0x03 || ProbeV3HasLod(reader))
                reader.ReadBytes(4);

            // Scan vertices for stitch sources (type bit 0) and stitched (type bit 1).
            // Decompilation order: stitched resolves first, then stitch sources are counted.
            // A type-3 vertex is BOTH stitched (resolve from prior source) AND a stitch source
            // (counted for others to reference). The resolved position becomes the source position.
            for (uint j = 0; j < vertexCount; j++)
            {
                var x = reader.ReadInt16();
                var y = reader.ReadInt16();
                var z = reader.ReadInt16();
                var type = reader.ReadUInt16();

                // Bit 1 (stitched): Y field is an attachment index, not a Y coordinate.
                // Resolve position from previously-collected stitch sources.
                Vector3 position;
                if ((type & 0x02) != 0)
                {
                    var attachIdx = (uint)(ushort)y;
                    if (attachableVertices.TryGetValue(attachIdx, out var resolvedWorld))
                        position = resolvedWorld; // Already in world space
                    else
                        position = objectOffset; // Unresolved — game would read uninitialized stitch buffer
                }
                else
                {
                    position = new Vector3(x / scaleDivisor, y / scaleDivisor, z / scaleDivisor) + objectOffset;
                }

                // Bit 0 (stitch source): store world position for other meshes to reference
                if ((type & 0x01) != 0)
                {
                    attachableVertices[attachmentIndex] = position;
                    attachmentIndex++;
                }
            }
        }

        return attachableVertices;
    }

    private static PsxMesh ReadMesh(BinaryReader reader, ushort version, float scaleDivisor,
        uint[] textureHashes, Dictionary<uint, Vector3>? attachableVertices = null,
        Vector3 objectOffset = default)
    {
        // v3 uses uint32 header fields; v4/v6 use uint16.
        // meshFlags is read but not used (contains LOD/rendering hints)
        _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var vertexCount = version == 0x03 ? reader.ReadUInt32() : (uint)reader.ReadUInt16();
        var normalCount = version == 0x03 ? reader.ReadUInt32() : (uint)reader.ReadUInt16();
        var faceCount = version == 0x03 ? reader.ReadUInt32() : (uint)reader.ReadUInt16();

        // Radius (uint32) + bounding box (6 x int16) = 16 bytes
        reader.ReadUInt32(); // radius
        reader.ReadBytes(12); // xMax, xMin, yMax, yMin, zMax, zMin

        // v4/v6 always have LOD fields. v3 varies: THPS1 Proto (1999) has them,
        // Apocalypse (1998) does not. Probe by checking vertex type validity.
        short lodDepth = short.MaxValue;
        ushort lodNextMeshIndex = ushort.MaxValue;
        if (version != 0x03 || ProbeV3HasLod(reader))
        {
            lodDepth = reader.ReadInt16();
            lodNextMeshIndex = reader.ReadUInt16();
        }

        // Read vertices (8 bytes each: x,y,z as int16, type as uint16).
        // Type bits are independent flags (confirmed by M3dInit_ParsePSX decompilation):
        //   bit 0 (0x01): stitch source — counted for attachment indexing
        //   bit 1 (0x02): stitched — Y field is attachment index, not a coordinate
        //   bit 4 (0x10): sprite billboard — position is metadata, not geometry
        // Decompilation order: stitched (bit 1) → sprite (bit 4) → stitch source (bit 0).
        var vertices = new List<PsxVertex>((int)vertexCount);
        var stitchFailures = 0;
        for (uint j = 0; j < vertexCount; j++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            var type = reader.ReadUInt16();

            float vx, vy, vz;

            if ((type & 0x02) != 0 && attachableVertices != null)
            {
                // Bit 1 (stitched): Y is an attachment index, not a Y coordinate.
                // Look up the stitch source's world position and convert to local space.
                var attachIdx = (uint)(ushort)y;
                if (attachableVertices.TryGetValue(attachIdx, out var worldPos))
                {
                    var localPos = worldPos - objectOffset;
                    vx = localPos.X; vy = localPos.Y; vz = localPos.Z;
                }
                else
                {
                    // Unresolved — game would read uninitialized stitch buffer.
                    // Place at object origin rather than treating the index as a coordinate.
                    vx = 0; vy = 0; vz = 0;
                    stitchFailures++;
                }
            }
            else
            {
                // Normal, stitch source (bit 0), and sprite (bit 4) vertices all use real coordinates.
                // M3dInit_ParsePSX only flags sprite meshes (0x100); sprite vertices go through
                // normal GTE transforms — their positions are not zeroed.
                vx = x / scaleDivisor; vy = y / scaleDivisor; vz = z / scaleDivisor;
            }

            vertices.Add(new PsxVertex
            {
                X = vx, Y = vy, Z = vz,
                Type = type, RawX = x, RawY = y, RawZ = z,
            });
        }

        // Read normals (8 bytes each: x,y,z as int16, pad as uint16)
        var normals = new List<PsxNormal>((int)normalCount);
        for (uint j = 0; j < normalCount; j++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            reader.ReadUInt16(); // pad
            normals.Add(new PsxNormal
            {
                X = x / 4096f,
                Y = y / 4096f,
                Z = z / 4096f,
            });
        }

        // Read faces
        var faces = new List<PsxFace>((int)faceCount);
        for (uint j = 0; j < faceCount; j++)
        {
            var face = ReadFace(reader, version, vertexCount, normalCount, textureHashes);
            if (face != null)
                faces.Add(face);
        }

        return new PsxMesh
        {
            Vertices = vertices,
            Normals = normals,
            Faces = faces,
            LodDepth = lodDepth,
            LodNextMeshIndex = lodNextMeshIndex,
            HasPerVertexNormals = normalCount == vertexCount + faceCount,
            VertexCount = vertexCount,
            StitchFailureCount = stitchFailures,
        };
    }

    /// <summary>
    /// Probes whether a v3 mesh header has a 4-byte LOD field between the bounding box and
    /// vertex data. THPS1 Proto (1999) v3 files have it (sentinel 0x7FFF/0xFFFF);
    /// Apocalypse (1998) v3 files do not. Peeks ahead without advancing the stream.
    /// </summary>
    private static bool ProbeV3HasLod(BinaryReader reader)
    {
        var savedPos = reader.BaseStream.Position;
        if (savedPos + 12 > reader.BaseStream.Length)
            return false;

        // Candidate A (no LOD): first vertex type at current+6
        reader.BaseStream.Seek(savedPos + 6, SeekOrigin.Begin);
        var typeA = reader.ReadUInt16();
        // Candidate B (has LOD): first vertex type at current+10
        reader.BaseStream.Seek(savedPos + 10, SeekOrigin.Begin);
        var typeB = reader.ReadUInt16();
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);

        // Valid vertex types have only bits 0 (stitch source), 1 (stitched), 4 (sprite) set.
        var validA = (typeA & ~0x13) == 0;
        var validB = (typeB & ~0x13) == 0;

        if (validA && !validB) return false; // No LOD — vertices start here
        if (!validA && validB) return true;  // Has LOD — 4 bytes to skip

        // Ambiguous (both valid or both invalid): check for LOD sentinel 0x7FFF
        var peekValue = reader.ReadInt16();
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
        return peekValue == 0x7FFF;
    }

    private static PsxFace? ReadFace(BinaryReader reader, ushort version,
        uint vertexCount, uint normalCount, uint[] textureHashes)
    {
        var facePosition = reader.BaseStream.Position;

        var faceFlags = reader.ReadUInt16();
        var faceLength = reader.ReadUInt16();

        // M3dInit_ParsePSX STP bit fixup: toggle bit 7 when bit 6 (semi-trans) is clear.
        // On disk, opaque faces have bit7=0; M3dInit sets it to 1 for PS1 GPU STP processing.
        // Apply this so face flag properties reflect what the game renderer actually sees.
        if ((faceFlags & 0x0040) == 0)
            faceFlags ^= 0x0080;

        // After the STP toggle, faces with both bits 6+7 clear were originally bit7=1 on disk.
        // These are invisible faces (triggers, invisible walls) written by CreateModelData with
        // visibility bits cleared (& 0xFF3F). The toggle inverts bit7, making them detectable.
        if ((faceFlags & 0x00C0) == 0)
        {
            // Must still advance past the face data using the stride before returning.
            reader.BaseStream.Seek(facePosition + (faceLength & 0xFFFC), SeekOrigin.Begin);
            return null;
        }

        // Bit 0: face has texture index. Bits 0+1: face has full UV coordinate block.
        // Ghidra: ProcessNewPSX checks (flags & 1) for index, M3dInit_ParsePSX checks (flags & 3) == 3 for UVs.
        var hasTextureIndex = (faceFlags & 0x0001) != 0;
        var hasTextureCoords = (faceFlags & 0x0003) == 0x0003;
        var isTextured = (faceFlags & 0x0003) != 0;
        var quad = (faceFlags & 0x0010) == 0;
        var semiTrans = (faceFlags & 0x0040) != 0;
        var gouraud = (faceFlags & 0x0800) != 0;
        var flag0008 = (faceFlags & 0x0008) != 0;
        var flag0020 = (faceFlags & 0x0020) != 0;

        // Vertex indices: v3 uses uint16, v4/v6 use byte
        uint i0, i1, i2, i3;
        if (version != 0x03)
        {
            i0 = reader.ReadByte();
            i1 = reader.ReadByte();
            i2 = reader.ReadByte();
            i3 = reader.ReadByte();
        }
        else
        {
            i0 = reader.ReadUInt16();
            i1 = reader.ReadUInt16();
            i2 = reader.ReadUInt16();
            i3 = reader.ReadUInt16();
        }

        // Color: r, g, b, mode
        var r = reader.ReadByte();
        var g = reader.ReadByte();
        var b = reader.ReadByte();
        var mode = reader.ReadByte();

        // Normal index + surface flags
        var normalIndex = reader.ReadUInt16();
        reader.ReadInt16(); // surfFlags

        // Texture index + optional UV coordinates
        uint textureIndex = 0;
        byte u0 = 0, v0 = 0, u1 = 0, v1 = 0, u2 = 0, v2 = 0, u3 = 0, v3 = 0;
        if (hasTextureIndex)
        {
            textureIndex = reader.ReadUInt32();

            // UV bytes only present when BOTH bits 0 and 1 are set
            if (hasTextureCoords)
            {
                u0 = reader.ReadByte();
                v0 = reader.ReadByte();
                u1 = reader.ReadByte();
                v1 = reader.ReadByte();
                u2 = reader.ReadByte();
                v2 = reader.ReadByte();
                u3 = reader.ReadByte();
                v3 = reader.ReadByte();
            }
        }

        // Flag 0x0008: extra 8 bytes
        if (flag0008)
        {
            reader.ReadBytes(8);
        }

        // Flag 0x0020 + texture index: extra 4 bytes
        if (hasTextureIndex && flag0020)
        {
            reader.ReadUInt32();
        }

        // Face stride: upper 14 bits of dword 0 = dword count (M3dInit_ParsePSX: dataPtr + (*dataPtr >> 18)).
        // faceLength is the upper 16 bits; mask off bits 0-1 to get the correct byte stride.
        reader.BaseStream.Seek(facePosition + (faceLength & 0xFFFC), SeekOrigin.Begin);

        // Validate indices
        if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
            return null;
        if (quad && i3 >= vertexCount)
            return null;
        if (normalIndex >= normalCount)
            return null;

        // Resolve texture hash
        uint textureHash = 0;
        if (hasTextureIndex && textureIndex < (uint)textureHashes.Length)
        {
            textureHash = textureHashes[textureIndex];
        }

        return new PsxFace
        {
            Flags = faceFlags,
            IsQuad = quad,
            IsTextured = isTextured,
            IsGouraud = gouraud,
            IsSemiTransparent = semiTrans,
            Index0 = i0,
            Index1 = i1,
            Index2 = i2,
            Index3 = i3,
            NormalIndex = normalIndex,
            R = r,
            G = g,
            B = b,
            Mode = mode,
            TextureHash = textureHash,
            U0 = u0, V0 = v0,
            U1 = u1, V1 = v1,
            U2 = u2, V2 = v2,
            U3 = u3, V3 = v3,
        };
    }
}

/// <summary>
/// A PSX object entry (36 bytes). Contains world-space position and mesh index.
/// </summary>
public sealed class PsxMeshObject
{
    public uint Flags { get; init; }
    public int RawX { get; init; }
    public int RawY { get; init; }
    public int RawZ { get; init; }
    public ushort MeshIndex { get; init; }
    public int ParentIndex { get; set; } = -1;

    /// <summary>
    /// Item flag bit 1 (0x02) = character ("Super"). The game uses this to select
    /// M3dAsm_TransformAndOutcodeSuperVertices (which divides vertices by 16)
    /// vs M3dAsm_TransformAndOutcodeItemVertices (no division).
    /// </summary>
    public bool IsCharacter => (Flags & 0x02) != 0;

    public float X(float translationDivisor) => RawX / (4096f * translationDivisor);
    public float Y(float translationDivisor) => RawY / (4096f * translationDivisor);
    public float Z(float translationDivisor) => RawZ / (4096f * translationDivisor);
}

/// <summary>
/// A parsed mesh within a PSX file. Contains vertices, normals, and faces.
/// </summary>
public sealed class PsxMesh
{
    public required List<PsxVertex> Vertices { get; init; }
    public required List<PsxNormal> Normals { get; init; }
    public required List<PsxFace> Faces { get; init; }
    public short LodDepth { get; init; }
    public ushort LodNextMeshIndex { get; init; }

    /// <summary>
    /// True when normalCount == vertexCount + faceCount, meaning the first VertexCount
    /// normals are per-vertex (for smooth shading) and the rest are per-face.
    /// Confirmed by M3dInit_ParsePSX decompilation (stitch flag propagation to per-vertex normals).
    /// </summary>
    public bool HasPerVertexNormals { get; init; }

    /// <summary>Number of vertices in this mesh (needed to index per-vertex normals).</summary>
    public uint VertexCount { get; init; }

    /// <summary>
    /// Number of type-2 (stitched) vertices whose attachment index could not be resolved.
    /// Non-zero indicates stitch source ordering mismatch. These vertices are placed at (0,0,0).
    /// </summary>
    public int StitchFailureCount { get; init; }
}

/// <summary>
/// A vertex in a PSX mesh. Coordinates are pre-divided by scale divisor.
/// </summary>
public sealed class PsxVertex
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public ushort Type { get; init; }
    public short RawX { get; init; }
    public short RawY { get; init; }
    public short RawZ { get; init; }
}

/// <summary>
/// A normal vector in a PSX mesh. Pre-divided by 4096.
/// </summary>
public sealed class PsxNormal
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

/// <summary>
/// A face (primitive) in a PSX mesh. Can be a triangle or quad.
/// </summary>
public sealed class PsxFace
{
    public ushort Flags { get; init; }
    public bool IsQuad { get; init; }
    public bool IsTextured { get; init; }
    public bool IsGouraud { get; init; }
    public bool IsSemiTransparent { get; init; }
    public uint Index0 { get; init; }
    public uint Index1 { get; init; }
    public uint Index2 { get; init; }
    public uint Index3 { get; init; }
    public uint NormalIndex { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public byte Mode { get; init; }
    public uint TextureHash { get; init; }
    public byte U0 { get; init; }
    public byte V0 { get; init; }
    public byte U1 { get; init; }
    public byte V1 { get; init; }
    public byte U2 { get; init; }
    public byte V2 { get; init; }
    public byte U3 { get; init; }
    public byte V3 { get; init; }
}
