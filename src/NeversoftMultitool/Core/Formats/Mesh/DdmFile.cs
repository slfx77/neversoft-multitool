using System.Text;

namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
/// Parsed DDM (Xbox 3D mesh) file containing static level geometry.
/// </summary>
public sealed class DdmFile
{
    public required List<DdmObject> Objects { get; init; }

    /// <summary>
    /// Parses a DDM file from disk.
    /// </summary>
    public static DdmFile Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        // File header: version (4) + dataSize (4) + objectCount (4)
        var version = reader.ReadUInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported DDM version: {version}");

        reader.ReadUInt32(); // dataSize — not needed
        var objectCount = reader.ReadUInt32();

        // Object table: offset (4) + size (4) per entry
        var objectTable = new (uint Offset, uint Size)[objectCount];
        for (var i = 0; i < objectCount; i++)
        {
            objectTable[i] = (reader.ReadUInt32(), reader.ReadUInt32());
        }

        var objects = new List<DdmObject>((int)objectCount);

        for (var i = 0; i < objectCount; i++)
        {
            stream.Seek(objectTable[i].Offset, SeekOrigin.Begin);
            objects.Add(ReadObject(reader));
        }

        return new DdmFile { Objects = objects };
    }

    private static DdmObject ReadObject(BinaryReader reader)
    {
        // Object header (136 bytes)
        reader.ReadUInt32(); // index
        var checksum = reader.ReadUInt32();
        var animSpeedX = reader.ReadSingle();
        var animSpeedY = reader.ReadSingle();
        reader.ReadSingle(); // animRate
        reader.ReadUInt32(); // animParams
        var flags = reader.ReadUInt32();

        var nameBytes = reader.ReadBytes(64);
        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

        // Bounding box center (3 floats) + extents (3 floats) + sphere radius (1 float)
        var bboxCenterX = reader.ReadSingle();
        var bboxCenterY = reader.ReadSingle();
        var bboxCenterZ = reader.ReadSingle();
        var bboxExtentX = reader.ReadSingle();
        var bboxExtentY = reader.ReadSingle();
        var bboxExtentZ = reader.ReadSingle();
        reader.ReadSingle(); // sphere radius

        var materialCount = reader.ReadUInt32();
        var vertexCount = reader.ReadUInt32();
        var indexCount = reader.ReadUInt32();
        var splitCount = reader.ReadUInt32();

        // Materials (152 bytes each)
        var materials = new List<DdmMaterial>((int)materialCount);
        for (var i = 0; i < materialCount; i++)
        {
            materials.Add(ReadMaterial(reader));
        }

        // Vertices (36 bytes each)
        var vertices = new List<DdmVertex>((int)vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var x = reader.ReadSingle();
            var y = reader.ReadSingle();
            var z = reader.ReadSingle();
            var nx = reader.ReadSingle();
            var ny = reader.ReadSingle();
            var nz = reader.ReadSingle();
            var r = reader.ReadByte();
            var g = reader.ReadByte();
            var b = reader.ReadByte();
            var a = reader.ReadByte();
            var u = reader.ReadSingle();
            var v = reader.ReadSingle();

            vertices.Add(new DdmVertex(x, y, z, nx, ny, nz, r, g, b, a, u, v));
        }

        // Indices (2 bytes each)
        var indices = new ushort[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = reader.ReadUInt16();
        }

        // Material splits (6 bytes each)
        var splits = new List<DdmSplit>((int)splitCount);
        for (var i = 0; i < splitCount; i++)
        {
            var matIdx = reader.ReadUInt16();
            var idxOffset = reader.ReadUInt16();
            var idxCount = reader.ReadUInt16();
            splits.Add(new DdmSplit(matIdx, idxOffset, idxCount));
        }

        return new DdmObject
        {
            Name = name,
            Checksum = checksum,
            Flags = flags,
            AnimSpeedX = animSpeedX,
            AnimSpeedY = animSpeedY,
            BBoxCenterX = bboxCenterX,
            BBoxCenterY = bboxCenterY,
            BBoxCenterZ = bboxCenterZ,
            BBoxExtentX = bboxExtentX,
            BBoxExtentY = bboxExtentY,
            BBoxExtentZ = bboxExtentZ,
            Materials = materials,
            Vertices = vertices,
            Indices = indices,
            Splits = splits
        };
    }

    private static DdmMaterial ReadMaterial(BinaryReader reader)
    {
        var materialNameBytes = reader.ReadBytes(64);
        var materialName = Encoding.ASCII.GetString(materialNameBytes).TrimEnd('\0');

        var textureNameBytes = reader.ReadBytes(64);
        var textureName = Encoding.ASCII.GetString(textureNameBytes).TrimEnd('\0');

        var drawOrder = reader.ReadUInt32();
        var diffuseR = reader.ReadByte();
        var diffuseG = reader.ReadByte();
        var diffuseB = reader.ReadByte();
        var diffuseA = reader.ReadByte();
        var emissive = reader.ReadSingle();
        var specularLevel = reader.ReadSingle();
        var glossiness = reader.ReadSingle();
        var blendMode = reader.ReadUInt32();

        return new DdmMaterial
        {
            Name = materialName,
            TextureName = textureName,
            DrawOrder = drawOrder,
            DiffuseR = diffuseR,
            DiffuseG = diffuseG,
            DiffuseB = diffuseB,
            DiffuseA = diffuseA,
            Emissive = emissive,
            SpecularLevel = specularLevel,
            Glossiness = glossiness,
            BlendMode = blendMode
        };
    }
}

/// <summary>
/// A single mesh object within a DDM file.
/// </summary>
public sealed class DdmObject
{
    public required string Name { get; init; }
    public uint Checksum { get; init; }
    public uint Flags { get; init; }
    public float AnimSpeedX { get; init; }
    public float AnimSpeedY { get; init; }
    public float BBoxCenterX { get; init; }
    public float BBoxCenterY { get; init; }
    public float BBoxCenterZ { get; init; }
    public float BBoxExtentX { get; init; }
    public float BBoxExtentY { get; init; }
    public float BBoxExtentZ { get; init; }
    public required List<DdmMaterial> Materials { get; init; }
    public required List<DdmVertex> Vertices { get; init; }
    public required ushort[] Indices { get; init; }
    public required List<DdmSplit> Splits { get; init; }
}

/// <summary>
/// Vertex with position, normal, vertex color, and texture coordinates.
/// </summary>
public readonly record struct DdmVertex(
    float X, float Y, float Z,
    float NX, float NY, float NZ,
    byte R, byte G, byte B, byte A,
    float U, float V);

/// <summary>
/// Material with texture reference and rendering properties.
/// </summary>
public sealed class DdmMaterial
{
    public required string Name { get; init; }
    public required string TextureName { get; init; }
    public uint DrawOrder { get; init; }
    public byte DiffuseR { get; init; }
    public byte DiffuseG { get; init; }
    public byte DiffuseB { get; init; }
    public byte DiffuseA { get; init; }
    public float Emissive { get; init; }
    public float SpecularLevel { get; init; }
    public float Glossiness { get; init; }
    public uint BlendMode { get; init; }
}

/// <summary>
/// Maps a range of triangle strip indices to a material.
/// </summary>
public readonly record struct DdmSplit(
    ushort MaterialIndex, ushort IndexOffset, ushort IndexCount);
