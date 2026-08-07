using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
///     Parses Neversoft TRG (trigger/script) files used in Apocalypse, Spider-Man, and THPS series.
///     Format: _TRG magic, version 2.0 (Apocalypse/THPS) or 2.1 (Spider-Man), node offset table, typed nodes.
/// </summary>
public sealed class TrgFile
{
    private const uint Magic = 0x4752545F; // "_TRG" as little-endian uint32

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public string FileName { get; init; } = "";
    public int VersionMajor { get; init; }
    public int VersionMinor { get; init; }
    public int NodeCount { get; init; }
    public List<TrgNode> Nodes { get; init; } = [];

    /// <summary>
    ///     True if this is a Spider-Man variant (minor version 1).
    ///     Affects parsing of certain node types (POWERUP terminators, RAILDEF extra data, etc.).
    /// </summary>
    [JsonIgnore]
    public bool IsSpiderMan => VersionMinor == 1;

    public static TrgFile Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return Parse(reader, Path.GetFileName(filePath));
    }

    /// <summary>
    ///     Parses a TRG in whichever byte order it is stored in. The N64 ports
    ///     keep this grammar field for field and re-encode it big-endian, so the
    ///     order is sniffed from the magic rather than from the file name: a PS1
    ///     file spells it <c>_TRG</c> and its N64 counterpart <c>GRT_</c>, which
    ///     is the same u32 read the other way round.
    /// </summary>
    public static TrgFile Parse(BinaryReader reader, string fileName = "")
    {
        return Parse(new EndianBinaryReader(reader, DetectBigEndian(reader.BaseStream)), fileName);
    }

    /// <summary>
    ///     Peeks the magic and reports whether the file is big-endian, leaving
    ///     the stream where it found it. An unrecognisable magic reports
    ///     little-endian so the parse below raises the normal error.
    /// </summary>
    private static bool DetectBigEndian(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        var origin = stream.Position;
        Span<byte> head = stackalloc byte[4];
        var read = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        stream.Position = origin;
        return read == 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == Magic;
    }

    private static TrgFile Parse(EndianBinaryReader reader, string fileName)
    {
        var magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"Invalid TRG magic: 0x{magic:X8} (expected 0x{Magic:X8})");

        // One u32 carrying major in its low half and minor in its high half,
        // for the same reason the PSX header's version/magic word is: a PS1
        // file stores 0x00000002 little-endian and its N64 counterpart stores
        // that value big-endian, so the WORD agrees across both while a pair of
        // u16s would appear exchanged. Bit-for-bit the previous two reads on the
        // little-endian path.
        var versionWord = reader.ReadUInt32();
        var versionMajor = (ushort)(versionWord & 0xFFFF);
        var versionMinor = (ushort)(versionWord >> 16);

        if (versionMajor != 2)
            throw new InvalidDataException(
                $"Unsupported TRG version: {versionMajor}.{versionMinor} (expected 2.x)");

        var nodeCountValue = reader.ReadUInt32();
        if (nodeCountValue < 1)
            throw new InvalidDataException("TRG file has no nodes");
        if (nodeCountValue > int.MaxValue)
            throw new InvalidDataException($"TRG node count is too large: {nodeCountValue}");

        var fileLength = reader.BaseStream.Length;
        var offsetTableStart = reader.BaseStream.Position;
        var remainingBytes = fileLength - offsetTableStart;
        if (remainingBytes < 0 || nodeCountValue > (ulong)remainingBytes / sizeof(uint))
        {
            throw new InvalidDataException(
                $"TRG node table ({nodeCountValue} entries) exceeds the file length");
        }

        var nodeCount = (int)nodeCountValue;
        var offsetTableEnd = offsetTableStart + (long)nodeCount * sizeof(uint);

        // Read offset table
        var offsets = new uint[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            var offset = reader.ReadUInt32();
            if (offset < offsetTableEnd || offset >= fileLength)
            {
                throw new InvalidDataException(
                    $"TRG node {i} offset 0x{offset:X8} is outside the node-data range");
            }

            if (i > 0 && offset < offsets[i - 1])
            {
                throw new InvalidDataException(
                    $"TRG node {i} offset 0x{offset:X8} decreases from the previous offset");
            }

            offsets[i] = offset;
        }

        var isSpiderMan = versionMinor == 1;

        // Parse each node
        var nodes = new List<TrgNode>(nodeCount);
        for (var runStart = 0; runStart < nodeCount;)
        {
            // A few shipped Enter Electro files intentionally alias adjacent
            // node IDs to the same serialized node. Give every member of such
            // a run the payload through the next distinct offset, instead of
            // treating the first alias as a zero-sized corrupt node.
            var runEnd = runStart + 1;
            while (runEnd < nodeCount && offsets[runEnd] == offsets[runStart])
                runEnd++;

            var nodeSizeValue = runEnd < nodeCount
                ? offsets[runEnd] - (long)offsets[runStart]
                : fileLength - offsets[runStart];
            if (nodeSizeValue <= 0 || nodeSizeValue > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"TRG node {runStart} has invalid size {nodeSizeValue}");
            }

            var nodeSize = (int)nodeSizeValue;

            for (var nodeIndex = runStart; nodeIndex < runEnd; nodeIndex++)
            {
                reader.BaseStream.Position = offsets[nodeIndex];
                var node = TrgNode.Parse(
                    reader, nodeIndex, offsets[nodeIndex], nodeSize, isSpiderMan);
                nodes.Add(node);
            }

            runStart = runEnd;
        }

        return new TrgFile
        {
            FileName = fileName,
            VersionMajor = versionMajor,
            VersionMinor = versionMinor,
            NodeCount = nodeCount,
            Nodes = nodes
        };
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public void WriteJson(string outputPath)
    {
        var json = ToJson();
        File.WriteAllText(outputPath, json);
    }
}
