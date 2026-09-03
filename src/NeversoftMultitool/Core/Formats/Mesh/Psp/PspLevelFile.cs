using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Psp;

/// <summary>
///     Static world geometry and embedded PSP GE textures from Neversoft's
///     <c>.psp_level</c> files (THUG2 Remix and Project 8 PSP).
/// </summary>
/// <remarks>
///     The two primitive streams account for the complete declared static
///     vertex buffer. The 64-byte object records and their auxiliary command
///     regions are a separate runtime path and are deliberately retained only
///     as counts until their placement contract is proven.
/// </remarks>
public sealed class PspLevelFile
{
    public const string Suffix = ".psp_level";
    public const uint Version = 11;
    public const uint HeaderSentinel = 0x37373737;

    private const int HeaderSize = 0x40;
    private const uint VertexBufferReturn = 0x0B000000;
    private const uint DefaultMaterialChecksum = 0xF5000000;
    private const uint MaterialChecksumBase = 0xF5000001;
    private const uint TextureChecksumBase = 0xF6000001;
    private const uint SectorChecksum = 0xF7000001;
    private const int FixedStride = 12;
    private const int FloatStride = 20;
    private const int MaxVerticesPerMesh = ushort.MaxValue + 1;

    private readonly Dictionary<uint, PspLevelTexture> _textureByChecksum;

    private PspLevelFile(
        ParsedXbxScene scene,
        PspLevelSummary summary,
        IReadOnlyList<PspLevelTexture> textures)
    {
        Scene = scene;
        Summary = summary;
        Textures = textures;
        _textureByChecksum = textures.ToDictionary(static texture => texture.Checksum);
    }

    /// <summary>The proven static world represented in the shared scene IR.</summary>
    public ParsedXbxScene Scene { get; }

    /// <summary>Exact container, command-list, and primitive-stream counts.</summary>
    public PspLevelSummary Summary { get; }

    /// <summary>Decoded base levels of the embedded GE texture resources.</summary>
    public IReadOnlyList<PspLevelTexture> Textures { get; }

    /// <summary>Returns an embedded base-level texture as PNG for the shared exporter.</summary>
    public byte[]? ResolveTexture(uint checksum)
    {
        return _textureByChecksum.TryGetValue(checksum, out var texture)
            ? texture.GetPngBytes()
            : null;
    }

    /// <summary>
    ///     Exact, fail-closed structure probe. A positive result accounts for
    ///     every file byte, every static vertex byte, and every referenced GE
    ///     texture region without allocating a scene or PNG images.
    /// </summary>
    public static bool TryInspect(
        byte[] data,
        out PspLevelSummary summary,
        out string? error)
    {
        try
        {
            var parsed = ParseCore(data, buildScene: false);
            summary = parsed.Summary;
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            summary = default;
            error = ex.Message;
            return false;
        }
    }

    public static bool TryParse(byte[] data, out PspLevelFile? file, out string? error)
    {
        try
        {
            file = Parse(data);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        {
            file = null;
            error = ex.Message;
            return false;
        }
    }

    public static PspLevelFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var parsed = ParseCore(data, buildScene: true);
        return new PspLevelFile(parsed.Scene!, parsed.Summary, parsed.Textures!);
    }

    /// <summary>
    ///     Exact standalone-sky spelling used by both supported PSP games.
    ///     This identifies an independently viewable camera-locked sky; it does
    ///     not infer which authored main level selects that sky.
    /// </summary>
    public static bool IsStandaloneSkyFileName(string fileName)
    {
        return Path.GetFileName(fileName)
            .EndsWith("_sky" + Suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedLevel ParseCore(byte[] data, bool buildScene)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < HeaderSize)
            throw new InvalidDataException("PSP level header is truncated");

        var h = ReadHeader(data);
        var layout = ComputeLayout(h, data.Length);
        var textureResult = ParseTextureLists(data, h, layout, buildScene);

        var meshes = buildScene ? new List<XbxMesh>() : null;
        var materialDescriptors = textureResult.Materials;
        if (materialDescriptors.Count == 0)
            materialDescriptors.Add(MaterialDescriptor.Default);

        var streamResult = ParsePrimitiveStreams(
            data,
            h,
            layout,
            textureResult.ListStartToMaterial,
            materialDescriptors,
            meshes,
            buildScene);

        var expectedReferences = textureResult.ListStarts.Skip(1).ToArray();
        if (!streamResult.CommandReferences.SequenceEqual(expectedReferences))
        {
            throw new InvalidDataException(
                "PSP level primitive streams do not reference every GE command list " +
                "exactly once in ascending order");
        }

        var summary = new PspLevelSummary(
            data.LongLength,
            h.DynamicObjectCount,
            h.TextureBlobBytes,
            h.BoxCount,
            h.StaticVertexBytes,
            h.TextureCommandCount,
            h.AuxiliaryCommandCount,
            h.Auxiliary44RecordCount,
            h.Auxiliary12RecordCount,
            h.PrimitiveShortCount,
            textureResult.ListStarts.Count,
            textureResult.Textures.Count,
            streamResult.PrimitiveCount,
            streamResult.VertexCount,
            streamResult.TheoreticalTriangleCount,
            streamResult.FixedVertexBytes,
            streamResult.FloatVertexBytes);

        if (!buildScene)
            return new ParsedLevel(null, summary, null);

        var materials = materialDescriptors
            .Select(CreateMaterial)
            .ToArray();
        XbxSector[] sectors;
        if (meshes!.Count == 0)
        {
            sectors = [];
        }
        else
        {
            var bounds = streamResult.Bounds;
            var center = (bounds.Min + bounds.Max) * 0.5f;
            var radius = meshes
                .SelectMany(static mesh => mesh.Vertices)
                .Max(vertex => Vector3.Distance(center, vertex.Position));
            sectors =
            [
                new XbxSector
                {
                    Checksum = SectorChecksum,
                    BoneIndex = -1,
                    Flags = 0x03,
                    BboxMin = bounds.Min,
                    BboxMax = bounds.Max,
                    BsphereCenter = center,
                    BsphereRadius = radius,
                    Meshes = meshes.ToArray()
                }
            ];
        }

        var scene = new ParsedXbxScene
        {
            Materials = materials,
            Sectors = sectors,
            Links = []
        };
        return new ParsedLevel(scene, summary, textureResult.Textures);
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        var words = new uint[16];
        for (var i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);

        if (words[0] != Version)
            throw new InvalidDataException($"Unsupported PSP level version {words[0]} (expected {Version})");
        if (words[15] != HeaderSentinel)
        {
            throw new InvalidDataException(
                $"PSP level header sentinel is 0x{words[15]:X8}, expected 0x{HeaderSentinel:X8}");
        }
        if (words[13] > words[9])
            throw new InvalidDataException("PSP level first primitive-stream boundary exceeds the stream");
        if (words[14] > words[4])
            throw new InvalidDataException("PSP level first vertex-stream boundary exceeds the vertex buffer");
        if ((words[2] == 0) != (words[5] == 0))
        {
            throw new InvalidDataException(
                "PSP level embedded texture bytes and GE texture commands disagree");
        }

        return new Header(
            words[1], words[2], words[3], words[4], words[5], words[6],
            words[7], words[8], words[9],
            unchecked((int)words[10]), unchecked((int)words[11]), unchecked((int)words[12]),
            words[13], words[14]);
    }

