using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

internal static class ThawSceneMeshSupport
{
    private const int SMeshHeaderSize = 224;

    public static XbxMaterial ReadMaterial(BinaryReader r)
    {
        var checksum = r.ReadUInt32();
        var nameChecksum = r.ReadUInt32();
        var numPasses = Math.Clamp(r.ReadInt32(), 0, ThawSceneFile.MaxPasses);

        r.ReadByte();
        var doubleSided = r.ReadByte() > 0;
        r.ReadUInt16();

        var opacityCutoff = r.ReadByte();
        r.ReadByte();
        var useOpacityCutoff = r.ReadUInt16() > 0;

        r.BaseStream.Position += 24;
        var drawOrder = r.ReadSingle();

        var passFlags = ReadUInt32Array(r, ThawSceneFile.MaxPasses);
        var passChecksums = ReadUInt32Array(r, ThawSceneFile.MaxPasses);
        var passColors = new Vector3[ThawSceneFile.MaxPasses];
        for (var i = 0; i < ThawSceneFile.MaxPasses; i++)
        {
            passColors[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            r.ReadSingle();
        }

        var passBlendModes = new uint[ThawSceneFile.MaxPasses];
        for (var i = 0; i < ThawSceneFile.MaxPasses; i++)
        {
            passBlendModes[i] = r.ReadUInt16();
            r.ReadInt16();
        }

        var passAddressing = ReadUInt32Array(r, ThawSceneFile.MaxPasses);
        r.BaseStream.Position += ThawSceneFile.MaxPasses * 8;
        r.BaseStream.Position += ThawSceneFile.MaxPasses * 4;
        r.BaseStream.Position += ThawSceneFile.MaxPasses * 4;
        r.BaseStream.Position += 16;

        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();
        r.ReadInt32();

        r.BaseStream.Position += 16;

        var passes = new XbxPass[numPasses];
        for (var p = 0; p < numPasses; p++)
        {
            passes[p] = new XbxPass
            {
                TextureChecksum = passChecksums[p],
                Flags = passFlags[p],
                HasColor = true,
                Color = passColors[p],
                BlendMode = passBlendModes[p],
                UAddressing = ConvertThawAddressMode(passAddressing[p] & 0xFFFF),
                VAddressing = ConvertThawAddressMode((passAddressing[p] >> 16) & 0xFFFF)
            };
        }

        return new XbxMaterial
        {
            Checksum = checksum,
            NameChecksum = nameChecksum,
            NumPasses = numPasses,
            AlphaCutoff = useOpacityCutoff ? opacityCutoff : 0,
            Sorted = MathF.Abs(drawOrder) > float.Epsilon,
            DrawOrder = drawOrder,
            SingleSided = !doubleSided,
            NoBfc = false,
            ZBias = 0,
            Grassify = false,
            Passes = passes
        };
    }

    public static XbxMesh ReadSMesh(
        BinaryReader r, int headerOffset, int offScene, int sectorFlags, XbxMaterial[] materials)
    {
        if (!CanRead(r, headerOffset, SMeshHeaderSize))
        {
            return new XbxMesh
            {
                Vertices = [],
                FaceIndices = [],
                IsPreTriangulated = true
            };
        }

        r.BaseStream.Position = headerOffset;

        var meshFlags = r.ReadUInt32();
        var spherePos = ReadVec3(r);
        var sphereRadius = r.ReadSingle();
        var materialChecksum = r.ReadUInt32();
        var vertexStride = r.ReadByte();
        r.BaseStream.Position += 3;
        r.BaseStream.Position += 4;
        r.BaseStream.Position += 2;
        r.BaseStream.Position += 2;
        r.BaseStream.Position += 2;
        var vertexCount = r.ReadUInt16();

        var faceCount0 = r.ReadInt32();
        r.BaseStream.Position += 12;
        r.BaseStream.Position += 8;
        r.BaseStream.Position += 16;
        r.BaseStream.Position += 12;

        var faceOffset0Raw = r.ReadInt32();
        r.BaseStream.Position += 28;
        var vertexOffsetRaw = r.ReadInt32();

        var passCount = GetPassCount(materials, materialChecksum);
        var isBillboard = (sectorFlags & ThawSceneFile.SecflagBillboard) != 0;

        ushort[] faceIndices = [];
        if (faceCount0 > 0 && faceOffset0Raw > 0)
        {
            var faceOffset = offScene + faceOffset0Raw;
            var faceBytes = 4L + faceCount0 * 2L;
            if (CanRead(r, faceOffset, faceBytes))
            {
                r.BaseStream.Position = faceOffset;
                r.ReadInt32();
                var rawIndices = new ushort[faceCount0];
                for (var i = 0; i < faceCount0; i++)
                    rawIndices[i] = r.ReadUInt16();

                faceIndices = TriangulateStrips(rawIndices, isBillboard);
            }
        }

        XbxVertex[] vertices = [];
        if (vertexCount > 0 && vertexOffsetRaw > 0 && vertexStride > 0)
        {
            var vertexOffset = offScene + vertexOffsetRaw;
            var vertexBytes = (long)vertexCount * vertexStride;
            if (CanRead(r, vertexOffset, vertexBytes))
            {
                r.BaseStream.Position = vertexOffset;
                vertices = ReadVertexBuffer(r, sectorFlags, vertexCount, vertexStride, passCount);
            }
        }

        return new XbxMesh
        {
            BsphereCenter = spherePos,
            BsphereRadius = sphereRadius,
            MeshFlags = meshFlags,
            MaterialChecksum = materialChecksum,
            Vertices = vertices,
            FaceIndices = faceIndices,
            IsPreTriangulated = true
        };
    }

    private static uint ConvertThawAddressMode(uint mode)
    {
        // THAW PC stores pass addressing as packed 16-bit fields where 0 is
        // wrap and 1 is clamp. Normalize to the D3D-style values used by the
        // THUG2 reader: 1 = wrap, 3 = clamp.
        return mode == 0 ? 1u : 3u;
    }

    private static bool CanRead(BinaryReader r, long offset, long byteCount)
    {
        var length = r.BaseStream.Length;
        return offset >= 0 && byteCount >= 0 && offset <= length && byteCount <= length - offset;
    }

    private static ushort[] TriangulateStrips(ushort[] rawIndices, bool billboard)
    {
        var triangles = new List<ushort>();
        var stripStart = 0;

        for (var i = 0; i <= rawIndices.Length; i++)
        {
            if (i == rawIndices.Length || rawIndices[i] == 0x7FFF)
            {
                TriangulateOneStrip(rawIndices, stripStart, i, triangles, billboard);
                stripStart = i + 1;
            }
        }

        return triangles.ToArray();
    }

    private static void TriangulateOneStrip(
        ushort[] indices, int start, int end, List<ushort> output, bool billboard)
    {
        var len = end - start;
        for (var f = 2; f < len; f++)
        {
            ushort i0;
            ushort i1;
            ushort i2;
            if (f % 2 == 0)
            {
                i0 = indices[start + f - 2];
                i1 = indices[start + f - 1];
                i2 = indices[start + f];
            }
            else
            {
                i0 = indices[start + f - 2];
                i1 = indices[start + f];
                i2 = indices[start + f - 1];
            }

            if (i0 == i1 || i1 == i2 || i0 == i2)
                continue;

            if (billboard)
            {
                output.Add(i2);
                output.Add(i1);
                output.Add(i0);
            }
            else
            {
                output.Add(i0);
                output.Add(i1);
                output.Add(i2);
            }
        }
    }

    private static XbxVertex[] ReadVertexBuffer(
        BinaryReader r, int sectorFlags, int vertexCount, int vertexStride, int passCount)
    {
        var vertices = new XbxVertex[vertexCount];
        var isSkinned = (sectorFlags & ThawSceneFile.SecflagHasWeights) != 0;
        var hasNormals = (sectorFlags & ThawSceneFile.SecflagHasNormals) != 0;
        var hasColors = (sectorFlags & ThawSceneFile.SecflagHasColors) != 0;
        var isBillboard = (sectorFlags & ThawSceneFile.SecflagBillboard) != 0;

        for (var i = 0; i < vertexCount; i++)
        {
            var vertStart = r.BaseStream.Position;
            var vertex = new XbxVertex();

            if (isSkinned)
            {
                vertex.Position = ReadVec3(r);
                XbxSkinVertexCodec.ReadSkinningData(r, ref vertex);

                if (hasNormals)
                {
                    vertex.Normal = UnpackNormal(r.ReadUInt32());
                    vertex.HasNormal = true;
                }
            }
            else
            {
                var position = ReadVec3(r);
                var normal = Vector3.Zero;

                if (hasNormals)
                {
                    normal = ReadVec3(r);
                    vertex.HasNormal = true;
                }

                if (isBillboard)
                {
                    position += normal;
                    normal = Vector3.UnitZ;
                    vertex.HasNormal = true;
                }

                vertex.Position = position;
                vertex.Normal = normal;
            }

            if (hasColors)
            {
                var blue = r.ReadByte();
                var green = r.ReadByte();
                var red = r.ReadByte();
                var alpha = r.ReadByte();
                vertex.Color = new Vector4(red / 128f, green / 128f, blue / 128f, alpha / 128f);
                vertex.HasColor = true;
            }

            if (passCount > 0)
            {
                var u = r.ReadSingle();
                var v = r.ReadSingle();
                vertex.TexCoord = new Vector2(u, 1.0f - v);
            }

            vertices[i] = vertex;
            r.BaseStream.Position = vertStart + vertexStride;
        }

        return vertices;
    }

    private static Vector3 UnpackNormal(uint packed)
    {
        var ix = (int)(packed & 0x7FF);
        if ((packed & 0x400) != 0)
            ix -= 0x800;

        var iy = (int)((packed >> 11) & 0x7FF);
        if ((packed & (0x400 << 11)) != 0)
            iy -= 0x800;

        var iz = (int)((packed >> 22) & 0x3FF);
        if ((packed & (0x200 << 22)) != 0)
            iz -= 0x400;

        var nx = ix / 1023f;
        var ny = iy / 1023f;
        var nz = iz / 511f;
        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 0)
        {
            nx /= len;
            ny /= len;
            nz /= len;
        }

        return new Vector3(nx, ny, nz);
    }

    private static int GetPassCount(XbxMaterial[] materials, uint checksum)
    {
        foreach (var material in materials)
            if (material.Checksum == checksum)
                return material.NumPasses;

        return 1;
    }

    private static Vector3 ReadVec3(BinaryReader r)
    {
        return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    private static uint[] ReadUInt32Array(BinaryReader r, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
            values[i] = r.ReadUInt32();

        return values;
    }
}
