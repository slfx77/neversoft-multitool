using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Psp;

/// <summary>
///     A Neversoft PSP mesh payload (<c>0xC0EDBABE</c>) carried directly by
///     <c>.skin.psp</c>/<c>.mdl.psp</c>, or embedded once in a
///     <c>.geom.psp</c>/bare <c>.mdl</c> wrapper.
/// </summary>
/// <remarks>
///     Geometry is a serialized PSP GE display list followed by the exact vertex
///     bytes consumed by its PRIM commands. The shipped corpus uses only
///     non-indexed triangle lists and strips. Weight bytes are part of the proven
///     vertex layout and are consumed, but deliberately not exposed as skinning:
///     joining each GE bone-palette call to the matching <c>.ske</c> hierarchy is
///     a separate, still-unproven contract. The resulting scene is therefore the
///     authored rigid bind pose.
/// </remarks>
public sealed class PspGeMeshFile
{
    public const uint Magic = 0xC0EDBABE;

    private const int HeaderSize = 0x40;
    private const int MinimumFirstDataOffset = 0x60;
    private const uint DefaultMaterialChecksum = 0x50535001;

    private static ReadOnlySpan<byte> MagicBytes => [0xBE, 0xBA, 0xED, 0xC0];

    private PspGeMeshFile(ParsedXbxScene scene, PspGeMeshSummary summary)
    {
        Scene = scene;
        Summary = summary;
    }

    /// <summary>The shared scene IR consumed by the existing GLB/Blend path.</summary>
    public ParsedXbxScene Scene { get; }

    /// <summary>Structural counts gathered while walking the display list.</summary>
    public PspGeMeshSummary Summary { get; }

    /// <summary>
    ///     Cheap family hint used only to decide whether a bounded bare-name probe
    ///     should re-read the whole file. A support verdict must use
    ///     <see cref="TryInspect" />.
    /// </summary>
    public static bool ContainsMagic(ReadOnlySpan<byte> data)
    {
        return data.IndexOf(MagicBytes) >= 0;
    }

    /// <summary>
    ///     Validates the unique payload without allocating decoded vertices.
    ///     Header sections must form one checked contiguous chain, every GE vertex
    ///     layout must be supported, and the PRIM walk must consume the declared
    ///     vertex buffer exactly (including the format's four-byte group padding).
    /// </summary>
    public static bool TryInspect(
        byte[] data,
        out PspGeMeshSummary summary,
        out string? error)
    {
        try
        {
            var located = LocateUniquePayload(data);
            summary = located.Summary;
            error = null;
            return true;
        }
        catch (InvalidDataException ex)
        {
            summary = default;
            error = ex.Message;
            return false;
        }
        catch (OverflowException ex)
        {
            summary = default;
            error = $"PSP GE mesh arithmetic overflow: {ex.Message}";
            return false;
        }
    }

    public static bool TryParse(byte[] data, out PspGeMeshFile? file)
    {
        try
        {
            file = Parse(data);
            return true;
        }
        catch (InvalidDataException)
        {
            file = null;
            return false;
        }
        catch (OverflowException)
        {
            file = null;
            return false;
        }
    }