    private static Layout ComputeLayout(Header h, int fileLength)
    {
        ulong offset = HeaderSize;
        var objectRecords = offset;
        offset = checked(offset + 64UL * h.DynamicObjectCount);
        var textureBlob = offset;
        offset = checked(offset + h.TextureBlobBytes);
        var boxes = offset;
        offset = checked(offset + 16UL * h.BoxCount);
        var vertices = offset;
        offset = checked(offset + h.StaticVertexBytes);
        var vertexReturn = offset;
        offset = checked(offset + sizeof(uint));
        var textureCommands = offset;
        offset = checked(offset + 4UL * h.TextureCommandCount);
        var auxiliaryCommands = offset;
        offset = checked(offset + 4UL * h.AuxiliaryCommandCount);
        var auxiliary44 = offset;
        offset = checked(offset + 44UL * h.Auxiliary44RecordCount);
        var auxiliary12 = offset;
        offset = checked(offset + 12UL * h.Auxiliary12RecordCount);
        var objectLookup = offset;
        offset = checked(offset + 4UL * h.DynamicObjectCount);
        var boxLookup = offset;
        offset = checked(offset + 4UL * h.BoxCount);
        var boxVisibility = offset;
        offset = checked(offset + Align4(h.BoxCount));
        var primitiveStream = offset;
        offset = checked(offset + 2UL * h.PrimitiveShortCount);

        if (offset != (ulong)fileLength)
        {
            throw new InvalidDataException(
                $"PSP level sections consume {offset} of {fileLength} bytes");
        }

        var returnOffset = CheckedInt(vertexReturn, "vertex-buffer return offset");
        return new Layout(
            CheckedInt(objectRecords, "object records"),
            CheckedInt(textureBlob, "texture blob"),
            CheckedInt(boxes, "boxes"),
            CheckedInt(vertices, "static vertices"),
            returnOffset,
            CheckedInt(textureCommands, "texture commands"),
            CheckedInt(auxiliaryCommands, "auxiliary commands"),
            CheckedInt(auxiliary44, "44-byte auxiliary records"),
            CheckedInt(auxiliary12, "12-byte auxiliary records"),
            CheckedInt(objectLookup, "object lookup"),
            CheckedInt(boxLookup, "box lookup"),
            CheckedInt(boxVisibility, "box visibility"),
            CheckedInt(primitiveStream, "primitive stream"));
    }

