using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Psx;

internal static class PsxMeshGeometryReader
{
    /// <summary>
    ///     First pass: scans all meshes to collect type-1 (attachable) vertices.
    ///     These are joint anchor vertices at body part boundaries. Each type-1 vertex
    ///     gets a file-wide sequential index used by type-2 vertices for stitching.
    ///     For hierarchical models (characters), positions are stored in WORLD space
    ///     (local + object offset) so type-2 vertices in other meshes can be correctly
    ///     placed regardless of their different object offsets.
    /// </summary>
    internal static Dictionary<uint, Vector3> CollectAttachableVertices(
        BinaryReader reader, uint[] meshTopPointers, ushort version, float scaleDivisor,
        bool hasHierarchy, List<PsxMeshObject> objects, int[] meshToObjectIndex,
        float translationDivisor)
    {
        var attachableVertices = new Dictionary<uint, Vector3>();
        uint attachmentIndex = 0;

        for (var meshIndex = 0; meshIndex < meshTopPointers.Length; meshIndex++)
        {
            var objectOffset = Vector3.Zero;
            if (hasHierarchy && meshToObjectIndex[meshIndex] >= 0)
            {
                var obj = objects[meshToObjectIndex[meshIndex]];
                objectOffset = new Vector3(
                    obj.X(translationDivisor),
                    obj.Y(translationDivisor),
                    obj.Z(translationDivisor));
            }

            reader.BaseStream.Seek(meshTopPointers[meshIndex], SeekOrigin.Begin);

            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            var vertexCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();

            reader.ReadBytes(16);

            if (version != 0x03 || ProbeV3HasLod(reader))
                reader.ReadBytes(4);

            for (uint vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                var x = reader.ReadInt16();
                var y = reader.ReadInt16();
                var z = reader.ReadInt16();
                var type = reader.ReadUInt16();

                Vector3 position;
                if ((type & 0x02) != 0)
                {
                    var attachIndex = (uint)(ushort)y;
                    if (attachableVertices.TryGetValue(attachIndex, out var resolvedWorld))
                        position = resolvedWorld;
                    else
                        position = objectOffset;
                }
                else
                {
                    position = new Vector3(x / scaleDivisor, y / scaleDivisor, z / scaleDivisor) + objectOffset;
                }

                if ((type & 0x01) != 0)
                {
                    attachableVertices[attachmentIndex] = position;
                    attachmentIndex++;
                }
            }
        }

        return attachableVertices;
    }

