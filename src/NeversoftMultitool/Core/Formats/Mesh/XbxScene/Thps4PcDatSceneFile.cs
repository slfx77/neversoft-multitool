using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Strict parser for the delimiter-free scene names used by Aspyr's THPS4
///     PC port: <c>*skin.dat</c>, <c>*mdl.dat</c>, and <c>*scn.dat</c>.
/// </summary>
/// <remarks>
///     These files begin with the same (1,1,1) version triple as later Xbox/PC
///     scenes, but their records are an earlier, incompatible layout. Vertices
///     are planar arrays shared by every mesh in a sector, and the final
///     hierarchy is an array of 80-byte <c>CHierarchyObject</c> records. Since
///     <c>.dat</c> is generic, routing is allowed only after this parser consumes
///     the complete payload and validates all cross-references.
/// </remarks>
public static class Thps4PcDatSceneFile
{
    public const string SkinSuffix = "skin.dat";
    public const string ModelSuffix = "mdl.dat";
    public const string SceneSuffix = "scn.dat";

    private const uint Version = 1;
    private const int MaxPasses = 4;
    private const uint SectorHasTexCoords = 1u << 0;
    private const uint SectorHasColors = 1u << 1;
    private const uint SectorHasNormals = 1u << 2;
    private const uint SectorHasWeights = 1u << 4;
    private const uint SectorHasColorWibbles = 1u << 11;
    private const uint SectorHasBillboard = 0x00800000;

