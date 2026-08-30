using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Xbox 360 / PS3 Neversoft scene files (<c>.skin.xen</c>, <c>.mdl.xen</c>,
///     <c>.scn.xen</c> and their <c>.ps3</c> twins), derived 2026-08-28.
///     <para>
///         The container is the shipped THAW <see cref="ThawSceneFile" /> layout read
///         BIG-ENDIAN, with the 28 reserved header bytes filled by a repeating
///         <c>FAAABACA</c> sentinel instead of zero; 0xBABEFACE plus its pad still
///         locate the CScene exactly where the little-endian reader expects. Every
///         struct inside it, however, has its own next-gen size — CScene 160,
///         CSector 64, CGeom 112, sMesh 128 — so "same family" buys the walk, not
///         the offsets.
///     </para>
///     <para>
///         Vertex and index data hang off a per-mesh pointer that lands on a 20-byte
///         GPU descriptor, with the buffer beginning at descriptor+20. The blob in
///         front of the vertex buffer is the exact byte-REVERSE of the one in front
///         of the index buffer, which marks it as runtime state rather than authored
///         layout — so the stride is taken from <c>vertexByteSize / vertexCount</c>,
///         which is exact on every mesh measured, and never read out of the
///         descriptor.
///     </para>
///     <para>
///         Proven against the GameCube twin of the same asset, whose GX display-list
///         reader shares no code with this one: <c>baseball_bat</c> decodes to 107
///         vertices, 152 triangles, an identical bounding box, and 107/107
///         byte-identical positions. Corpus: 3,960/3,960 THAW Xbox 360 scene files
///         and 95,571 meshes parse, and across the non-skinned .mdl/.scn families
///         no index falls outside its mesh and no position falls outside the file's
///         own declared bounding box.
///     </para>
///     <para>
///         Project 8 and Proving Ground share the container but are a later revision,
///         read by <see cref="NextGenLaterRevision" />. Their CScene field offsets are
///         NOT stable across file kinds, so the mesh table is LOCATED by anchoring on
///         the buffers' own 16-byte <c>CAFEBAB4</c> magic instead of being trusted from
///         CScene, and their geometry is split differently: positions live in a chain
///         of batches and the index buffer sits at the END of a second block behind a
///         big-endian <c>FACEF001 FACEF000</c> header.
///     </para>
/// </summary>
public static class NextGenSceneFile
{
    private const uint Sentinel = 0xFAAABACA;
    private const uint BabefaceMagic = 0xBABEFACE;

    /// <summary>Size of the GPU descriptor that precedes each buffer.</summary>
    private const int BufferDescriptorSize = 20;

    private const int SMeshRecordSize = 128;
    private const int SectorRecordSize = 64;
    private const int GeomRecordSize = 112;
    private const int StripRestart = 0x7FFF;

    public static readonly string[] SupportedExtensions =
        [".skin.xen", ".mdl.xen", ".scn.xen", ".skin.ps3", ".mdl.ps3", ".scn.ps3"];

    /// <summary>
    ///     The repeating sentinel in the reserved header words, plus a big-endian
    ///     read of the header it introduces.
    ///     <para>
    ///         <b>The sentinel alone is NOT a next-gen marker.</b> It looked like one
    ///         — seven consecutive copies of a memorable constant — but it is just
    ///         what Neversoft's exporter writes into those reserved bytes, and all
    ///         723 THAW <b>PC</b> scene files carry it too. Detecting on the sentinel
    ///         alone claimed every one of them, because this check sits ahead of the
    ///         little-endian THAW reader in the routing ladder. What actually
    ///         separates the platforms is byte order, so the test is that the header
    ///         resolves when read BIG-endian: a little-endian file's material-list
    ///         size comes back byte-swapped and absurd, and its 0xBABEFACE sentinel
    ///         is not where the big-endian walk expects it.
    ///     </para>
    /// </summary>
    public static bool IsNextGenScene(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40)
            return false;

        for (var o = 4; o < 32; o += 4)
            if (BinaryPrimitives.ReadUInt32BigEndian(data[o..]) != Sentinel)
                return false;

        return TryFindSceneOffset(data, out _);
    }

    /// <summary>
    ///     Walks past the material list to the CScene, big-endian. Returns false
    ///     rather than throwing so it can double as the detection predicate.
    /// </summary>
    private static bool TryFindSceneOffset(ReadOnlySpan<byte> data, out int scene)
    {
        scene = 0;
        if (data.Length < 0x28)
            return false;

        var materialListSize = BinaryPrimitives.ReadInt32BigEndian(data[0x24..]);
        if (materialListSize <= 0 || materialListSize > data.Length)
            return false;

        var afterMaterials = 0x20 + materialListSize;
        if (afterMaterials < 0 || afterMaterials + 8 > data.Length)
            return false;

        if (BinaryPrimitives.ReadUInt32BigEndian(data[afterMaterials..]) != BabefaceMagic)
            return false;

        var pad = BinaryPrimitives.ReadInt32BigEndian(data[(afterMaterials + 4)..]);
        if (pad < 0 || pad > data.Length)
            return false;

        var candidate = afterMaterials + 8 + pad;
        if (candidate < 0 || candidate >= data.Length)
            return false;

        scene = candidate;
        return true;
    }

    /// <summary>
    ///     The sibling file a PlayStation 3 scene keeps its attribute and index
    ///     blocks in, or null when this is not a PS3 scene. The kind is swapped
    ///     rather than suffixed: <c>.skin.ps3</c> pairs with <c>.skiv.ps3</c>,
    ///     <c>.mdl.ps3</c> with <c>.mdv.ps3</c>, <c>.scn.ps3</c> with
    ///     <c>.scv.ps3</c>.
    /// </summary>
    public static string? GetVramCompanionName(string fileName)
    {
        foreach (var (scene, vram) in Ps3VramKinds)
            if (fileName.EndsWith(scene, StringComparison.OrdinalIgnoreCase))
                return string.Concat(fileName.AsSpan(0, fileName.Length - scene.Length), vram);

        return null;
    }

    private static readonly (string Scene, string Vram)[] Ps3VramKinds =
    [
        (".skin.ps3", ".skiv.ps3"), (".mdl.ps3", ".mdv.ps3"), (".scn.ps3", ".scv.ps3")
    ];

    public static XbxScene Parse(byte[] data, byte[]? vram = null)
    {
        if (!IsNextGenScene(data))
            throw new InvalidDataException("Not a next-gen Neversoft scene (missing FAAABACA sentinel)");

        var scene = FindSceneOffset(data);

        // Project 8 and Proving Ground share this container but moved the CScene
        // tables, so they are read by their own path rather than at THAW's offsets.
        if (!SceneLayout.TryResolve(data, scene, out var layout))
            return NextGenLaterRevision.Parse(data, scene, vram);

        var sectors = new XbxSector[layout.SectorCount];
        var materials = new Dictionary<uint, XbxMaterial>();
        var meshIndex = 0;

        for (var s = 0; s < layout.SectorCount; s++)
        {
            var sectorOffset = layout.SectorTable + SectorRecordSize * s;
            var geomOffset = layout.GeomTable + GeomRecordSize * s;
            RequireRange(data.Length, sectorOffset, SectorRecordSize, "next-gen CSector");
            RequireRange(data.Length, geomOffset, GeomRecordSize, "next-gen CGeom");

            var meshCount = ReadInt32(data, geomOffset + 0x5C);
            if (meshCount < 0 || meshCount > 0xFFFF)
                throw new InvalidDataException($"Next-gen CGeom mesh count {meshCount} is invalid");

            var meshes = new XbxMesh[meshCount];
            for (var m = 0; m < meshCount; m++)
                meshes[m] = ReadMesh(data, scene, layout.MeshTable + SMeshRecordSize * (meshIndex + m), materials);

            meshIndex += meshCount;

            sectors[s] = new XbxSector
            {
                Checksum = ReadUInt32(data, sectorOffset + 4),
                BoneIndex = -1,
                Flags = ReadInt32(data, sectorOffset + 0x1C),
                BboxMin = ReadVec3(data, geomOffset + 0x20),
                BboxMax = ReadVec3(data, geomOffset + 0x30),
                Meshes = meshes
            };
        }

        return new XbxScene
        {
            Materials = materials.Values.ToArray(),
            Sectors = sectors,
            Links = []
        };
    }

    /// <summary>Container walk: material list, then the 0xBABEFACE sentinel and its pad.</summary>
    private static int FindSceneOffset(byte[] data)
    {
        var materialListSize = ReadInt32(data, 0x24);
        if (materialListSize <= 0 || materialListSize > data.Length)
            throw new InvalidDataException($"Next-gen material list size {materialListSize} is invalid");

        var afterMaterials = 0x20 + materialListSize;
        RequireRange(data.Length, afterMaterials, 8, "next-gen BABEFACE sentinel");
        if (ReadUInt32(data, afterMaterials) != BabefaceMagic)
            throw new InvalidDataException("Next-gen scene is missing its 0xBABEFACE sentinel");

        var pad = ReadInt32(data, afterMaterials + 4);
        if (pad < 0 || pad > data.Length)
            throw new InvalidDataException($"Next-gen scene pad {pad} is invalid");

        var scene = afterMaterials + 8 + pad;
        if (scene < 0 || scene >= data.Length)
            throw new InvalidDataException("Next-gen CScene lies past the end of the file");

        return scene;
    }

    private static XbxMesh ReadMesh(
        byte[] data, int scene, int record, Dictionary<uint, XbxMaterial> materials)
    {
        RequireRange(data.Length, record, SMeshRecordSize, "next-gen sMesh");

        var materialChecksum = ReadUInt32(data, record + 0x14);
        if (!materials.ContainsKey(materialChecksum))
            materials[materialChecksum] = BuildMaterial(materialChecksum);

        var declaration = VertexDeclaration.Read(data, record + 0x18);
        var indexCount = ReadUInt16(data, record + 0x20);
        var vertexCount = ReadUInt16(data, record + 0x22);
        var vertexBuffer = ReadUInt32(data, record + 0x50);
        var vertexBytes = ReadUInt32(data, record + 0x5C);
        var indexBuffer = ReadUInt32(data, record + 0x70);

        var vertices = ReadVertices(data, scene, vertexBuffer, vertexBytes, vertexCount, declaration);
        var indices = ReadIndices(data, scene, indexBuffer, indexCount);

        return new XbxMesh
        {
            BsphereCenter = ReadVec3(data, record),
            BsphereRadius = ReadSingle(data, record + 0x10),
            MaterialChecksum = materialChecksum,
            Vertices = vertices,
            FaceIndices = indices,
            IsPreTriangulated = true
        };
    }

    /// <summary>
    ///     Materials are emitted as opaque single-pass placeholders keyed by the
    ///     mesh's own checksum. The next-gen material record is not yet derived, so
    ///     no texture binding is claimed rather than guessed.
    /// </summary>
    private static XbxMaterial BuildMaterial(uint checksum)
    {
        return new XbxMaterial
        {
            Checksum = checksum,
            NameChecksum = checksum,
            NumPasses = 1,
            SingleSided = false,
            Passes = [new XbxPass { TextureChecksum = 0, HasColor = false }]
        };
    }

    private static XbxVertex[] ReadVertices(
        byte[] data, int scene, uint pointer, uint byteSize, int count, VertexDeclaration declaration)
    {
        if (count <= 0 || pointer == 0 || pointer == uint.MaxValue || byteSize == 0)
            return [];

        // The descriptor in front of the buffer is runtime state (the vertex copy
        // is the index copy's bytes reversed), so the stride comes from the
        // authored size/count pair, which divides exactly on every mesh measured.
        if (byteSize % (uint)count != 0)
            return [];

        var stride = (int)(byteSize / (uint)count);
        if (stride < 12 || stride > 256)
            return [];

        var start = scene + (int)pointer + BufferDescriptorSize;
        if (start < 0 || byteSize > (uint)(data.Length - start))
            return [];

        var vertices = new XbxVertex[count];
        for (var i = 0; i < count; i++)
        {
            var o = start + stride * i;
            var vertex = new XbxVertex
            {
                Position = ReadVec3(data, o),
                Color = Vector4.One
            };

            if (declaration.Normal >= 0 && declaration.Normal + 4 <= stride)
            {
                vertex.Normal = UnpackNormal(ReadUInt32(data, o + declaration.Normal));
                vertex.HasNormal = true;
            }

            if (declaration.Color >= 0 && declaration.Color + 4 <= stride)
            {
                // BGRA with 128 = 1.0, the Neversoft convention shared with the
                // little-endian THAW reader.
                var b = data[o + declaration.Color];
                var g = data[o + declaration.Color + 1];
                var r = data[o + declaration.Color + 2];
                var a = data[o + declaration.Color + 3];
                vertex.Color = new Vector4(r / 128f, g / 128f, b / 128f, a / 128f);
                vertex.HasColor = true;
            }

            if (declaration.TexCoord >= 0 && declaration.TexCoord + 8 <= stride)
            {
                vertex.TexCoord = new Vector2(
                    ReadSingle(data, o + declaration.TexCoord),
                    1.0f - ReadSingle(data, o + declaration.TexCoord + 4));
            }

            vertices[i] = vertex;
        }

        return vertices;
    }

    private static ushort[] ReadIndices(byte[] data, int scene, uint pointer, int count)
    {
        if (count <= 0 || pointer == 0 || pointer == uint.MaxValue)
            return [];

        var start = scene + (int)pointer + BufferDescriptorSize;
        var byteCount = (long)count * 2;
        if (start < 0 || byteCount > data.Length - start)
            return [];

        var raw = new ushort[count];
        for (var i = 0; i < count; i++)
            raw[i] = ReadUInt16(data, start + 2 * i);

        return Triangulate(raw);
    }

    /// <summary>
    ///     Degenerate triangle strips, the same shape the little-endian THAW reader
    ///     produces: alternate winding per step, drop degenerate joins, and treat
    ///     0x7FFF as an explicit restart.
    /// </summary>
    private static ushort[] Triangulate(ushort[] strip)
    {
        var triangles = new List<ushort>();
        var start = 0;

        for (var i = 0; i <= strip.Length; i++)
        {
            if (i != strip.Length && strip[i] != StripRestart)
                continue;

            for (var f = start + 2; f < i; f++)
            {
                var even = (f - start) % 2 == 0;
                var i0 = strip[f - 2];
                var i1 = even ? strip[f - 1] : strip[f];
                var i2 = even ? strip[f] : strip[f - 1];
                if (i0 == i1 || i1 == i2 || i0 == i2)
                    continue;

                triangles.Add(i0);
                triangles.Add(i1);
                triangles.Add(i2);
            }

            start = i + 1;
        }

        return triangles.ToArray();
    }

    /// <summary>10/10/10/2 packed unit vector, the Xenos/RSX normal encoding.</summary>
    private static Vector3 UnpackNormal(uint packed)
    {
        static float Component(uint value)
        {
            var raw = (int)(value & 0x3FF);
            if (raw >= 512)
                raw -= 1024;
            return raw / 511f;
        }

        var v = new Vector3(Component(packed), Component(packed >> 10), Component(packed >> 20));
        return v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitY;
    }

    /// <summary>
    ///     The four bytes at sMesh+0x18 state where the non-position components
    ///     live inside the vertex, so the layout is read from the file rather than
    ///     hardcoded: non-skinned meshes carry <c>0c 10 18 14</c> and skinned ones
    ///     <c>14 18 20 1c</c>. A component whose offset falls outside the stride is
    ///     simply absent.
    /// </summary>
    private readonly record struct VertexDeclaration(int Normal, int Tangent, int TexCoord, int Color)
    {
        public static VertexDeclaration Read(byte[] data, int offset)
        {
            static int Slot(byte value)
            {
                return value == 0xFF ? -1 : value;
            }

            return new VertexDeclaration(
                Slot(data[offset]), Slot(data[offset + 1]),
                Slot(data[offset + 2]), Slot(data[offset + 3]));
        }
    }

    /// <summary>
    ///     Where the sector, geometry and mesh tables live inside the CScene object.
    ///     Project 8 and Proving Ground moved these, so a file whose tables do not
    ///     resolve is reported as an unsupported revision instead of being read at
    ///     the wrong offsets.
    /// </summary>
    private readonly record struct SceneLayout(int SectorCount, int SectorTable, int GeomTable, int MeshTable)
    {
        public static bool TryResolve(byte[] data, int scene, out SceneLayout layout)
        {
            layout = default;
            if (scene < 0 || scene > data.Length - 0x90)
                return false;

            var count = ReadInt32(data, scene + 0x78);
            if (count <= 0 || count > 4096)
                return false;

            var resolved = new SceneLayout(
                count,
                scene + ReadInt32(data, scene + 0x7C),
                scene + ReadInt32(data, scene + 0x80),
                scene + ReadInt32(data, scene + 0x8C));

            if (resolved.SectorTable < scene || resolved.SectorTable > data.Length ||
                resolved.GeomTable < scene || resolved.GeomTable > data.Length ||
                resolved.MeshTable < scene || resolved.MeshTable > data.Length)
                return false;

            layout = resolved;
            return true;
        }
    }

    // ── Big-endian primitives ───────────────────────────────────────────────

    private static void RequireRange(int length, long offset, long size, string what)
    {
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
            throw new InvalidDataException($"{what} lies outside the file");
    }

    private static uint ReadUInt32(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    }

    private static int ReadInt32(byte[] d, int o)
    {
        return BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o));
    }

    private static ushort ReadUInt16(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    }

    private static float ReadSingle(byte[] d, int o)
    {
        return BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o));
    }

    private static Vector3 ReadVec3(byte[] d, int o)
    {
        return new Vector3(ReadSingle(d, o), ReadSingle(d, o + 4), ReadSingle(d, o + 8));
    }
}