    internal static PsxMesh ReadMesh(BinaryReader reader, ushort version, float scaleDivisor,
        uint[] textureHashes, Dictionary<uint, Vector3>? attachableVertices = null,
        Vector3 objectOffset = default)
    {
        _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var vertexCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var normalCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var faceCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();

        reader.ReadUInt32();
        reader.ReadBytes(12);

        var lodDepth = short.MaxValue;
        var lodNextMeshIndex = ushort.MaxValue;
        if (version != 0x03 || ProbeV3HasLod(reader))
        {
            lodDepth = reader.ReadInt16();
            lodNextMeshIndex = reader.ReadUInt16();
        }

        var vertices = new List<PsxVertex>((int)vertexCount);
        var stitchFailures = 0;
        for (uint vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            var type = reader.ReadUInt16();

            float vx;
            float vy;
            float vz;

            if ((type & 0x02) != 0 && attachableVertices != null)
            {
                var attachIndex = (uint)(ushort)y;
                if (attachableVertices.TryGetValue(attachIndex, out var worldPos))
                {
                    var localPos = worldPos - objectOffset;
                    vx = localPos.X;
                    vy = localPos.Y;
                    vz = localPos.Z;
                }
                else
                {
                    vx = 0;
                    vy = 0;
                    vz = 0;
                    stitchFailures++;
                }
            }
            else
            {
                vx = x / scaleDivisor;
                vy = y / scaleDivisor;
                vz = z / scaleDivisor;
            }

            vertices.Add(new PsxVertex
            {
                X = vx,
                Y = vy,
                Z = vz,
                Type = type,
                RawX = x,
                RawY = y,
                RawZ = z
            });
        }

        var normals = new List<PsxNormal>((int)normalCount);
        for (uint normalIndex = 0; normalIndex < normalCount; normalIndex++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            reader.ReadUInt16();
            normals.Add(new PsxNormal
            {
                X = x / 4096f,
                Y = y / 4096f,
                Z = z / 4096f
            });
        }

        var faces = new List<PsxFace>((int)faceCount);
        for (uint faceIndex = 0; faceIndex < faceCount; faceIndex++)
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
            StitchFailureCount = stitchFailures
        };
    }

    /// <summary>
    ///     Probes whether a v3 mesh header has a 4-byte LOD field between the bounding box and
    ///     vertex data. THPS1 Proto (1999) v3 files have it (sentinel 0x7FFF/0xFFFF);
    ///     Apocalypse (1998) v3 files do not. Peeks ahead without advancing the stream.
    /// </summary>
    private static bool ProbeV3HasLod(BinaryReader reader)
    {
        var savedPos = reader.BaseStream.Position;
        if (savedPos + 12 > reader.BaseStream.Length)
            return false;

        reader.BaseStream.Seek(savedPos + 6, SeekOrigin.Begin);
        var typeA = reader.ReadUInt16();
        reader.BaseStream.Seek(savedPos + 10, SeekOrigin.Begin);
        var typeB = reader.ReadUInt16();
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);

        var validA = (typeA & ~0x13) == 0;
        var validB = (typeB & ~0x13) == 0;

        if (validA && !validB) return false;
        if (!validA && validB) return true;

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

        if ((faceFlags & 0x0040) == 0)
            faceFlags ^= 0x0080;

        if ((faceFlags & 0x00C0) == 0)
        {
            reader.BaseStream.Seek(facePosition + (faceLength & 0xFFFC), SeekOrigin.Begin);
            return null;
        }

        var hasTextureIndex = (faceFlags & 0x0001) != 0;
        var hasTextureCoords = (faceFlags & 0x0003) == 0x0003;
        var isTextured = (faceFlags & 0x0003) != 0;
        var quad = (faceFlags & 0x0010) == 0;
        var semiTrans = (faceFlags & 0x0040) != 0;
        var gouraud = (faceFlags & 0x0800) != 0;
        var flag0008 = (faceFlags & 0x0008) != 0;
        var flag0020 = (faceFlags & 0x0020) != 0;

        uint i0;
        uint i1;
        uint i2;
        uint i3;
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

        var r = reader.ReadByte();
        var g = reader.ReadByte();
        var b = reader.ReadByte();
        var mode = reader.ReadByte();

        var normalIndex = reader.ReadUInt16();
        reader.ReadInt16();

        uint textureIndex = 0;
        byte u0 = 0, v0 = 0, u1 = 0, v1 = 0, u2 = 0, v2 = 0, u3 = 0, v3 = 0;
        if (hasTextureIndex)
        {
            textureIndex = reader.ReadUInt32();
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

        if (flag0008)
            reader.ReadBytes(8);

        if (hasTextureIndex && flag0020)
            reader.ReadUInt32();

        reader.BaseStream.Seek(facePosition + (faceLength & 0xFFFC), SeekOrigin.Begin);

        if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
            return null;
        if (quad && i3 >= vertexCount)
            return null;
        if (normalIndex >= normalCount)
            return null;

        uint textureHash = 0;
        if (hasTextureIndex && textureIndex < (uint)textureHashes.Length)
            textureHash = textureHashes[textureIndex];

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
            U0 = u0,
            V0 = v0,
            U1 = u1,
            V1 = v1,
            U2 = u2,
            V2 = v2,
            U3 = u3,
            V3 = v3
        };
    }
}
