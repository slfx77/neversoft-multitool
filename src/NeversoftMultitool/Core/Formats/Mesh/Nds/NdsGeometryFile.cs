using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Vicarious Visions DS geometry file — the model format behind the three Tony
///     Hawk DS carts. The ARM9 loader names it <c>.\%08x.%08x.geometry.bin</c>
///     (Sk8land <c>FUN_02046440</c> composing through <c>FUN_020464ac</c>), so it
///     has no magic and no filename to key on and must be recognised structurally.
///
///     Layout: an 84-byte header of 21 little-endian u32s, then a prologue, then a
///     packed Nintendo GX display list.
///     <code>
///     [ 0] version = 4
///     [ 1] bounding-box X extent   (20.12)
///     [ 5] bounding-box Y extent   (20.12)
///     [ 9] bounding-box Z extent   (20.12)
///     [10..12] bounding-box minimum XYZ (20.12)
///     [14] joint count             (the ARM9 model ctor reads +0x38 and sizes its
///                                   joint arrays from it: count*0x28 + 2*count*0x10)
///     [15] 84 -- start of the prologue, constant in every shipped file
///     [16] sub-object count
///     [17] offset of the sub-object offset table
///     [18] end of the sub-object records
///     [19] prologue size + 8; the display list begins at 76 + this
///     [20] a further offset inside the record region
///     </code>
///
///     The display list runs from <c>76 + w19</c> to the first sub-object offset
///     (or to <c>w18</c> when there are no sub-objects) and consumes that span
///     EXACTLY — for 4,741 of 4,741 files across all three carts. Deriving the
///     start from the counts instead (<c>84 + joints*12 + subObjects*4</c>) is only
///     93% right, because joint records are not a fixed 12 bytes in every file.
///
///     The header's bounding box is an unusually good self-check: a decoder with a
///     wrong vertex format, fixed-point scale or matrix convention will not
///     reproduce it. Among rigid, self-contained models it matches to within 2% for
///     731/808 Sk8land, 793/808 Downhill Jam and 944/973 Proving Ground files; see
///     NdsGeometryTests for which classes are excluded and why.
/// </summary>
/// <summary>
///     One sub-object: a texture, named by ordinal in the model's bank, plus the
///     TEXIMAGE_PARAM sites in the display list that the runtime patches with it.
/// </summary>
public sealed record NdsSubObject(int TextureIndex, int[] PatchSites);

public sealed class NdsGeometryFile
{
    public const int HeaderSize = 84;
    private const int HeaderWords = 21;
    private const int Version = 4;

    private NdsGeometryFile(
        uint[] header, int displayListStart, int displayListEnd, int[] subObjectOffsets)
    {
        Header = header;
        DisplayListStart = displayListStart;
        DisplayListEnd = displayListEnd;
        SubObjectOffsets = subObjectOffsets;
    }

    public uint[] Header { get; }
    public int DisplayListStart { get; }
    public int DisplayListEnd { get; }
    public int[] SubObjectOffsets { get; }

    /// <summary>
    ///     The model's texture bindings, one per sub-object. Populated by
    ///     <see cref="TryParse" /> when the records are well formed.
    /// </summary>
    public IReadOnlyList<NdsSubObject> SubObjects { get; private set; } = [];

    public int JointCount => (int)Header[14];

    /// <summary>The model's own bounding-box extents, in world units.</summary>
    public Vector3 DeclaredExtent => new(Fixed(1), Fixed(5), Fixed(9));

    /// <summary>The model's own bounding-box minimum corner, in world units.</summary>
    public Vector3 DeclaredMinimum => new(Fixed(10), Fixed(11), Fixed(12));

    private float Fixed(int index)
    {
        return (int)Header[index] / 4096f;
    }

    public static bool IsGeometry(ReadOnlySpan<byte> data)
    {
        return TryParse(data, out _);
    }

    /// <summary>
    ///     Parses the header and locates the display list, rejecting anything whose
    ///     sections do not line up. Detection rests on this plus the caller walking
    ///     the list: the format carries no magic, so "the declared span is exactly a
    ///     display list" is the identifying property.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out NdsGeometryFile? file)
    {
        file = null;
        if (data.Length < HeaderSize)
            return false;

        var header = new uint[HeaderWords];
        for (var i = 0; i < HeaderWords; i++)
            header[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);

        if (header[0] != Version || header[15] != HeaderSize)
            return false;

        // The display list start is stored, not derived. w19 counts from 76 rather
        // than from the end of the header, which reads oddly but is exact across the
        // whole corpus where the count-derived formula is not.
        var start = 76L + header[19];
        var subObjectCount = (long)header[16];
        var tableAt = start - subObjectCount * 4;
        if (tableAt < HeaderSize || start > data.Length || subObjectCount > data.Length / 4)
            return false;

        var offsets = new int[subObjectCount];
        for (var i = 0; i < subObjectCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(tableAt + i * 4)..]);
            if (value > data.Length)
                return false;
            offsets[i] = (int)value;
        }

        var end = subObjectCount > 0 ? offsets[0] : (long)header[18];
        if (end > data.Length || end < start)
            return false;

        file = new NdsGeometryFile(header, (int)start, (int)end, offsets);
        file.SubObjects = ReadSubObjects(data, file);
        return true;
    }

    /// <summary>
    ///     Reads the sub-object records, each of which binds a texture to a set of
    ///     TEXIMAGE_PARAM sites inside the display list:
    ///     <code>
    ///     +0   u32 scratch       // zero on disk; the loader caches the index here
    ///     +4   u32 textureIndex  // ordinal in the model's texture bank
    ///     +8   u32 patchCount
    ///     +12  i32 rel[count]    // RECORD-relative offsets of the words to patch
    ///     </code>
    ///     This is the model's whole texture binding, and it is why TEXIMAGE_PARAM's
    ///     VRAM-address field is zero in every shipped file: the ARM9 loader
    ///     (Sk8land <c>FUN_02045edc</c>) resolves the index to a bank record and the
    ///     renderer writes that texture's address into each listed word.
    ///
    ///     Verified across the corpus: every one of the 12,362 listed offsets lands
    ///     exactly on a TEXIMAGE_PARAM parameter, and the record size is
    ///     <c>12 + count*4</c> without exception.
    /// </summary>
    private static NdsSubObject[] ReadSubObjects(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        var result = new List<NdsSubObject>(file.SubObjectOffsets.Length);
        foreach (var offset in file.SubObjectOffsets)
        {
            if (offset < 0 || offset + 12 > data.Length)
                return [];

            var index = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]);
            if (count > (data.Length - offset) / 4)
                return [];

            var sites = new int[count];
            for (var i = 0; i < count; i++)
            {
                var relative = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12 + i * 4)..]);
                var site = offset + relative;
                if (site < file.DisplayListStart || site + 4 > file.DisplayListEnd)
                    return [];
                sites[i] = site;
            }

            result.Add(new NdsSubObject((int)index, sites));
        }

        return [.. result];
    }

    /// <summary>
    ///     Parses and additionally requires the display-list span to consume exactly.
    ///     Use this where a false positive matters — the exact-consumption test is
    ///     what separates a real geometry file from 84 bytes that happen to start
    ///     with a 4.
    /// </summary>
    public static bool TryParseValidated(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out NdsGeometryFile? file)
    {
        return TryParse(data, out file) && NdsDisplayList.Consumes(data, file);
    }
}
