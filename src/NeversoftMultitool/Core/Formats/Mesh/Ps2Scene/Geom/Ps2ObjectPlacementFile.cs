using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Legacy experimental parser for byte slices once attributed to THAW PS2 <c>.rnb</c> entries
///     (type hash 0x91E1028D). The 2026-08-11 archive audit proved those slices began at unresolved,
///     pre-fix PAK offsets inside preceding entries; canonical header-relative <c>.rnb</c> payloads
///     reject this grammar. Production worldzone instancing uses QB NodeArrays through
///     <c>Ps2WorldzoneQbObjectResolver</c> and does not call this parser. Retained only as an explicit
///     fail-static record of the superseded grammar.
/// </summary>
public static class Ps2ObjectPlacementFile
{
    private const int PreHeaderSize = 0xC;
    private const int BlockHeaderSize = 8;
    private const int ItemStride = 0x58;
    private const int ItemTailStride = 0x1C;
    private const int BlockStrideFactor = ItemStride + ItemTailStride; // 0x74
    private const int MaxCountPerBlock = 100;

    /// <summary>
    ///     Test a byte slice against the superseded pre-fix block-chain grammar. Canonical
    ///     <c>.rnb</c> payloads are expected to return false with a populated
    ///     <paramref name="skipReason" />.
    /// </summary>
    public static bool TryParse(byte[] data, out ObjectPlacementFile? result, out string skipReason)
    {
        result = null;
        skipReason = string.Empty;

        if (data is null || data.Length < PreHeaderSize + BlockHeaderSize)
        {
            skipReason = "file too small for a block chain";
            return false;
        }

        var blocks = new List<PlacementBlock>();
        var offset = PreHeaderSize;

        while (offset + BlockHeaderSize <= data.Length)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            var flag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));

            if (count == 0)
            {
                // Normal termination once we've seen at least one block.
                if (blocks.Count > 0)
                    break;
                skipReason = $"first block count is 0 at +0x{offset:X}";
                return false;
            }

            if (count > MaxCountPerBlock)
            {
                if (blocks.Count > 0)
                    break;
                skipReason = $"first block count {count} exceeds sanity limit at +0x{offset:X}";
                return false;
            }

            var blockSize = BlockHeaderSize + (int)count * BlockStrideFactor;
            if (offset + blockSize > data.Length)
            {
                if (blocks.Count > 0)
                    break;
                skipReason = $"first block at +0x{offset:X} with count {count} would exit file";
                return false;
            }

            var items = new PlacementItem[count];
            for (var i = 0; i < count; i++)
            {
                var itemOffset = offset + BlockHeaderSize + i * ItemStride;
                items[i] = ReadItem(data, itemOffset);
            }

            blocks.Add(new PlacementBlock
            {
                Offset = offset,
                Flag = flag,
                Items = items
            });

            offset += blockSize;
        }

        if (blocks.Count == 0)
        {
            skipReason = "no blocks parsed";
            return false;
        }

        result = new ObjectPlacementFile
        {
            Blocks = blocks
        };
        return true;
    }

    private static PlacementItem ReadItem(byte[] data, int offset)
    {
        var span = data.AsSpan(offset);

        // Superseded mis-slice interpretation of an interleaved AABB layout:
        //   +0x20 min_x, +0x24 min_y, +0x28 max_z
        //   +0x2C max_x, +0x30 max_y, +0x34 min_z
        var minX = BinaryPrimitives.ReadSingleLittleEndian(span[0x20..]);
        var minY = BinaryPrimitives.ReadSingleLittleEndian(span[0x24..]);
        var maxZ = BinaryPrimitives.ReadSingleLittleEndian(span[0x28..]);
        var maxX = BinaryPrimitives.ReadSingleLittleEndian(span[0x2C..]);
        var maxY = BinaryPrimitives.ReadSingleLittleEndian(span[0x30..]);
        var minZ = BinaryPrimitives.ReadSingleLittleEndian(span[0x34..]);

        return new PlacementItem
        {
            Offset = offset,
            BboxMin = new Vector3(minX, minY, minZ),
            BboxMax = new Vector3(maxX, maxY, maxZ),
            Field_40 = BinaryPrimitives.ReadUInt32LittleEndian(span[0x40..]),
            Field_44 = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]),
            Field_4C = BinaryPrimitives.ReadUInt32LittleEndian(span[0x4C..])
        };
    }

    public sealed record ObjectPlacementFile
    {
        public required IReadOnlyList<PlacementBlock> Blocks { get; init; }
    }

    public sealed record PlacementBlock
    {
        public required int Offset { get; init; }
        public required uint Flag { get; init; }
        public required IReadOnlyList<PlacementItem> Items { get; init; }

        /// <summary>
        ///     Center derived under the legacy item-0 AABB interpretation. Empty when the block has no items.
        /// </summary>
        public Vector3 AabbCenter => Items.Count > 0
            ? (Items[0].BboxMin + Items[0].BboxMax) * 0.5f
            : Vector3.Zero;
    }

    public sealed record PlacementItem
    {
        public required int Offset { get; init; }

        /// <summary>Legacy interpretation of the item-0 minimum corner.</summary>
        public required Vector3 BboxMin { get; init; }

        /// <summary>Legacy interpretation of the item-0 maximum corner.</summary>
        public required Vector3 BboxMax { get; init; }

        /// <summary>
        ///     Opaque word at item+0x40 in the superseded byte-slice grammar.
        /// </summary>
#pragma warning disable CA1707 // Unknown binary fields retain their hexadecimal byte offsets in their names.
        public required uint Field_40 { get; init; }

        /// <summary>
        ///     Legacy interpretation: item 0 pointed into the preceding mis-sliced MDL bytes and
        ///     item 1 was treated as a class/instance hash.
        /// </summary>
        public required uint Field_44 { get; init; }

        /// <summary>Legacy class/instance-hash interpretation.</summary>
        public required uint Field_4C { get; init; }
#pragma warning restore CA1707
    }
}