    private static TextureParseResult ParseTextureLists(
        byte[] data,
        Header h,
        Layout layout,
        bool buildPixels)
    {
        if (ReadU32(data, layout.VertexReturn) != VertexBufferReturn)
        {
            throw new InvalidDataException(
                $"PSP level static vertex buffer is not followed by GE RET 0x{VertexBufferReturn:X8}");
        }

        var materials = new List<MaterialDescriptor>();
        var listStarts = new List<int>();
        var startToMaterial = new Dictionary<int, int>();
        var textures = new List<PspLevelTexture>();
        var textureByKey = new Dictionary<TextureKey, PspLevelTexture>();
        if (h.TextureCommandCount == 0)
        {
            return new TextureParseResult(
                materials, listStarts, startToMaterial, textures);
        }

        var commandCount = CheckedInt(h.TextureCommandCount, "texture command count");
        var state = new GeState();
        var listStart = 0;
        var sawPaletteAddress = false;
        var sawBaseAddress = false;
        var pointersSeen = new bool[4];
        var widthsSeen = new bool[4];
        var sizesSeen = new bool[4];
        listStarts.Add(0);

        for (var commandIndex = 0; commandIndex < commandCount; commandIndex++)
        {
            var word = ReadU32(data, checked(layout.TextureCommands + commandIndex * 4));
            var command = (byte)(word >> 24);
            var argument = word & 0x00FFFFFF;
            if (!IsSupportedTextureCommand(command))
            {
                throw new InvalidDataException(
                    $"PSP level GE texture list has unsupported command 0x{command:X2} at {commandIndex}");
            }

            switch (command)
            {
                case 0x0B: // RET
                {
                    if (argument != 0)
                        throw new InvalidDataException("PSP level GE RET has a nonzero argument");

                    var hasNewTexture = sawPaletteAddress || sawBaseAddress;
                    if (sawPaletteAddress != sawBaseAddress)
                    {
                        throw new InvalidDataException(
                            $"PSP level GE list {listStart} changes only one of CLUT/base texture address");
                    }

                    if (hasNewTexture)
                    {
                        ValidateMipCommands(state, pointersSeen, widthsSeen, sizesSeen, listStart);
                        var key = CreateTextureKey(state, h.TextureBlobBytes, listStart);
                        if (!textureByKey.TryGetValue(key, out var texture))
                        {
                            var checksum = checked(TextureChecksumBase + (uint)textures.Count);
                            texture = DecodeTexture(
                                data.AsSpan(layout.TextureBlob, CheckedInt(h.TextureBlobBytes, "texture blob bytes")),
                                key,
                                checksum,
                                buildPixels,
                                listStart);
                            textureByKey.Add(key, texture);
                            textures.Add(texture);
                        }

                        state.TextureChecksum = texture.Checksum;
                    }

                    if (!state.TextureChecksum.HasValue)
                    {
                        throw new InvalidDataException(
                            $"PSP level GE list {listStart} has no established texture resource");
                    }

                    var materialIndex = materials.Count;
                    materials.Add(state.Snapshot(checked(MaterialChecksumBase + (uint)materialIndex)));
                    startToMaterial.Add(listStart, materialIndex);

                    if (commandIndex + 1 < commandCount)
                    {
                        listStart = commandIndex + 1;
                        listStarts.Add(listStart);
                        sawPaletteAddress = false;
                        sawBaseAddress = false;
                        Array.Clear(pointersSeen);
                        Array.Clear(widthsSeen);
                        Array.Clear(sizesSeen);
                    }

                    break;
                }

                case 0x1D: // CULLFACEENABLE
                    RequireBooleanArgument(command, argument, commandIndex);
                    state.CullEnabled = argument != 0;
                    break;
                case 0x21: // ALPHABLENDENABLE
                    RequireBooleanArgument(command, argument, commandIndex);
                    state.AlphaBlendEnabled = argument != 0;
                    break;
                case 0x48: // TEXSCALEU
                    state.UScale = DecodeGeFloat(argument, "U scale", commandIndex);
                    break;
                case 0x49: // TEXSCALEV
                    state.VScale = DecodeGeFloat(argument, "V scale", commandIndex);
                    break;
                case 0x4A: // TEXOFFSETU
                    state.UOffset = DecodeGeFloat(argument, "U offset", commandIndex);
                    break;
                case 0x4B: // TEXOFFSETV
                    state.VOffset = DecodeGeFloat(argument, "V offset", commandIndex);
                    break;
                case >= 0xA0 and <= 0xA3: // TEXADDR0..3
                {
                    var level = command - 0xA0;
                    state.TexturePointers[level] = argument;
                    pointersSeen[level] = true;
                    if (level == 0)
                        sawBaseAddress = true;
                    break;
                }
                case >= 0xA8 and <= 0xAB: // TEXBUFWIDTH0..3 (+ address high byte)
                {
                    var level = command - 0xA8;
                    state.BufferWidths[level] = (int)(argument & 0xFFFF);
                    widthsSeen[level] = true;
                    break;
                }
                case 0xB0: // CLUTADDR
                    state.PalettePointer = argument;
                    sawPaletteAddress = true;
                    break;
                case >= 0xB8 and <= 0xBB: // TEXSIZE0..3
                {
                    var level = command - 0xB8;
                    state.WidthExponents[level] = (int)(argument & 0xFF);
                    state.HeightExponents[level] = (int)((argument >> 8) & 0xFF);
                    if ((argument & 0xFF0000) != 0)
                        throw new InvalidDataException("PSP level GE texture size has nonzero reserved bits");
                    sizesSeen[level] = true;
                    break;
                }
                case 0xC2: // TEXMODE: swizzle + max mip level
                    if ((argument & 0xFFFF) != 1 || (argument >> 16) > 3)
                    {
                        throw new InvalidDataException(
                            $"PSP level GE texture mode 0x{argument:X6} is outside the proven swizzled 1-4 mip set");
                    }

                    state.MipCount = checked((int)(argument >> 16) + 1);
                    break;
                case 0xC3: // TEXFORMAT
                    if (argument is not 4 and not 5)
                    {
                        throw new InvalidDataException(
                            $"PSP level GE texture format {argument} is not T4/T8");
                    }

                    state.PixelFormat = checked((int)argument);
                    break;
                case 0xC4: // LOADCLUT, in 32-byte blocks
                    if (argument == 0)
                        throw new InvalidDataException("PSP level GE palette load is empty");
                    state.PaletteBytes = checked((int)argument * 32);
                    break;
                case 0xC6: // TEXFILTER
                    state.FilteringMode = argument;
                    break;
                case 0xC7: // TEXWRAP: V in byte 1, U in byte 0
                    if ((argument & 0xFFFEFE) != 0)
                    {
                        throw new InvalidDataException(
                            $"PSP level GE texture wrap 0x{argument:X6} is outside repeat/clamp");
                    }
                    state.UClamp = (argument & 1) != 0;
                    state.VClamp = (argument & 0x100) != 0;
                    break;
                case 0xDB: // ALPHATEST, reference in bits 8..15
                    state.AlphaCutoff = (int)((argument >> 8) & 0xFF);
                    break;
            }
        }

        if ((ReadU32(data, checked(layout.TextureCommands + (commandCount - 1) * 4)) >> 24) != 0x0B)
            throw new InvalidDataException("PSP level GE texture command section does not end in RET");

        return new TextureParseResult(materials, listStarts, startToMaterial, textures);
    }

