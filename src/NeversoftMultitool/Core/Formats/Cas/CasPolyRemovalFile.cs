using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Cas;

/// <summary>Strict parser for version-2 little-endian PS2 and Xbox CAS polygon-removal sidecars.</summary>
public static class CasPolyRemovalFile
{
    private const int HeaderSize = 12;
    private const uint SupportedVersion = 2;
    private const int Ps2RecordSize = 8;
    private const int XboxRecordSize = 12;

    public static CasPolyRemovalDocument Parse(ReadOnlySpan<byte> data, CasPolyRemovalPlatform platform)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"CAS header is truncated: expected at least {HeaderSize} bytes, found {data.Length}");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported CAS version {version}; expected exactly version 2");

        var removalMask = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (count < 0)
            throw new InvalidDataException($"CAS entry count is negative: {count}");

        var recordSize = platform switch
        {
            CasPolyRemovalPlatform.Ps2 => Ps2RecordSize,
            CasPolyRemovalPlatform.Xbox => XboxRecordSize,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported CAS platform")
        };

        var expectedSize = checked((long)HeaderSize + checked((long)count * recordSize));
        if (data.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"CAS {platform} layout requires exactly {expectedSize} bytes for {count} entries; found {data.Length}");
        }

        var entries = new CasPolyRemovalEntry[count];
        var offset = HeaderSize;
        for (var i = 0; i < entries.Length; i++)
        {
            var mask = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (platform == CasPolyRemovalPlatform.Ps2)
            {
                var vertexReference = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
                entries[i] = new CasPs2PolyRemovalEntry(mask, vertexReference);
            }
            else
            {
                var data0 = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
                var data1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
                entries[i] = new CasXboxPolyRemovalEntry(mask, data0, data1);
            }

            offset += recordSize;
        }

        return new CasPolyRemovalDocument
        {
            Platform = platform,
            Version = version,
            RemovalMask = removalMask,
            SerializedSize = data.Length,
            SerializedSha256 = Convert.ToHexString(SHA256.HashData(data)),
            Entries = entries
        };
    }
}
