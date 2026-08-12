using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Wgt;

/// <summary>Strict parser for the source-proven little-endian compiled WGT version-1 layout.</summary>
public static class CutsceneWeightMapFile
{
    private const int HeaderSize = 8;
    private const uint SupportedVersion = 1;
    private const int WeightsPerVertex = 3;
    private const int WeightBytesPerVertex = WeightsPerVertex * sizeof(float);
    private const int IndexBytesPerVertex = WeightsPerVertex;
    private const int SerializedBytesPerVertex = WeightBytesPerVertex + IndexBytesPerVertex;

    public static CutsceneWeightMapDocument Parse(
        ReadOnlySpan<byte> data,
        CutsceneWeightMapPlatform platform)
    {
        if (platform is not CutsceneWeightMapPlatform.Ps2 and not CutsceneWeightMapPlatform.Xbox)
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported WGT platform");

        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"WGT header is truncated: expected at least {HeaderSize} bytes, found {data.Length}");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported WGT version {version}; expected exactly version 1");

        var vertexCount = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (vertexCount < 0)
            throw new InvalidDataException($"WGT vertex count is negative: {vertexCount}");

        var expectedSize = checked((long)HeaderSize + checked((long)vertexCount * SerializedBytesPerVertex));
        if (data.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"WGT version 1 requires exactly {expectedSize} bytes for {vertexCount} vertices; " +
                $"found {data.Length}");
        }

        var vertices = new CutsceneWeightMapVertex[vertexCount];
        var weightOffset = HeaderSize;
        var indexOffset = checked(HeaderSize + vertexCount * WeightBytesPerVertex);
        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var tupleWeightOffset = weightOffset + vertexIndex * WeightBytesPerVertex;
            var weight0 = ReadSingleLittleEndian(data[tupleWeightOffset..]);
            var weight1 = ReadSingleLittleEndian(data[(tupleWeightOffset + sizeof(float))..]);
            var weight2 = ReadSingleLittleEndian(data[(tupleWeightOffset + 2 * sizeof(float))..]);
            if (!float.IsFinite(weight0) || !float.IsFinite(weight1) || !float.IsFinite(weight2))
            {
                throw new InvalidDataException(
                    $"WGT vertex {vertexIndex} contains a non-finite mesh-scaling weight");
            }

            var tupleIndexOffset = indexOffset + vertexIndex * IndexBytesPerVertex;
            vertices[vertexIndex] = new CutsceneWeightMapVertex(
                weight0,
                weight1,
                weight2,
                unchecked((sbyte)data[tupleIndexOffset]),
                unchecked((sbyte)data[tupleIndexOffset + 1]),
                unchecked((sbyte)data[tupleIndexOffset + 2]));
        }

        return new CutsceneWeightMapDocument
        {
            Platform = platform,
            Version = version,
            SerializedSize = data.Length,
            SerializedSha256 = Convert.ToHexString(SHA256.HashData(data)),
            Vertices = vertices
        };
    }

    private static float ReadSingleLittleEndian(ReadOnlySpan<byte> data)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
    }
}