    private static void ValidateMipCommands(
        GeState state,
        IReadOnlyList<bool> pointersSeen,
        IReadOnlyList<bool> widthsSeen,
        IReadOnlyList<bool> sizesSeen,
        int listStart)
    {
        if (state.PixelFormat is not 4 and not 5 || state.MipCount is < 1 or > 4
            || state.PaletteBytes <= 0)
        {
            throw new InvalidDataException(
                $"PSP level GE list {listStart} starts a texture before format/mips/palette are established");
        }

        for (var level = 0; level < state.MipCount; level++)
        {
            // Compact lists always replace every address. Buffer widths and
            // logical sizes are ordinary GE state and are intentionally
            // inherited when the new image has the same layout.
            if (!pointersSeen[level])
            {
                throw new InvalidDataException(
                    $"PSP level GE list {listStart} omits mip {level} address");
            }
        }

        for (var level = state.MipCount; level < 4; level++)
        {
            if (pointersSeen[level] || widthsSeen[level] || sizesSeen[level])
            {
                throw new InvalidDataException(
                    $"PSP level GE list {listStart} describes mip {level} beyond its declared mip count");
            }
        }
    }

    private static TextureKey CreateTextureKey(GeState state, uint textureBlobBytes, int listStart)
    {
        var levels = new TextureLevelKey[state.MipCount];
        var bpp = state.PixelFormat == 4 ? 4 : 8;
        for (var level = 0; level < levels.Length; level++)
        {
            var widthExponent = state.WidthExponents[level];
            var heightExponent = state.HeightExponents[level];
            if (widthExponent is < 0 or > 12 || heightExponent is < 0 or > 12)
            {
                throw new InvalidDataException(
                    $"PSP level GE list {listStart} has implausible mip {level} exponents " +
                    $"{widthExponent},{heightExponent}");
            }
            if (level > 0
                && (widthExponent != Math.Max(0, state.WidthExponents[0] - level)
                    || heightExponent != Math.Max(0, state.HeightExponents[0] - level)))
            {
                throw new InvalidDataException(
                    $"PSP level GE list {listStart} mip {level} dimensions do not descend from mip 0");
            }

            var width = 1 << widthExponent;
            var height = 1 << heightExponent;
            var bufferWidth = state.BufferWidths[level];
            if (bufferWidth < width || bufferWidth > 8192)
            {
                throw new InvalidDataException(
                    $"PSP level GE list {listStart} mip {level} buffer width {bufferWidth} " +
                    $"cannot contain {width} pixels");
            }

            var rowBytes = Align16(checked((bufferWidth * bpp + 7) / 8));
            var encodedBytes = checked(rowBytes * height);
            EnsureBlobRange(
                state.TexturePointers[level], encodedBytes, textureBlobBytes,
                $"GE list {listStart} mip {level}");
            levels[level] = new TextureLevelKey(
                state.TexturePointers[level], width, height, bufferWidth, rowBytes, encodedBytes);
        }

        if ((state.PaletteBytes & 3) != 0 || state.PaletteBytes > 1024)
        {
            throw new InvalidDataException(
                $"PSP level GE list {listStart} palette size {state.PaletteBytes} is invalid");
        }
        EnsureBlobRange(
            state.PalettePointer, state.PaletteBytes, textureBlobBytes,
            $"GE list {listStart} palette");

        return new TextureKey(
            state.PixelFormat,
            state.PalettePointer,
            state.PaletteBytes,
            levels);
    }

    private static PspLevelTexture DecodeTexture(
        ReadOnlySpan<byte> blob,
        TextureKey key,
        uint checksum,
        bool buildPixels,
        int listStart)
    {
        var baseLevel = key.Levels[0];
        var packed = blob.Slice(CheckedInt(baseLevel.Pointer, "texture pointer"), baseLevel.EncodedBytes);
        var linear = GeUnswizzle(packed, baseLevel.RowBytes, baseLevel.Height);
        var palette = blob.Slice(CheckedInt(key.PalettePointer, "palette pointer"), key.PaletteBytes);
        var paletteEntries = key.PaletteBytes / 4;
        byte[]? rgba = buildPixels
            ? new byte[checked(baseLevel.Width * baseLevel.Height * 4)]
            : null;

        for (var y = 0; y < baseLevel.Height; y++)
        {
            var row = y * baseLevel.RowBytes;
            for (var x = 0; x < baseLevel.Width; x++)
            {
                var index = key.PixelFormat == 5
                    ? linear[row + x]
                    : (x & 1) == 0
                        ? linear[row + (x >> 1)] & 0xF
                        : linear[row + (x >> 1)] >> 4;
                if (index >= paletteEntries)
                {
                    throw new InvalidDataException(
                        $"PSP level GE list {listStart} pixel index {index} exceeds its " +
                        $"{paletteEntries}-entry palette");
                }

                if (rgba != null)
                    palette.Slice(index * 4, 4).CopyTo(rgba.AsSpan((y * baseLevel.Width + x) * 4, 4));
            }
        }

        return new PspLevelTexture(
            checksum,
            baseLevel.Width,
            baseLevel.Height,
            key.PixelFormat,
            key.Levels.Length,
            rgba);
    }

