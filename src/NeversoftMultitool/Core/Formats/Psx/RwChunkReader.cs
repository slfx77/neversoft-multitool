using System.Text;

namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Shared RenderWare 3.x chunk reading utilities.
///     Used by both RwTxdFile (texture dictionaries) and RwDffFile (3D models).
///     Chunk header: type(u32) + size(u32) + version(u32) = 12 bytes.
/// </summary>
internal static class RwChunkReader
{
    // ── Chunk type constants ──
    internal const uint RW_STRUCT = 0x0001;
    internal const uint RW_STRING = 0x0002;
    internal const uint RW_EXTENSION = 0x0003;
    internal const uint RW_TEXTURE = 0x0006;
    internal const uint RW_MATERIAL = 0x0007;
    internal const uint RW_MATERIAL_LIST = 0x0008;
    internal const uint RW_FRAME_LIST = 0x000E;
    internal const uint RW_GEOMETRY = 0x000F;
    internal const uint RW_CLUMP = 0x0010;
    internal const uint RW_ATOMIC = 0x0014;
    internal const uint RW_TEX_NATIVE = 0x0015;
    internal const uint RW_TEX_DICT = 0x0016;
    internal const uint RW_GEOMETRY_LIST = 0x001A;
    internal const uint RW_ATOMIC_SECTION = 0x0009;
    internal const uint RW_PLANE_SECTION = 0x000A;
    internal const uint RW_WORLD = 0x000B;
    internal const uint RW_SKIN_PLG = 0x0116;
    internal const uint RW_BINMESH_PLG = 0x050E;

    internal static (uint type, uint size, uint version) ReadChunkHeader(byte[] data, ref int offset)
    {
        var type = BitConverter.ToUInt32(data, offset);
        var size = BitConverter.ToUInt32(data, offset + 4);
        var version = BitConverter.ToUInt32(data, offset + 8);
        offset += 12;
        return (type, size, version);
    }

    internal static bool TryReadStruct(byte[] data, ref int offset, int endOffset,
        out uint type, out uint size)
    {
        type = 0;
        size = 0;
        if (offset + 12 > endOffset || offset + 12 > data.Length) return false;

        type = BitConverter.ToUInt32(data, offset);
        size = BitConverter.ToUInt32(data, offset + 4);
        offset += 12;
        return type == RW_STRUCT;
    }

    internal static bool TryReadChunk(byte[] data, ref int offset, int endOffset,
        uint expectedType, out uint size)
    {
        size = 0;
        if (offset + 12 > endOffset || offset + 12 > data.Length) return false;

        var type = BitConverter.ToUInt32(data, offset);
        size = BitConverter.ToUInt32(data, offset + 4);
        offset += 12;
        return type == expectedType;
    }

    /// <summary>
    ///     Reads a chunk header without requiring a specific type.
    ///     Returns false if there aren't enough bytes remaining.
    /// </summary>
    internal static bool TryReadAnyChunk(byte[] data, ref int offset, int endOffset,
        out uint type, out uint size)
    {
        type = 0;
        size = 0;
        if (offset < 0 || offset + 12 > endOffset || offset + 12 > data.Length) return false;

        type = BitConverter.ToUInt32(data, offset);
        size = BitConverter.ToUInt32(data, offset + 4);
        offset += 12;
        return true;
    }

    internal static string ReadNullTerminatedString(byte[] data, int offset, int maxLength)
    {
        var end = offset + maxLength;
        if (end > data.Length) end = data.Length;

        var len = 0;
        while (offset + len < end && data[offset + len] != 0)
            len++;

        return Encoding.ASCII.GetString(data, offset, len);
    }
}
