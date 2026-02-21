using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
/// Parses Neversoft TRG (trigger/script) files used in Apocalypse, Spider-Man, and THPS series.
/// Format: _TRG magic, version 2.0 (Apocalypse/THPS) or 2.1 (Spider-Man), node offset table, typed nodes.
/// </summary>
public sealed class TrgFile
{
    private const uint Magic = 0x4752545F; // "_TRG" as little-endian uint32

    public string FileName { get; init; } = "";
    public int VersionMajor { get; init; }
    public int VersionMinor { get; init; }
    public int NodeCount { get; init; }
    public List<TrgNode> Nodes { get; init; } = [];

    /// <summary>
    /// True if this is a Spider-Man variant (minor version 1).
    /// Affects parsing of certain node types (POWERUP terminators, RAILDEF extra data, etc.).
    /// </summary>
    [JsonIgnore]
    public bool IsSpiderMan => VersionMinor == 1;

    public static TrgFile Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);
        return Parse(reader, Path.GetFileName(filePath));
    }

    public static TrgFile Parse(BinaryReader reader, string fileName = "")
    {
        var magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException(
                $"Invalid TRG magic: 0x{magic:X8} (expected 0x{Magic:X8})");

        var versionMajor = reader.ReadUInt16();
        var versionMinor = reader.ReadUInt16();

        if (versionMajor != 2)
            throw new InvalidDataException(
                $"Unsupported TRG version: {versionMajor}.{versionMinor} (expected 2.x)");

        var nodeCount = (int)reader.ReadUInt32();
        if (nodeCount < 1)
            throw new InvalidDataException("TRG file has no nodes");

        // Read offset table
        var offsets = new uint[nodeCount];
        for (var i = 0; i < nodeCount; i++)
            offsets[i] = reader.ReadUInt32();

        var fileLength = reader.BaseStream.Length;
        var isSpiderMan = versionMinor == 1;

        // Parse each node
        var nodes = new List<TrgNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            reader.BaseStream.Position = offsets[i];
            var nodeSize = i < nodeCount - 1
                ? (int)(offsets[i + 1] - offsets[i])
                : (int)(fileLength - offsets[i]);

            var node = TrgNode.Parse(reader, i, offsets[i], nodeSize, isSpiderMan);
            nodes.Add(node);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void WriteJson(string outputPath)
    {
        var json = ToJson();
        File.WriteAllText(outputPath, json);
    }
}