    private static PrimitiveParseResult ParsePrimitiveStreams(
        byte[] data,
        Header h,
        Layout layout,
        IReadOnlyDictionary<int, int> listStartToMaterial,
        IReadOnlyList<MaterialDescriptor> materials,
        List<XbxMesh>? meshes,
        bool buildScene)
    {
        var references = new List<int>();
        var accumulator = buildScene ? new MeshAccumulator(meshes!) : null;
        var bounds = new BoundsBuilder();
        var primitiveCount = 0;
        var vertexCount = 0;
        var triangleCount = 0;
        var fixedBytes = 0;
        var floatBytes = 0;
        var currentMaterial = 0;

        ParseStream(
            0,
            CheckedInt(h.FirstStreamShortBoundary, "first stream short boundary"),
            0,
            CheckedInt(h.FirstStreamVertexBoundary, "first stream vertex boundary"));
        ParseStream(
            CheckedInt(h.FirstStreamShortBoundary, "first stream short boundary"),
            CheckedInt(h.PrimitiveShortCount, "primitive short count"),
            CheckedInt(h.FirstStreamVertexBoundary, "first stream vertex boundary"),
            CheckedInt(h.StaticVertexBytes, "static vertex bytes"));
        accumulator?.Finish();

        return new PrimitiveParseResult(
            references,
            primitiveCount,
            vertexCount,
            triangleCount,
            fixedBytes,
            floatBytes,
            bounds.ToBounds());

        void ParseStream(int firstShort, int endShort, int firstVertex, int endVertex)
        {
            if (firstShort == endShort)
            {
                if (firstVertex != endVertex)
                    throw new InvalidDataException("Empty PSP level primitive stream owns vertex bytes");
                return;
            }

            var shortIndex = firstShort;
            var vertexOffset = firstVertex;
            var stride = FixedStride;
            var terminated = false;
            while (shortIndex < endShort)
            {
                var token = ReadU16(data, checked(layout.PrimitiveStream + shortIndex * 2));
                shortIndex++;
                if (token != 0)
                {
                    var count = token >> 11;
                    var boxReference = token & 0x7FF;
                    if (count == 0)
                        throw new InvalidDataException("PSP level primitive token has a zero vertex count");
                    if (boxReference < 2 || (ulong)boxReference > checked(h.BoxCount + 1UL))
                    {
                        throw new InvalidDataException(
                            $"PSP level primitive box reference {boxReference} is outside " +
                            $"2..{h.BoxCount + 1UL}");
                    }

                    var bytes = checked(count * stride);
                    if (bytes > endVertex - vertexOffset)
                        throw new InvalidDataException("PSP level primitive overruns its vertex stream");

                    if (buildScene)
                    {
                        var descriptor = materials[currentMaterial];
                        var decoded = DecodeVertices(
                            data.AsSpan(checked(layout.Vertices + vertexOffset), bytes),
                            count,
                            stride,
                            h,
                            descriptor,
                            bounds);
                        accumulator!.AddStrip(descriptor.MaterialChecksum, decoded);
                    }
                    else
                    {
                        ValidateVertices(
                            data.AsSpan(checked(layout.Vertices + vertexOffset), bytes),
                            count,
                            stride,
                            h,
                            materials[currentMaterial],
                            bounds);
                    }

                    primitiveCount = checked(primitiveCount + 1);
                    vertexCount = checked(vertexCount + count);
                    triangleCount = checked(triangleCount + Math.Max(0, count - 2));
                    if (stride == FixedStride)
                        fixedBytes = checked(fixedBytes + bytes);
                    else
                        floatBytes = checked(floatBytes + bytes);
                    vertexOffset = checked(vertexOffset + bytes);
                    continue;
                }

                if (shortIndex >= endShort)
                    throw new InvalidDataException("PSP level primitive escape is truncated");
                var special = ReadI16(data, checked(layout.PrimitiveStream + shortIndex * 2));
                shortIndex++;
                if (special > 0)
                {
                    if (!listStartToMaterial.TryGetValue(special, out currentMaterial))
                    {
                        throw new InvalidDataException(
                            $"PSP level primitive stream references non-list GE command {special}");
                    }
                    references.Add(special);
                }
                else if (special == -1)
                {
                    stride = stride == FixedStride ? FloatStride : FixedStride;
                }
                else if (special == 0)
                {
                    if (shortIndex != endShort)
                        throw new InvalidDataException("PSP level primitive stream has data after its terminator");
                    terminated = true;
                }
                else
                {
                    throw new InvalidDataException(
                        $"PSP level primitive stream has unknown control value {special}");
                }
            }

            if (!terminated)
                throw new InvalidDataException("Nonempty PSP level primitive stream has no terminator");
            if (vertexOffset != endVertex)
            {
                throw new InvalidDataException(
                    $"PSP level primitive stream consumes {vertexOffset - firstVertex} of " +
                    $"{endVertex - firstVertex} vertex bytes");
            }
        }
    }

