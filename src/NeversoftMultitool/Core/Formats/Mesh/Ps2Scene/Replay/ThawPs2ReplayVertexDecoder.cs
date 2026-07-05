using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal static class ThawPs2ReplayVertexDecoder
{
    /// <summary>
    ///     THAW/Project 8 position scale: Q12.4 fixed point (SUB_INCH_PRECISION = 16).
    ///     Proving Ground re-encodes positions as Q4.12 (×4096) in the same V3_16 slot;
    ///     callers pass 1/4096 for that variant (see <see cref="ThpgPositionUnwrapper" />).
    /// </summary>
    internal const float DefaultPositionScale = 1f / 16f;

    internal const float Q412PositionScale = 1f / 4096f;

    private const float NormalScale = 1f / 127f;
    private const float UvScale = 1f / 4096f;

    internal static ReplayVertexSource[] DecodeVertexSources(
        Vu1Memory memory,
        VifUnpackCommand? positionUnpack,
        VifUnpackCommand? normalUnpack,
        VifUnpackCommand? uvAdcUnpack,
        int count,
        float positionScale = DefaultPositionScale)
    {
        if (count <= 0 || positionUnpack is null)
            return [];

        var sources = new ReplayVertexSource[count];
        for (var i = 0; i < count; i++)
        {
            var position = Vector3.Zero;
            if (i < positionUnpack.WriteAddresses.Length)
            {
                var word = memory.ReadQword(positionUnpack.WriteAddresses[i]);
                position = new Vector3(
                    unchecked((int)word.X) * positionScale,
                    unchecked((int)word.Y) * positionScale,
                    unchecked((int)word.Z) * positionScale);
            }

            var normal = Vector3.UnitY;
            var hasNormal = false;
            if (normalUnpack is not null && i < normalUnpack.WriteAddresses.Length)
            {
                var word = memory.ReadQword(normalUnpack.WriteAddresses[i]);
                var rawNormal = new Vector3(
                    unchecked((sbyte)(byte)word.X) * NormalScale,
                    unchecked((sbyte)(byte)word.Y) * NormalScale,
                    unchecked((sbyte)(byte)word.Z) * NormalScale);
                var length = rawNormal.Length();
                normal = length > 0.001f ? rawNormal / length : Vector3.UnitY;
                hasNormal = true;
            }

            var u = 0f;
            var v = 0f;
            var hasUv = false;
            var outputFullAddress = 0;
            var duplicateFullAddress = 0;
            byte outputAddress = 0;
            byte duplicateAddress = 0;
            var outputNoKick = false;
            var duplicateNoKick = false;
            if (uvAdcUnpack is not null && i < uvAdcUnpack.WriteAddresses.Length)
            {
                var word = memory.ReadQword(uvAdcUnpack.WriteAddresses[i]);
                var outputWord = unchecked((ushort)word.Z);
                var duplicateWord = unchecked((ushort)word.W);
                u = unchecked((short)word.X) * UvScale;
                v = unchecked((short)word.Y) * UvScale;
                outputFullAddress = outputWord & 0x3FF;
                duplicateFullAddress = duplicateWord & 0x3FF;
                outputAddress = (byte)outputWord;
                duplicateAddress = (byte)duplicateWord;
                outputNoKick = (outputWord & 0x8000) != 0;
                duplicateNoKick = (duplicateWord & 0x8000) != 0;
                hasUv = true;
            }

            sources[i] = new ReplayVertexSource(
                position,
                normal,
                hasNormal,
                u,
                v,
                hasUv,
                outputFullAddress,
                duplicateFullAddress,
                outputAddress,
                duplicateAddress,
                outputNoKick,
                duplicateNoKick);
        }

        return sources;
    }

    internal static ReplayVertexSource[] DecodeRawVertexSources(
        byte[] data,
        int positionOffset,
        int normalOffset,
        int uvAdcOffset,
        int count,
        float positionScale = DefaultPositionScale)
    {
        if (count <= 0)
            return [];

        var sources = new ReplayVertexSource[count];
        for (var i = 0; i < count; i++)
        {
            var position = Vector3.Zero;
            var posOffset = positionOffset + i * 6;
            if (positionOffset >= 0 && posOffset + 6 <= data.Length)
            {
                position = new Vector3(
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset)) * positionScale,
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset + 2)) * positionScale,
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset + 4)) * positionScale);
            }

            var normal = Vector3.UnitY;
            var hasNormal = false;
            if (normalOffset >= 0)
            {
                var nrmOffset = normalOffset + i * 3;
                if (nrmOffset + 3 <= data.Length)
                {
                    var rawNormal = new Vector3(
                        (sbyte)data[nrmOffset] * NormalScale,
                        (sbyte)data[nrmOffset + 1] * NormalScale,
                        (sbyte)data[nrmOffset + 2] * NormalScale);
                    var length = rawNormal.Length();
                    normal = length > 0.001f ? rawNormal / length : Vector3.UnitY;
                    hasNormal = true;
                }
            }

            var u = 0f;
            var v = 0f;
            var hasUv = false;
            var outputFullAddress = 0;
            var duplicateFullAddress = 0;
            byte outputAddress = 0;
            byte duplicateAddress = 0;
            var outputNoKick = false;
            var duplicateNoKick = false;
            if (uvAdcOffset >= 0)
            {
                var uvOffset = uvAdcOffset + i * 8;
                if (uvOffset + 8 <= data.Length)
                {
                    u = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOffset)) * UvScale;
                    v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOffset + 2)) * UvScale;
                    var outputWord = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(uvOffset + 4));
                    var duplicateWord = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(uvOffset + 6));
                    outputFullAddress = outputWord & 0x3FF;
                    duplicateFullAddress = duplicateWord & 0x3FF;
                    outputAddress = (byte)outputWord;
                    duplicateAddress = (byte)duplicateWord;
                    outputNoKick = (outputWord & 0x8000) != 0;
                    duplicateNoKick = (duplicateWord & 0x8000) != 0;
                    hasUv = true;
                }
            }

            sources[i] = new ReplayVertexSource(
                position,
                normal,
                hasNormal,
                u,
                v,
                hasUv,
                outputFullAddress,
                duplicateFullAddress,
                outputAddress,
                duplicateAddress,
                outputNoKick,
                duplicateNoKick);
        }

        return sources;
    }

    internal static ReplayEmittedVertex[] BuildEmittedVertices(IReadOnlyList<ReplayVertexSource> vertexSources)
    {
        if (vertexSources.Count == 0)
            return [];

        var emitted = new List<ReplayEmittedVertex>(vertexSources.Count * 2);
        for (var i = 0; i < vertexSources.Count; i++)
        {
            var source = vertexSources[i];
            if (!source.HasUv)
                continue;

            emitted.Add(new ReplayEmittedVertex
            {
                SourceIndex = i,
                Source = source,
                FullOutputAddress = source.OutputFullAddress,
                OutputAddress = source.OutputAddress,
                IsNoKick = source.OutputNoKick,
                IsDuplicate = false
            });

            if (source.DuplicateFullAddress != source.OutputFullAddress ||
                source.DuplicateAddress != source.OutputAddress)
            {
                emitted.Add(new ReplayEmittedVertex
                {
                    SourceIndex = i,
                    Source = source,
                    FullOutputAddress = source.DuplicateFullAddress,
                    OutputAddress = source.DuplicateAddress,
                    IsNoKick = source.DuplicateNoKick,
                    IsDuplicate = true
                });
            }
        }

        return [.. emitted];
    }
}