    public static PspGeMeshFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var located = LocateUniquePayload(data);
        var parsed = ParsePayload(data, located.Offset, buildScene: true);
        return new PspGeMeshFile(parsed.Scene!, parsed.Summary);
    }

    private static LocatedPayload LocateUniquePayload(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        LocatedPayload? found = null;
        string? firstStructuralError = null;
        var searchOffset = 0;
        while (searchOffset <= data.Length - sizeof(uint))
        {
            var relative = data.AsSpan(searchOffset).IndexOf(MagicBytes);
            if (relative < 0)
                break;

            var offset = checked(searchOffset + relative);
            searchOffset = checked(offset + 1);

            // Every direct and embedded corpus payload is at least dword-aligned.
            // Refusing byte-shifted hits sharply reduces arbitrary-data matches.
            if ((offset & 3) != 0)
                continue;

            ParsedPayload candidate;
            try
            {
                candidate = ParsePayload(data, offset, buildScene: false);
            }
            catch (InvalidDataException ex)
            {
                firstStructuralError ??= ex.Message;
                continue;
            }

            if (found.HasValue)
            {
                throw new InvalidDataException(
                    $"PSP GE mesh contains more than one structurally valid 0x{Magic:X8} payload");
            }

            found = new LocatedPayload(offset, candidate.Summary);
        }

        if (found.HasValue)
            return found.Value;

        throw new InvalidDataException(
            firstStructuralError ?? $"PSP GE mesh contains no valid 0x{Magic:X8} payload");
    }

    private static ParsedPayload ParsePayload(byte[] data, int payloadOffset, bool buildScene)
    {
        EnsureAvailable(data.Length, payloadOffset, HeaderSize, "header");
        var payload = data.AsSpan(payloadOffset);
        if (ReadU32(payload, 0) != Magic)
            throw new InvalidDataException("PSP GE mesh magic is missing");

        var firstDataOffset = ReadU32(payload, 0x08);
        var textureOffset = ReadU32(payload, 0x14);
        var textureSize = ReadU32(payload, 0x18);
        var vertexBufferOffset = ReadU32(payload, 0x20);
        var vertexBufferSize = ReadU32(payload, 0x24);
        var displayListOffset = ReadU32(payload, 0x2C);
        var displayListSize = ReadU32(payload, 0x30);

        if (ReadU32(payload, 0x1C) != 0 || ReadU32(payload, 0x28) != 0
                                             || ReadU32(payload, 0x34) != 0
                                             || ReadU32(payload, 0x3C) != 0)
        {
            throw new InvalidDataException("PSP GE mesh reserved header words are not zero");
        }

        if (firstDataOffset < MinimumFirstDataOffset || firstDataOffset > textureOffset)
        {
            throw new InvalidDataException(
                $"PSP GE mesh first-data offset 0x{firstDataOffset:X} is outside " +
                $"0x{MinimumFirstDataOffset:X}..0x{textureOffset:X}");
        }

        if ((firstDataOffset & 0xF) != 0 || (textureOffset & 0xF) != 0
                                               || (displayListOffset & 3) != 0
                                               || (displayListSize & 3) != 0
                                               || (vertexBufferOffset & 3) != 0)
        {
            throw new InvalidDataException("PSP GE mesh section alignment is invalid");
        }

        var textureEnd = checked((ulong)textureOffset + textureSize);
        var displayListEnd = checked((ulong)displayListOffset + displayListSize);
        var vertexBufferEnd = checked((ulong)vertexBufferOffset + vertexBufferSize);
        if (textureEnd != displayListOffset || displayListEnd != vertexBufferOffset)
        {
            throw new InvalidDataException(
                "PSP GE mesh texture, display-list, and vertex-buffer sections are not contiguous");
        }

        var available = checked((ulong)(data.Length - payloadOffset));
        if (vertexBufferEnd > available)
        {
            throw new InvalidDataException(
                $"PSP GE mesh payload declares {vertexBufferEnd} bytes but only {available} remain");
        }

        // A payload at byte zero is the whole file. Embedded payloads have a
        // wrapper prefix and may retain a small wrapper trailer.
        if (payloadOffset == 0 && vertexBufferEnd != available)
        {
            throw new InvalidDataException(
                $"Direct PSP GE mesh consumes {vertexBufferEnd} of {available} bytes");
        }

        var dlOffset = CheckedInt(displayListOffset, "display-list offset");
        var dlSize = CheckedInt(displayListSize, "display-list size");
        var vbOffset = CheckedInt(vertexBufferOffset, "vertex-buffer offset");
        var vbSize = CheckedInt(vertexBufferSize, "vertex-buffer size");

        var meshes = buildScene ? new List<XbxMesh>() : null;
        uint? vertexType = null;
        var uScale = 1f;
        var vScale = 1f;
        var uOffset = 0f;
        var vOffset = 0f;
        var groupBytes = 0;
        var completedGroupBytes = 0;
        var primitiveCount = 0;
        var vertexCount = 0;
        var weightedVertexCount = 0;
        var theoreticalTriangleCount = 0;

        for (var commandOffset = 0; commandOffset < dlSize; commandOffset += sizeof(uint))
        {
            var word = ReadU32(payload, checked(dlOffset + commandOffset));
            var command = (byte)(word >> 24);
            var argument = word & 0x00FFFFFF;
            switch (command)
            {
                case 0x12: // VTYPE
                    _ = DecodeVertexLayout(argument);
                    vertexType = argument;
                    break;

                case 0x48: // TEXSCALEU
                    uScale = DecodeGeFloat(argument, "U scale");
                    break;
                case 0x49: // TEXSCALEV
                    vScale = DecodeGeFloat(argument, "V scale");
                    break;
                case 0x4A: // TEXOFFSETU
                    uOffset = DecodeGeFloat(argument, "U offset");
                    break;
                case 0x4B: // TEXOFFSETV
                    vOffset = DecodeGeFloat(argument, "V offset");
                    break;

                case 0x04: // PRIM
                {
                    if (!vertexType.HasValue)
                        throw new InvalidDataException("PSP GE PRIM appears before VTYPE");

                    var primitiveType = (int)((argument >> 16) & 7);
                    if (primitiveType is not 3 and not 4)
                    {
                        throw new InvalidDataException(
                            $"Unsupported PSP GE primitive type {primitiveType} (only triangles/strips occur)");
                    }

                    var count = (int)(argument & 0xFFFF);
                    var layout = DecodeVertexLayout(vertexType.Value);
                    var primitiveBytes = checked(count * layout.Stride);
                    var startInBuffer = checked(completedGroupBytes + groupBytes);
                    if (startInBuffer < 0 || primitiveBytes > vbSize - startInBuffer)
                    {
                        throw new InvalidDataException(
                            $"PSP GE PRIM needs {primitiveBytes} vertex bytes at {startInBuffer}, " +
                            $"outside the {vbSize}-byte buffer");
                    }

                    if (buildScene && count > 0)
                    {
                        meshes!.Add(DecodePrimitive(
                            payload,
                            checked(vbOffset + startInBuffer),
                            count,
                            primitiveType,
                            layout,
                            uScale,
                            vScale,
                            uOffset,
                            vOffset));
                    }

                    groupBytes = checked(groupBytes + primitiveBytes);
                    primitiveCount++;
                    vertexCount = checked(vertexCount + count);
                    if (layout.WeightFormat != 0)
                        weightedVertexCount = checked(weightedVertexCount + count);
                    theoreticalTriangleCount = checked(theoreticalTriangleCount +
                        (primitiveType == 3 ? count / 3 : Math.Max(0, count - 2)));
                    break;
                }

                case 0xFF: // Neversoft draw-group terminator
                    completedGroupBytes = checked(completedGroupBytes + Align4(groupBytes));
                    groupBytes = 0;
                    if (completedGroupBytes > vbSize)
                        throw new InvalidDataException("PSP GE draw groups overrun the declared vertex buffer");
                    break;
            }
        }

        var consumedVertexBytes = checked(completedGroupBytes + Align4(groupBytes));
        if (consumedVertexBytes != vbSize)
        {
            throw new InvalidDataException(
                $"PSP GE display list consumes {consumedVertexBytes} of {vbSize} vertex bytes");
        }

        var summary = new PspGeMeshSummary(
            payloadOffset,
            checked((int)vertexBufferEnd),
            primitiveCount,
            vertexCount,
            weightedVertexCount,
            theoreticalTriangleCount);
        if (!buildScene)
            return new ParsedPayload(null, summary);

        var material = new XbxMaterial
        {
            Checksum = DefaultMaterialChecksum,
            NameChecksum = DefaultMaterialChecksum,
            NumPasses = 0,
            AlphaCutoff = 0,
            Passes = []
        };

        XbxSector[] sectors;
        if (meshes!.Count == 0)
        {
            sectors = [];
        }
        else
        {
            var allVertices = meshes.SelectMany(static mesh => mesh.Vertices).ToArray();
            var min = new Vector3(
                allVertices.Min(static vertex => vertex.Position.X),
                allVertices.Min(static vertex => vertex.Position.Y),
                allVertices.Min(static vertex => vertex.Position.Z));
            var max = new Vector3(
                allVertices.Max(static vertex => vertex.Position.X),
                allVertices.Max(static vertex => vertex.Position.Y),
                allVertices.Max(static vertex => vertex.Position.Z));
            var center = (min + max) * 0.5f;
            var radius = allVertices.Max(vertex => Vector3.Distance(center, vertex.Position));
            sectors =
            [
                new XbxSector
                {
                    Checksum = ReadU32(payload, 0x0C),
                    BoneIndex = -1,
                    Flags = 0,
                    BboxMin = min,
                    BboxMax = max,
                    BsphereCenter = center,
                    BsphereRadius = radius,
                    Meshes = meshes.ToArray()
                }
            ];
        }

        return new ParsedPayload(
            new ParsedXbxScene
            {
                Materials = meshes.Count == 0 ? [] : [material],
                Sectors = sectors,
                Links = []
            },
            summary);
    }

    private static XbxMesh DecodePrimitive(
        ReadOnlySpan<byte> payload,
        int vertexOffset,
        int count,
        int primitiveType,
        VertexLayout layout,
        float uScale,
        float vScale,
        float uOffset,
        float vOffset)
    {
        var vertices = new XbxVertex[count];
        var indices = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            var record = payload.Slice(checked(vertexOffset + index * layout.Stride), layout.Stride);
            vertices[index] = DecodeVertex(record, layout, uScale, vScale, uOffset, vOffset);
            indices[index] = checked((ushort)index);
        }

        return new XbxMesh
        {
            MaterialChecksum = DefaultMaterialChecksum,
            Vertices = vertices,
            FaceIndices = indices,
            IsPreTriangulated = primitiveType == 3
        };
    }

    private static XbxVertex DecodeVertex(
        ReadOnlySpan<byte> record,
        VertexLayout layout,
        float uScale,
        float vScale,
        float uOffset,
        float vOffset)
    {
        var sourceX = BinaryPrimitives.ReadInt16LittleEndian(record[layout.PositionOffset..]);
        var sourceY = BinaryPrimitives.ReadInt16LittleEndian(record[(layout.PositionOffset + 2)..]);
        var sourceZ = BinaryPrimitives.ReadInt16LittleEndian(record[(layout.PositionOffset + 4)..]);
        var position = new Vector3(sourceX / 16f, sourceZ / 16f, -sourceY / 16f);

        var hasNormal = layout.NormalOffset >= 0;
        var normal = Vector3.UnitY;
        if (hasNormal)
        {
            var nx = unchecked((sbyte)record[layout.NormalOffset]);
            var ny = unchecked((sbyte)record[layout.NormalOffset + 1]);
            var nz = unchecked((sbyte)record[layout.NormalOffset + 2]);
            normal = new Vector3(nx / 127f, nz / 127f, -ny / 127f);
        }

        var packedColor = BinaryPrimitives.ReadUInt16LittleEndian(record[layout.ColorOffset..]);
        var color = new Vector4(
            (packedColor & 0xF) / 15f,
            ((packedColor >> 4) & 0xF) / 15f,
            ((packedColor >> 8) & 0xF) / 15f,
            ((packedColor >> 12) & 0xF) / 15f);

        var texCoord = Vector2.Zero;
        if (layout.TexCoordOffset >= 0)
        {
            var u = BinaryPrimitives.ReadUInt16LittleEndian(record[layout.TexCoordOffset..]);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(record[(layout.TexCoordOffset + 2)..]);
            var decodedU = u / 32768f * uScale + uOffset;
            var decodedV = v / 32768f * vScale + vOffset;
            texCoord = new Vector2(decodedU, 1f - decodedV);
        }

        return new XbxVertex
        {
            Position = position,
            Normal = normal,
            Color = color,
            TexCoord = texCoord,
            HasNormal = hasNormal,
            HasColor = true,
            // Weight bytes were consumed to reach PositionOffset, but no bone
            // palette / .ske join is inferred for this rigid bind-pose route.
            HasSkinData = false
        };
    }

    private static VertexLayout DecodeVertexLayout(uint vertexType)
    {
        var texCoordFormat = (int)(vertexType & 3);
        var colorFormat = (int)((vertexType >> 2) & 7);
        var normalFormat = (int)((vertexType >> 5) & 3);
        var positionFormat = (int)((vertexType >> 7) & 3);
        var weightFormat = (int)((vertexType >> 9) & 3);
        var indexFormat = (int)((vertexType >> 11) & 3);
        var weightCount = (int)((vertexType >> 14) & 7) + 1;
        var morphCount = (int)((vertexType >> 18) & 7) + 1;
        var through = (vertexType & (1u << 23)) != 0 || (vertexType & (1u << 24)) != 0;
        var reserved = vertexType & 0xFE622000u;

        if (texCoordFormat is not 0 and not 2
            || colorFormat != 6
            || normalFormat is not 0 and not 1
            || positionFormat != 2
            || weightFormat is not 0 and not 1
            || indexFormat != 0
            || morphCount != 1
            || through
            || reserved != 0)
        {
            throw new InvalidDataException($"Unsupported PSP GE VTYPE 0x{vertexType:X6}");
        }

        var offset = 0;
        var alignment = 1;

        if (weightFormat != 0)
        {
            offset = Align(offset, 1);
            offset = checked(offset + weightCount);
        }

        var texCoordOffset = -1;
        if (texCoordFormat == 2)
        {
            alignment = Math.Max(alignment, 2);
            offset = Align(offset, 2);
            texCoordOffset = offset;
            offset = checked(offset + 4);
        }

        alignment = Math.Max(alignment, 2);
        offset = Align(offset, 2);
        var colorOffset = offset;
        offset = checked(offset + 2);

        var normalOffset = -1;
        if (normalFormat == 1)
        {
            normalOffset = offset;
            offset = checked(offset + 3);
        }

        offset = Align(offset, 2);
        var positionOffset = offset;
        offset = checked(offset + 6);
        var stride = Align(offset, alignment);
        return new VertexLayout(
            stride,
            weightFormat,
            texCoordOffset,
            colorOffset,
            normalOffset,
            positionOffset);
    }

    private static float DecodeGeFloat(uint argument, string field)
    {
        var value = BitConverter.Int32BitsToSingle(unchecked((int)(argument << 8)));
        if (!float.IsFinite(value))
            throw new InvalidDataException($"PSP GE {field} is not finite");
        return value;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(uint))
            throw new InvalidDataException("PSP GE mesh field overruns its containing payload");
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static void EnsureAvailable(int length, int offset, int count, string field)
    {
        if (offset < 0 || count < 0 || offset > length - count)
            throw new InvalidDataException($"PSP GE mesh {field} is truncated");
    }

    private static int CheckedInt(uint value, string field)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"PSP GE mesh {field} exceeds the supported range");
        return (int)value;
    }

    private static int Align(int value, int alignment)
    {
        return checked((value + alignment - 1) & ~(alignment - 1));
    }

    private static int Align4(int value)
    {
        return Align(value, 4);
    }

    private readonly record struct LocatedPayload(int Offset, PspGeMeshSummary Summary);

    private readonly record struct ParsedPayload(ParsedXbxScene? Scene, PspGeMeshSummary Summary);

    private readonly record struct VertexLayout(
        int Stride,
        int WeightFormat,
        int TexCoordOffset,
        int ColorOffset,
        int NormalOffset,
        int PositionOffset);
}

/// <summary>Structural census for one validated PSP GE mesh payload.</summary>
/// <param name="PayloadOffset">Byte offset of the unique payload in its containing file.</param>
/// <param name="PayloadSize">Payload bytes from magic through the end of the vertex buffer.</param>
/// <param name="PrimitiveCount">Number of PSP GE PRIM commands.</param>
/// <param name="VertexCount">Total sequential vertex records consumed.</param>
/// <param name="WeightedVertexCount">Records whose VTYPE carries weights (not emitted as skinning).</param>
/// <param name="TheoreticalTriangleCount">List/strip triangles before geometric degenerates are removed.</param>
public readonly record struct PspGeMeshSummary(
    int PayloadOffset,
    int PayloadSize,
    int PrimitiveCount,
    int VertexCount,
    int WeightedVertexCount,
    int TheoreticalTriangleCount);