    private static XbxVertex[] DecodeVertices(
        ReadOnlySpan<byte> records,
        int count,
        int stride,
        Header header,
        MaterialDescriptor material,
        BoundsBuilder bounds)
    {
        var vertices = new XbxVertex[count];
        for (var i = 0; i < count; i++)
        {
            var vertex = DecodeVertex(records.Slice(i * stride, stride), stride, header, material);
            bounds.Include(vertex.Position);
            vertices[i] = vertex;
        }
        return vertices;
    }

    private static void ValidateVertices(
        ReadOnlySpan<byte> records,
        int count,
        int stride,
        Header header,
        MaterialDescriptor material,
        BoundsBuilder bounds)
    {
        for (var i = 0; i < count; i++)
        {
            var vertex = DecodeVertex(records.Slice(i * stride, stride), stride, header, material);
            bounds.Include(vertex.Position);
        }
    }

    private static XbxVertex DecodeVertex(
        ReadOnlySpan<byte> record,
        int stride,
        Header header,
        MaterialDescriptor material)
    {
        var rawU = BinaryPrimitives.ReadUInt16LittleEndian(record);
        var rawV = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        var texCoord = new Vector2(
            rawU / 32768f * material.UScale + material.UOffset,
            rawV / 32768f * material.VScale + material.VOffset);
        if (!float.IsFinite(texCoord.X) || !float.IsFinite(texCoord.Y))
            throw new InvalidDataException("PSP level vertex has a non-finite transformed UV");

        Vector4 color;
        Vector3 sourcePosition;
        if (stride == FixedStride)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            color = new Vector4(
                (packed & 0xF) / 15f,
                ((packed >> 4) & 0xF) / 15f,
                ((packed >> 8) & 0xF) / 15f,
                ((packed >> 12) & 0xF) / 15f);
            sourcePosition = new Vector3(
                (float)((BinaryPrimitives.ReadInt16LittleEndian(record[6..]) + (double)header.OriginX) / 4d),
                (float)((BinaryPrimitives.ReadInt16LittleEndian(record[8..]) + (double)header.OriginY) / 4d),
                (float)((BinaryPrimitives.ReadInt16LittleEndian(record[10..]) + (double)header.OriginZ) / 4d));
        }
        else
        {
            color = new Vector4(record[4] / 255f, record[5] / 255f, record[6] / 255f, record[7] / 255f);
            var rawX = BinaryPrimitives.ReadSingleLittleEndian(record[8..]);
            var rawY = BinaryPrimitives.ReadSingleLittleEndian(record[12..]);
            var rawZ = BinaryPrimitives.ReadSingleLittleEndian(record[16..]);
            if (!float.IsFinite(rawX) || !float.IsFinite(rawY) || !float.IsFinite(rawZ))
                throw new InvalidDataException("PSP level float vertex has a non-finite position");
            sourcePosition = new Vector3(
                (float)((rawX * 32768d + header.OriginX) / 4d),
                (float)((rawY * 32768d + header.OriginY) / 4d),
                (float)((rawZ * 32768d + header.OriginZ) / 4d));
        }

        if (!float.IsFinite(sourcePosition.X)
            || !float.IsFinite(sourcePosition.Y)
            || !float.IsFinite(sourcePosition.Z))
        {
            throw new InvalidDataException("PSP level vertex position exceeds the finite scene range");
        }