    /// <summary>
    ///     True only for the three delimiter-free THPS4 PC spellings. A bare
    ///     asset-kind name has no stem, and a dotted compound name belongs to a
    ///     different format namespace.
    /// </summary>
    public static bool IsCandidateFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return IsDelimiterFreeSuffix(name, SkinSuffix)
               || IsDelimiterFreeSuffix(name, ModelSuffix)
               || IsDelimiterFreeSuffix(name, SceneSuffix);
    }

    public static XbxScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static XbxScene Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reader = new Reader(data);

        var materialVersion = reader.ReadUInt32("material version");
        var meshVersion = reader.ReadUInt32("mesh version");
        var vertexVersion = reader.ReadUInt32("vertex version");
        if (materialVersion != Version || meshVersion != Version || vertexVersion != Version)
        {
            throw new InvalidDataException(
                $"Unexpected THPS4 PC scene version ({materialVersion},{meshVersion},{vertexVersion}); " +
                "expected (1,1,1)");
        }

        var materialCount = reader.ReadNonZeroCount(
            "material count", minimumRecordBytes: 112, reserveBytes: 8);
        var materials = new XbxMaterial[materialCount];
        var materialChecksums = new HashSet<uint>();
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = ReadMaterial(reader, i);
            if (!materialChecksums.Add(materials[i].Checksum))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene material {i} duplicates checksum 0x{materials[i].Checksum:X8}");
            }
        }

        var sectorCount = reader.ReadNonZeroCount(
            "sector count", minimumRecordBytes: 64, reserveBytes: 4);
        var sectors = new XbxSector[sectorCount];
        var sectorChecksums = new HashSet<uint>();
        var sectorsByBone = new Dictionary<int, uint>();
        for (var i = 0; i < sectors.Length; i++)
        {
            var sector = ReadSector(reader, i, materialChecksums);
            sectors[i] = sector;
            if (!sectorChecksums.Add(sector.Checksum))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene sector {i} duplicates checksum 0x{sector.Checksum:X8}");
            }

            if (sector.BoneIndex >= 0 && !sectorsByBone.TryAdd(sector.BoneIndex, sector.Checksum))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene sector {i} duplicates bone index {sector.BoneIndex}");
            }
        }

        var links = ReadHierarchy(reader, sectorsByBone);
        reader.RequireEnd("THPS4 PC scene hierarchy");

        return new XbxScene
        {
            Materials = materials,
            Sectors = sectors,
            Links = links,
            ApplyHierarchyTransforms = links.Length > 0
        };
    }

    public static bool TryParse(byte[] data, out XbxScene? scene, out string? error)
    {
        try
        {
            scene = Parse(data);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            scene = null;
            error = ex.Message;
            return false;
        }
    }

    private static XbxMaterial ReadMaterial(Reader reader, int materialIndex)
    {
        var checksum = reader.ReadUInt32($"material {materialIndex} checksum");
        var passCountRaw = reader.ReadUInt32($"material {materialIndex} pass count");
        if (passCountRaw is 0 or > MaxPasses)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene material {materialIndex} has invalid pass count {passCountRaw}");
        }

        var alphaCutoffRaw = reader.ReadUInt32($"material {materialIndex} alpha cutoff");
        if (alphaCutoffRaw > byte.MaxValue)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene material {materialIndex} has invalid alpha cutoff {alphaCutoffRaw}");
        }

        var sorted = reader.ReadBoolean($"material {materialIndex} sorted flag");
        var drawOrder = reader.ReadFiniteSingle($"material {materialIndex} draw order");
        var singleSided = reader.ReadBoolean($"material {materialIndex} single-sided flag");
        var grassify = reader.ReadBoolean($"material {materialIndex} grass flag");

        var grassHeight = 0f;
        var grassLayers = 0;
        if (grassify)
        {
            grassHeight = reader.ReadFiniteSingle($"material {materialIndex} grass height");
            grassLayers = reader.ReadInt32($"material {materialIndex} grass layer count");
            if (grassLayers <= 0)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene material {materialIndex} has invalid grass layer count {grassLayers}");
            }
        }

        var passCount = (int)passCountRaw;
        var passes = new XbxPass[passCount];
        for (var pass = 0; pass < passes.Length; pass++)
            passes[pass] = ReadPass(reader, materialIndex, pass);

        return new XbxMaterial
        {
            Checksum = checksum,
            // THPS4 serializes one material identity rather than the later
            // checksum/name-checksum pair.
            NameChecksum = checksum,
            NumPasses = passCount,
            AlphaCutoff = (int)alphaCutoffRaw,
            Sorted = sorted,
            DrawOrder = drawOrder,
            SingleSided = singleSided,
            NoBfc = !singleSided,
            ZBias = 0,
            Grassify = grassify,
            GrassHeight = grassHeight,
            GrassLayers = grassLayers,
            Passes = passes
        };
    }

    private static XbxPass ReadPass(Reader reader, int materialIndex, int passIndex)
    {
        var label = $"material {materialIndex} pass {passIndex}";
        var textureChecksum = reader.ReadUInt32($"{label} texture checksum");
        var flags = reader.ReadUInt32($"{label} flags");
        var hasColor = reader.ReadBoolean($"{label} color flag");
        var color = reader.ReadVector3($"{label} color");
        var blendMode = reader.ReadUInt32($"{label} blend mode");
        var fixedAlpha = reader.ReadUInt32($"{label} fixed alpha");
        var uAddressing = reader.ReadUInt32($"{label} U addressing");
        var vAddressing = reader.ReadUInt32($"{label} V addressing");
        var filteringMode = reader.ReadUInt32($"{label} filtering mode");

        // THPS4 carries ambient, diffuse and specular RGB triples per pass.
        // The portable scene material does not expose those lighting terms,
        // but every component is still structurally validated.
        for (var component = 0; component < 9; component++)
            reader.ReadFiniteSingle($"{label} lighting component {component}");

        if ((flags & XbxMaterialFlags.UvWibble) != 0)
        {
            for (var component = 0; component < 8; component++)
                reader.ReadFiniteSingle($"{label} UV-wibble component {component}");
        }

        if (passIndex == 0 && (flags & XbxMaterialFlags.VcWibble) != 0)
        {
            var sequenceCount = reader.ReadCount(
                $"{label} VC-wibble sequence count", minimumRecordBytes: 8);
            for (var sequence = 0; sequence < sequenceCount; sequence++)
            {
                var keyCount = reader.ReadCount(
                    $"{label} VC-wibble sequence {sequence} key count",
                    minimumRecordBytes: 8,
                    reserveBytes: 4);
                reader.ReadInt32($"{label} VC-wibble sequence {sequence} phase");
                reader.Skip((long)keyCount * 8, $"{label} VC-wibble sequence {sequence} keys");
            }
        }

        if ((flags & XbxMaterialFlags.PassTextureAnimates) != 0)
        {
            var keyCount = reader.ReadCount(
                $"{label} animated-texture key count", minimumRecordBytes: 8, reserveBytes: 12);
            reader.ReadInt32($"{label} animated-texture period");
            reader.ReadInt32($"{label} animated-texture iterations");
            reader.ReadInt32($"{label} animated-texture phase");
            reader.Skip((long)keyCount * 8, $"{label} animated-texture keys");
        }

        reader.ReadUInt32($"{label} mip MMAG");
        reader.ReadUInt32($"{label} mip MMIN");
        reader.ReadFiniteSingle($"{label} mip K");
        reader.ReadFiniteSingle($"{label} mip L");

        return new XbxPass
        {
            TextureChecksum = textureChecksum,
            Flags = flags,
            HasColor = hasColor,
            Color = color,
            BlendMode = blendMode,
            FixedAlpha = fixedAlpha,
            UAddressing = uAddressing,
            VAddressing = vAddressing,
            FilteringMode = filteringMode
        };
    }

    private static XbxSector ReadSector(
        Reader reader,
        int sectorIndex,
        HashSet<uint> materialChecksums)
    {
        var label = $"sector {sectorIndex}";
        var checksum = reader.ReadUInt32($"{label} checksum");
        var boneIndex = reader.ReadInt32($"{label} bone index");
        if (boneIndex < -1 || boneIndex > sbyte.MaxValue)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene {label} has invalid hierarchy bone index {boneIndex}");
        }

        var flags = reader.ReadUInt32($"{label} flags");
        var meshCount = reader.ReadNonZeroCount(
            $"{label} mesh count", minimumRecordBytes: 12, reserveBytes: 48);
        var bboxMin = reader.ReadVector3($"{label} bounding-box minimum");
        var bboxMax = reader.ReadVector3($"{label} bounding-box maximum");
        var sphereCenter = reader.ReadVector3($"{label} bounding-sphere center");
        var sphereRadius = reader.ReadFiniteSingle($"{label} bounding-sphere radius");
        if (sphereRadius < 0f)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene {label} has negative bounding-sphere radius {sphereRadius}");
        }

        if ((flags & SectorHasBillboard) != 0)
        {
            var billboardType = reader.ReadUInt32($"{label} billboard type");
            if (billboardType is not (1 or 2))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene {label} has unknown billboard type {billboardType}");
            }

            reader.ReadVector3($"{label} billboard origin");
            reader.ReadVector3($"{label} billboard pivot");
            reader.ReadVector3($"{label} billboard axis");
        }

        var vertexCount = reader.ReadNonZeroCount(
            $"{label} vertex count", minimumRecordBytes: 12, reserveBytes: (long)meshCount * 12 + 4);
        var serializedStride = reader.ReadUInt32($"{label} vertex stride");
        if (serializedStride == 0 || serializedStride > 1024 || (serializedStride & 3) != 0)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene {label} has invalid vertex stride {serializedStride}");
        }

        var vertices = new XbxVertex[vertexCount];
        for (var vertex = 0; vertex < vertices.Length; vertex++)
            vertices[vertex].Position = reader.ReadVector3($"{label} vertex {vertex} position");

        if ((flags & SectorHasNormals) != 0)
        {
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                vertices[vertex].Normal = reader.ReadVector3($"{label} vertex {vertex} normal");
                vertices[vertex].HasNormal = true;
            }
        }

        if ((flags & SectorHasWeights) != 0)
        {
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                var w0 = reader.ReadFiniteSingle($"{label} vertex {vertex} weight 0");
                var w1 = reader.ReadFiniteSingle($"{label} vertex {vertex} weight 1");
                var w2 = reader.ReadFiniteSingle($"{label} vertex {vertex} weight 2");
                var w3 = reader.ReadFiniteSingle($"{label} vertex {vertex} weight 3");
                if (w0 < 0f || w1 < 0f || w2 < 0f || w3 < 0f)
                {
                    throw new InvalidDataException(
                        $"THPS4 PC scene {label} vertex {vertex} has a negative skin weight");
                }

                vertices[vertex].BoneWeight0 = w0;
                vertices[vertex].BoneWeight1 = w1;
                vertices[vertex].BoneWeight2 = w2;
                vertices[vertex].BoneWeight3 = w3;
                vertices[vertex].HasSkinData = w0 + w1 + w2 + w3 > 0f;
            }

            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                vertices[vertex].BoneIndex0 = reader.ReadUInt16($"{label} vertex {vertex} bone 0");
                vertices[vertex].BoneIndex1 = reader.ReadUInt16($"{label} vertex {vertex} bone 1");
                vertices[vertex].BoneIndex2 = reader.ReadUInt16($"{label} vertex {vertex} bone 2");
                vertices[vertex].BoneIndex3 = reader.ReadUInt16($"{label} vertex {vertex} bone 3");
            }
        }

        var uvSetCount = 0;
        if ((flags & SectorHasTexCoords) != 0)
        {
            uvSetCount = reader.ReadNonZeroCount(
                $"{label} UV-set count", minimumRecordBytes: (long)vertexCount * 8);
            if (uvSetCount > MaxPasses)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene {label} has unsupported UV-set count {uvSetCount}");
            }

            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                for (var uvSet = 0; uvSet < uvSetCount; uvSet++)
                {
                    // Shipped THPS4 PC data leaves some UV slots as NaN (even
                    // in set zero). The original importer accepts those raw
                    // slots; normalize them to zero so the portable IR/glTF
                    // remains finite while retaining exact stream framing.
                    var uv = reader.ReadSanitizedVector2($"{label} vertex {vertex} UV set {uvSet}");
                    if (uvSet == 0)
                        vertices[vertex].TexCoord = uv;
                }
            }
        }

        if ((flags & SectorHasColors) != 0)
        {
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                var blue = reader.ReadByte($"{label} vertex {vertex} blue");
                var green = reader.ReadByte($"{label} vertex {vertex} green");
                var red = reader.ReadByte($"{label} vertex {vertex} red");
                var alpha = reader.ReadByte($"{label} vertex {vertex} alpha");
                vertices[vertex].Color = new Vector4(
                    red / 255f,
                    green / 255f,
                    blue / 255f,
                    alpha / 128f);
                vertices[vertex].HasColor = true;
            }
        }

        if ((flags & SectorHasColorWibbles) != 0)
            reader.Skip(vertexCount, $"{label} vertex-color wibble indices");

        var meshes = new XbxMesh[meshCount];
        for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
        {
            var meshFlags = reader.ReadUInt32($"{label} mesh {meshIndex} flags");
            var materialChecksum = reader.ReadUInt32($"{label} mesh {meshIndex} material checksum");
            if (!materialChecksums.Contains(materialChecksum))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene {label} mesh {meshIndex} references missing material " +
                    $"0x{materialChecksum:X8}");
            }

            var indexCount = reader.ReadNonZeroCount(
                $"{label} mesh {meshIndex} index count", minimumRecordBytes: 2);
            var sourceIndices = new ushort[indexCount];
            for (var index = 0; index < sourceIndices.Length; index++)
            {
                sourceIndices[index] = reader.ReadUInt16($"{label} mesh {meshIndex} index {index}");
                if (sourceIndices[index] >= vertices.Length)
                {
                    throw new InvalidDataException(
                        $"THPS4 PC scene {label} mesh {meshIndex} references vertex " +
                        $"{sourceIndices[index]}, but only {vertices.Length} vertices exist");
                }
            }

            var (meshVertices, meshIndices) = CompactVertexPool(vertices, sourceIndices);
            meshes[meshIndex] = new XbxMesh
            {
                BsphereCenter = sphereCenter,
                BsphereRadius = sphereRadius,
                BboxMin = bboxMin,
                BboxMax = bboxMax,
                MeshFlags = meshFlags,
                MaterialChecksum = materialChecksum,
                Vertices = meshVertices,
                FaceIndices = meshIndices,
                IsPreTriangulated = false
            };
        }

        return new XbxSector
        {
            Checksum = checksum,
            BoneIndex = boneIndex,
            Flags = unchecked((int)flags),
            BboxMin = bboxMin,
            BboxMax = bboxMax,
            BsphereCenter = sphereCenter,
            BsphereRadius = sphereRadius,
            Meshes = meshes,
            SourceVertexCount = vertexCount,
            SourceVertexStride = serializedStride,
            SourceUvSetCount = uvSetCount
        };
    }

    private static XbxLink[] ReadHierarchy(Reader reader, IReadOnlyDictionary<int, uint> sectorsByBone)
    {
        var countRaw = reader.ReadUInt32("hierarchy object count");
        if (countRaw > int.MaxValue)
            throw new InvalidDataException($"THPS4 PC scene hierarchy count {countRaw} is too large");

        var count = (int)countRaw;
        var requiredBytes = (long)count * 80;
        if (reader.Remaining != requiredBytes)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene hierarchy declares {count} objects ({requiredBytes} bytes), " +
                $"but {reader.Remaining} bytes remain");
        }

        var links = new XbxLink[count];
        var checksums = new HashSet<uint>();
        var boneIndices = new HashSet<sbyte>();
        for (var i = 0; i < links.Length; i++)
        {
            var checksum = reader.ReadUInt32($"hierarchy object {i} checksum");
            var parentChecksum = reader.ReadUInt32($"hierarchy object {i} parent checksum");
            var parentIndex = reader.ReadInt16($"hierarchy object {i} parent index");
            var boneIndex = reader.ReadSByte($"hierarchy object {i} bone index");
            var pad8 = reader.ReadByte($"hierarchy object {i} byte padding");
            var pad32 = reader.ReadUInt32($"hierarchy object {i} word padding");
            if (pad8 != 0 || pad32 != 0)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene hierarchy object {i} has nonzero padding");
            }

            if (!checksums.Add(checksum))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene hierarchy object {i} duplicates checksum 0x{checksum:X8}");
            }

            if (boneIndex < 0 || !boneIndices.Add(boneIndex))
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene hierarchy object {i} has invalid or duplicate bone index {boneIndex}");
            }

            if (!sectorsByBone.TryGetValue(boneIndex, out var sectorChecksum) || sectorChecksum != checksum)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene hierarchy object {i} does not match sector bone {boneIndex}");
            }

            if (parentIndex == -1)
            {
                if (parentChecksum != 0)
                {
                    throw new InvalidDataException(
                        $"THPS4 PC scene hierarchy root {i} has parent checksum 0x{parentChecksum:X8}");
                }
            }
            else if (parentIndex < 0 || parentIndex >= i || links[parentIndex].SectorChecksum != parentChecksum)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene hierarchy object {i} has inconsistent parent " +
                    $"index/checksum ({parentIndex},0x{parentChecksum:X8})");
            }

            var transform = reader.ReadMatrix4x4($"hierarchy object {i} setup matrix");
            links[i] = new XbxLink
            {
                SectorChecksum = checksum,
                ParentChecksum = parentChecksum,
                ParentIndex = parentIndex,
                BoneIndex = boneIndex,
                Index = (ushort)boneIndex,
                Transform = transform
            };
        }

        if (links.Length != 0 && sectorsByBone.Count != links.Length)
        {
            throw new InvalidDataException(
                $"THPS4 PC scene has {sectorsByBone.Count} hierarchy-bound sectors but {links.Length} objects");
        }

        return links;
    }

    private static (XbxVertex[] Vertices, ushort[] Indices) CompactVertexPool(
        XbxVertex[] sourceVertices,
        ushort[] sourceIndices)
    {
        var remap = new Dictionary<ushort, ushort>();
        var vertices = new List<XbxVertex>(Math.Min(sourceIndices.Length, sourceVertices.Length));
        var indices = new ushort[sourceIndices.Length];
        for (var i = 0; i < sourceIndices.Length; i++)
        {
            var sourceIndex = sourceIndices[i];
            if (!remap.TryGetValue(sourceIndex, out var compactIndex))
            {
                compactIndex = checked((ushort)vertices.Count);
                remap.Add(sourceIndex, compactIndex);
                vertices.Add(sourceVertices[sourceIndex]);
            }

            indices[i] = compactIndex;
        }

        return (vertices.ToArray(), indices);
    }

    private static bool IsDelimiterFreeSuffix(string name, string suffix)
    {
        return name.Length > suffix.Length
               && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && name[name.Length - suffix.Length - 1] != '.';
    }

    private sealed class Reader(byte[] data)
    {
        public int Offset { get; private set; }
        public long Remaining => data.LongLength - Offset;

        public byte ReadByte(string field)
        {
            Require(1, field);
            return data[Offset++];
        }

        public sbyte ReadSByte(string field)
        {
            return unchecked((sbyte)ReadByte(field));
        }

        public bool ReadBoolean(string field)
        {
            var value = ReadByte(field);
            if (value > 1)
                throw new InvalidDataException($"THPS4 PC scene {field} is {value}, expected 0 or 1");
            return value != 0;
        }

        public short ReadInt16(string field)
        {
            return unchecked((short)ReadUInt16(field));
        }

        public ushort ReadUInt16(string field)
        {
            Require(2, field);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Offset, 2));
            Offset += 2;
            return value;
        }

        public int ReadInt32(string field)
        {
            return unchecked((int)ReadUInt32(field));
        }

        public uint ReadUInt32(string field)
        {
            Require(4, field);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Offset, 4));
            Offset += 4;
            return value;
        }

        public float ReadFiniteSingle(string field)
        {
            var value = ReadSingle(field);
            if (!float.IsFinite(value))
                throw new InvalidDataException($"THPS4 PC scene {field} is not finite");
            return value;
        }

        public float ReadSingle(string field)
        {
            return BitConverter.Int32BitsToSingle(ReadInt32(field));
        }

        public Vector2 ReadSanitizedVector2(string field)
        {
            var x = ReadSingle($"{field} X");
            var y = ReadSingle($"{field} Y");
            return new Vector2(
                float.IsFinite(x) ? x : 0f,
                float.IsFinite(y) ? y : 0f);
        }

        public Vector3 ReadVector3(string field)
        {
            return new Vector3(
                ReadFiniteSingle($"{field} X"),
                ReadFiniteSingle($"{field} Y"),
                ReadFiniteSingle($"{field} Z"));
        }

        public Matrix4x4 ReadMatrix4x4(string field)
        {
            return new Matrix4x4(
                ReadFiniteSingle($"{field} M11"), ReadFiniteSingle($"{field} M12"),
                ReadFiniteSingle($"{field} M13"), ReadFiniteSingle($"{field} M14"),
                ReadFiniteSingle($"{field} M21"), ReadFiniteSingle($"{field} M22"),
                ReadFiniteSingle($"{field} M23"), ReadFiniteSingle($"{field} M24"),
                ReadFiniteSingle($"{field} M31"), ReadFiniteSingle($"{field} M32"),
                ReadFiniteSingle($"{field} M33"), ReadFiniteSingle($"{field} M34"),
                ReadFiniteSingle($"{field} M41"), ReadFiniteSingle($"{field} M42"),
                ReadFiniteSingle($"{field} M43"), ReadFiniteSingle($"{field} M44"));
        }

        public int ReadCount(
            string field,
            long minimumRecordBytes,
            long reserveBytes = 0)
        {
            var raw = ReadUInt32(field);
            return ValidateCount(raw, field, minimumRecordBytes, reserveBytes, requireNonZero: false);
        }

        public int ReadNonZeroCount(
            string field,
            long minimumRecordBytes,
            long reserveBytes = 0)
        {
            var raw = ReadUInt32(field);
            return ValidateCount(raw, field, minimumRecordBytes, reserveBytes, requireNonZero: true);
        }

        public void Skip(long count, string field)
        {
            Require(count, field);
            Offset = checked(Offset + (int)count);
        }

        public void RequireEnd(string field)
        {
            if (Remaining != 0)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene has {Remaining} trailing bytes after {field}");
            }
        }

        private int ValidateCount(
            uint raw,
            string field,
            long minimumRecordBytes,
            long reserveBytes,
            bool requireNonZero)
        {
            if (requireNonZero && raw == 0)
                throw new InvalidDataException($"THPS4 PC scene {field} must be nonzero");
            if (raw > int.MaxValue)
                throw new InvalidDataException($"THPS4 PC scene {field} {raw} is too large");
            if (minimumRecordBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumRecordBytes));

            var available = Remaining - reserveBytes;
            if (available < 0 || (long)raw * minimumRecordBytes > available)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene {field} {raw} exceeds the remaining payload");
            }

            return (int)raw;
        }

        private void Require(long count, string field)
        {
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException(
                    $"THPS4 PC scene {field} overruns the payload at 0x{Offset:X} " +
                    $"({count} bytes requested, {Remaining} remain)");
            }
        }
    }
}