        return new XbxVertex
        {
            Position = new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y),
            Normal = Vector3.UnitY,
            Color = color,
            TexCoord = texCoord,
            HasNormal = false,
            HasColor = true,
            HasSkinData = false
        };
    }

    private static XbxMaterial CreateMaterial(MaterialDescriptor descriptor)
    {
        XbxPass[] passes = descriptor.TextureChecksum.HasValue
            ?
            [
                new XbxPass
                {
                    TextureChecksum = descriptor.TextureChecksum.Value,
                    BlendMode = descriptor.AlphaBlendEnabled ? 5u : 0u,
                    UAddressing = descriptor.UClamp ? 3u : 0u,
                    VAddressing = descriptor.VClamp ? 3u : 0u,
                    FilteringMode = descriptor.FilteringMode
                }
            ]
            : [];
        return new XbxMaterial
        {
            Checksum = descriptor.MaterialChecksum,
            NameChecksum = descriptor.MaterialChecksum,
            NumPasses = passes.Length,
            AlphaCutoff = descriptor.AlphaCutoff,
            Sorted = descriptor.AlphaBlendEnabled,
            SingleSided = descriptor.CullEnabled,
            NoBfc = !descriptor.CullEnabled,
            Passes = passes
        };
    }

    private static byte[] GeUnswizzle(ReadOnlySpan<byte> swizzled, int stride, int rows)
    {
        if ((stride & 15) != 0 || stride <= 0 || rows <= 0
            || swizzled.Length != checked(stride * rows))
        {
            throw new InvalidDataException("PSP level GE swizzle dimensions are inconsistent");
        }

        var linear = new byte[swizzled.Length];
        var source = 0;
        for (var blockY = 0; blockY < rows; blockY += 8)
        {
            var rowsInBlock = Math.Min(8, rows - blockY);
            for (var blockX = 0; blockX < stride; blockX += 16)
            for (var row = 0; row < rowsInBlock; row++)
            {
                swizzled.Slice(source, 16)
                    .CopyTo(linear.AsSpan((blockY + row) * stride + blockX, 16));
                source += 16;
            }
        }

        if (source != swizzled.Length)
            throw new InvalidDataException("PSP level GE swizzle walk did not consume its mip");
        return linear;
    }

    private static bool IsSupportedTextureCommand(byte command)
    {
        return command is 0x0B or 0x1D or 0x21 or 0x47 or 0x48 or 0x49 or 0x4A or 0x4B
            or >= 0xA0 and <= 0xA3
            or >= 0xA8 and <= 0xAB
            or 0xB0
            or >= 0xB8 and <= 0xBB
            or 0xC2 or 0xC3 or 0xC4 or 0xC6 or 0xC7 or 0xCB or 0xCF
            or 0xD0 or 0xDB or 0xDF or 0xE0 or 0xE1;
    }

    private static float DecodeGeFloat(uint argument, string field, int commandIndex)
    {
        var value = BitConverter.Int32BitsToSingle(unchecked((int)(argument << 8)));
        if (!float.IsFinite(value))
            throw new InvalidDataException($"PSP level GE {field} at command {commandIndex} is non-finite");
        return value;
    }

    private static void RequireBooleanArgument(byte command, uint argument, int commandIndex)
    {
        if (argument > 1)
        {
            throw new InvalidDataException(
                $"PSP level GE boolean command 0x{command:X2} at {commandIndex} has argument {argument}");
        }
    }

    private static void EnsureBlobRange(uint pointer, int length, uint blobLength, string label)
    {
        var end = checked((ulong)pointer + (uint)length);
        if (end > blobLength)
        {
            throw new InvalidDataException(
                $"PSP level {label} range {pointer}..{end} exceeds {blobLength}-byte texture blob");
        }
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
    }

    private static ushort ReadU16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
    }

    private static short ReadI16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short)));
    }

    private static int CheckedInt(uint value, string field)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"PSP level {field} exceeds the supported in-memory range");
        return (int)value;
    }

    private static int CheckedInt(ulong value, string field)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"PSP level {field} exceeds the supported in-memory range");
        return (int)value;
    }

    private static ulong Align4(uint value)
    {
        return checked(((ulong)value + 3UL) & ~3UL);
    }

    private static int Align16(int value)
    {
        return checked((value + 15) & ~15);
    }

    private sealed class GeState
    {
        public uint[] TexturePointers { get; } = new uint[4];
        public int[] BufferWidths { get; } = new int[4];
        public int[] WidthExponents { get; } = [-1, -1, -1, -1];
        public int[] HeightExponents { get; } = [-1, -1, -1, -1];
        public uint PalettePointer { get; set; }
        public int PaletteBytes { get; set; }
        public int PixelFormat { get; set; }
        public int MipCount { get; set; }
        public float UScale { get; set; } = 1f;
        public float VScale { get; set; } = 1f;
        public float UOffset { get; set; }
        public float VOffset { get; set; }
        public bool UClamp { get; set; }
        public bool VClamp { get; set; }
        public bool CullEnabled { get; set; }
        public bool AlphaBlendEnabled { get; set; }
        public int AlphaCutoff { get; set; }
        public uint FilteringMode { get; set; }
        public uint? TextureChecksum { get; set; }

        public MaterialDescriptor Snapshot(uint checksum)
        {
            return new MaterialDescriptor(
                checksum,
                TextureChecksum,
                UScale,
                VScale,
                UOffset,
                VOffset,
                UClamp,
                VClamp,
                CullEnabled,
                AlphaBlendEnabled,
                AlphaCutoff,
                FilteringMode);
        }
    }

    private sealed class MeshAccumulator(List<XbxMesh> destination)
    {
        private readonly List<ushort> _indices = [];
        private readonly List<XbxVertex> _vertices = [];
        private uint _materialChecksum;
        private bool _hasMaterial;

        public void AddStrip(uint materialChecksum, IReadOnlyList<XbxVertex> strip)
        {
            if (_hasMaterial
                && (_materialChecksum != materialChecksum
                    || _vertices.Count + strip.Count > MaxVerticesPerMesh))
            {
                Finish();
            }

            _hasMaterial = true;
            _materialChecksum = materialChecksum;
            var first = _vertices.Count;
            _vertices.AddRange(strip);
            for (var i = 2; i < strip.Count; i++)
            {
                var a = checked((ushort)(first + i - 2));
                var b = checked((ushort)(first + i - 1));
                var c = checked((ushort)(first + i));
                if ((i & 1) == 0)
                {
                    _indices.Add(a);
                    _indices.Add(b);
                }
                else
                {
                    _indices.Add(b);
                    _indices.Add(a);
                }
                _indices.Add(c);
            }
        }

        public void Finish()
        {
            if (!_hasMaterial)
                return;
            if (_indices.Count > 0)
            {
                var min = new Vector3(
                    _vertices.Min(static vertex => vertex.Position.X),
                    _vertices.Min(static vertex => vertex.Position.Y),
                    _vertices.Min(static vertex => vertex.Position.Z));
                var max = new Vector3(
                    _vertices.Max(static vertex => vertex.Position.X),
                    _vertices.Max(static vertex => vertex.Position.Y),
                    _vertices.Max(static vertex => vertex.Position.Z));
                var center = (min + max) * 0.5f;
                destination.Add(new XbxMesh
                {
                    MaterialChecksum = _materialChecksum,
                    BboxMin = min,
                    BboxMax = max,
                    BsphereCenter = center,
                    BsphereRadius = _vertices.Max(vertex => Vector3.Distance(center, vertex.Position)),
                    Vertices = _vertices.ToArray(),
                    FaceIndices = _indices.ToArray(),
                    IsPreTriangulated = true
                });
            }

            _vertices.Clear();
            _indices.Clear();
            _hasMaterial = false;
        }
    }

    private sealed class BoundsBuilder
    {
        private Vector3 _min = new(float.PositiveInfinity);
        private Vector3 _max = new(float.NegativeInfinity);

        public void Include(Vector3 point)
        {
            _min = Vector3.Min(_min, point);
            _max = Vector3.Max(_max, point);
        }

        public Bounds ToBounds()
        {
            return float.IsPositiveInfinity(_min.X)
                ? new Bounds(Vector3.Zero, Vector3.Zero)
                : new Bounds(_min, _max);
        }
    }

    private readonly record struct Header(
        uint DynamicObjectCount,
        uint TextureBlobBytes,
        uint BoxCount,
        uint StaticVertexBytes,
        uint TextureCommandCount,
        uint AuxiliaryCommandCount,
        uint Auxiliary44RecordCount,
        uint Auxiliary12RecordCount,
        uint PrimitiveShortCount,
        int OriginX,
        int OriginY,
        int OriginZ,
        uint FirstStreamShortBoundary,
        uint FirstStreamVertexBoundary);

    private readonly record struct Layout(
        int ObjectRecords,
        int TextureBlob,
        int Boxes,
        int Vertices,
        int VertexReturn,
        int TextureCommands,
        int AuxiliaryCommands,
        int Auxiliary44,
        int Auxiliary12,
        int ObjectLookup,
        int BoxLookup,
        int BoxVisibility,
        int PrimitiveStream);

    private readonly record struct TextureLevelKey(
        uint Pointer,
        int Width,
        int Height,
        int BufferWidth,
        int RowBytes,
        int EncodedBytes);

    private sealed record TextureKey(
        int PixelFormat,
        uint PalettePointer,
        int PaletteBytes,
        TextureLevelKey[] Levels)
    {
        public bool Equals(TextureKey? other)
        {
            return other != null
                   && PixelFormat == other.PixelFormat
                   && PalettePointer == other.PalettePointer
                   && PaletteBytes == other.PaletteBytes
                   && Levels.AsSpan().SequenceEqual(other.Levels);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(PixelFormat);
            hash.Add(PalettePointer);
            hash.Add(PaletteBytes);
            foreach (var level in Levels)
                hash.Add(level);
            return hash.ToHashCode();
        }
    }

    private readonly record struct MaterialDescriptor(
        uint MaterialChecksum,
        uint? TextureChecksum,
        float UScale,
        float VScale,
        float UOffset,
        float VOffset,
        bool UClamp,
        bool VClamp,
        bool CullEnabled,
        bool AlphaBlendEnabled,
        int AlphaCutoff,
        uint FilteringMode)
    {
        public static MaterialDescriptor Default => new(
            DefaultMaterialChecksum, null, 1f, 1f, 0f, 0f,
            false, false, false, false, 0, 0);
    }

    private readonly record struct TextureParseResult(
        List<MaterialDescriptor> Materials,
        List<int> ListStarts,
        Dictionary<int, int> ListStartToMaterial,
        List<PspLevelTexture> Textures);

    private readonly record struct Bounds(Vector3 Min, Vector3 Max);

    private readonly record struct PrimitiveParseResult(
        List<int> CommandReferences,
        int PrimitiveCount,
        int VertexCount,
        int TheoreticalTriangleCount,
        int FixedVertexBytes,
        int FloatVertexBytes,
        Bounds Bounds);

    private readonly record struct ParsedLevel(
        ParsedXbxScene? Scene,
        PspLevelSummary Summary,
        IReadOnlyList<PspLevelTexture>? Textures);
}

public readonly record struct PspLevelSummary(
    long FileBytes,
    uint DynamicObjectCount,
    uint TextureBlobBytes,
    uint BoxCount,
    uint StaticVertexBytes,
    uint TextureCommandCount,
    uint AuxiliaryCommandCount,
    uint Auxiliary44RecordCount,
    uint Auxiliary12RecordCount,
    uint PrimitiveShortCount,
    int CommandListCount,
    int TextureCount,
    int PrimitiveCount,
    int VertexCount,
    int TheoreticalTriangleCount,
    int FixedVertexBytes,
    int FloatVertexBytes);

public sealed class PspLevelTexture
{
    private readonly Lazy<byte[]>? _pngBytes;

    internal PspLevelTexture(
        uint checksum,
        int width,
        int height,
        int pixelFormat,
        int mipCount,
        byte[]? rgba)
    {
        Checksum = checksum;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        MipCount = mipCount;
        Rgba = rgba;
        if (rgba != null)
        {
            _pngBytes = new Lazy<byte[]>(
                () => ImageWriter.WritePngToMemory(width, height, rgba),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public uint Checksum { get; }
    public int Width { get; }
    public int Height { get; }
    public int PixelFormat { get; }
    public int MipCount { get; }
    public byte[]? Rgba { get; }

    internal byte[] GetPngBytes()
    {
        return _pngBytes?.Value
               ?? throw new InvalidOperationException("This inspected PSP level did not retain texture pixels");
    }
}
