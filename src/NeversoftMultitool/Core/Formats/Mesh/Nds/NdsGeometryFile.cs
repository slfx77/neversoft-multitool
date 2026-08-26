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
///     [10..12] bounding-box CENTRE XYZ  (20.12)
///     [14] joint count             (the ARM9 model ctor reads +0x38 and sizes its
///                                   joint arrays from it: count*0x28 + 2*count*0x10)
///     [15] 84 -- joint offset table position, constant in every shipped file
///     [16] sub-object count
///     [17] offset of the sub-object offset table
///     [18] offset of the POLYGON_ATTR cull-patch section (also the record end)
///     [19] prologue size + 8; the display list begins at 76 + this
///     [20] display-list byte length -- the runtime's DMA count for the whole list
///     </code>
///
///     The display list runs from <c>76 + w19</c> to the first sub-object offset
///     (or to <c>w18</c> when there are no sub-objects) and consumes that span
///     EXACTLY — for 4,741 of 4,741 files across all three carts. Deriving the
///     start from the counts instead (<c>84 + joints*12 + subObjects*4</c>) is only
///     93% right, because joint records are not a fixed 12 bytes in every file.
///     The draw routine (Sk8land ITCM <c>0x01FFBBF0</c>) confirms both stored
///     fields: it DMAs <c>w20</c> bytes from <c>geom + 0x4C + w19</c> straight into
///     the GX FIFO.
///
///     The header's bounding box is an unusually good self-check: a decoder with a
///     wrong vertex format, fixed-point scale or matrix convention will not
///     reproduce it. Among rigid, self-contained models it matches to within 2% for
///     731/808 Sk8land, 793/808 Downhill Jam and 944/973 Proving Ground files; see
///     NdsGeometryTests for which classes are excluded and why. Words 10..12 are the
///     box CENTRE — see <see cref="DeclaredCentre" />, which corrects an earlier
///     "minimum corner" reading that was invisible because only the extents were
///     ever checked — so the oracle constrains WHERE the model sits as well as how
///     big it is, and a missing or misapplied outer transform fails it.
/// </summary>
/// <summary>
///     One sub-object: a texture, named by ordinal in the model's bank, plus the
///     TEXIMAGE_PARAM sites in the display list that the runtime patches with it.
/// </summary>
public sealed record NdsSubObject(int TextureIndex, int[] PatchSites);

/// <summary>
///     One joint: which animation channel kinds drive it, and the display-list
///     matrix operands the runtime scatters each frame's pose into.
///
///     The engine has no skeleton at runtime — no parent table, no matrix slots per
///     joint. The hierarchy is compiled into the display list as
///     PUSH / MULT_4x3 / POP nesting, the shipped operand values ARE the bind pose,
///     and animating means OVERWRITING those operands in RAM before the list is
///     DMA'd (Sk8land ITCM <c>0x01FFDA6C</c>, the joint-record scatter). Per flags
///     bit, a target takes: rotation — 9 words (a 4.12 3x3) at target+0;
///     translation — 3 words at target+0x24 when the joint also rotates (row 3 of
///     the same MULT_4x3), else at target+0; scale — 3 words after the rotation
///     block (the following MTX_SCALE's operands).
///
///     Corpus-verified: every target of every Sk8land joint record resolves to a
///     matrix operand under these rules — flags 1/3/7 onto MULT_4x3/MULT_3x3
///     rotation blocks (1,655), flags 2/6 onto MTX_TRANS operands (184) or MULT_4x3
///     row 3 (1,033).
/// </summary>
public sealed record NdsJointRecord(int Flags, int[] Targets)
{
    public bool HasRotation => (Flags & 1) != 0;
    public bool HasTranslation => (Flags & 2) != 0;
    public bool HasScale => (Flags & 4) != 0;
}

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

    /// <summary>
    ///     The model's joints, one per header-declared joint, in joint order. A
    ///     joint whose offset-table entry is zero has no record and appears here
    ///     with flags 0 and no targets. Empty when the records are malformed.
    /// </summary>
    public IReadOnlyList<NdsJointRecord> Joints { get; private set; } = [];

    public int JointCount => (int)Header[14];

    /// <summary>The model's own bounding-box extents, in world units.</summary>
    public Vector3 DeclaredExtent => new(Fixed(1), Fixed(5), Fixed(9));

    /// <summary>
    ///     The model's own bounding-box CENTRE, in world units — not its minimum
    ///     corner. Measured over every rigid self-contained model whose decoded size
    ///     reproduces <see cref="DeclaredExtent" />: the centre reading holds for
    ///     2,525 of 2,525 across the three carts, while the minimum reading holds
    ///     only for the 97 whose box is flat on every axis it would differ on (there,
    ///     centre and minimum are the same number).
    /// </summary>
    public Vector3 DeclaredCentre => new(Fixed(10), Fixed(11), Fixed(12));

    /// <summary>
    ///     True when the header carries the authoring tool's boilerplate box instead
    ///     of a measured one: words 2/3/4/6/7/8 — the off-diagonal of the 3×4 block
    ///     the box occupies, zero in a real model — hold a fixed non-zero pattern,
    ///     and the "extents" that go with it are nonsense (one axis around 65,000
    ///     units). Every such file in all three carts decodes to zero vertices
    ///     (102 Sk8land, 309 Downhill Jam, 440 Proving Ground; 851/851), so this is
    ///     what an authored-empty model looks like rather than a second box layout.
    ///     Its declared box must not be fed to a bounds oracle or a level's extent.
    /// </summary>
    public bool HasBoilerplateBox =>
        (Header[2] | Header[3] | Header[4] | Header[6] | Header[7] | Header[8]) != 0;

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
        file.Joints = ReadJoints(data, file);
        return true;
    }

    /// <summary>
    ///     Reads the joint records the offset table at 84 points to:
    ///     <code>
    ///     84: u32 recordOffset[jointCount]   // file-relative; 0 = no record
    ///     record: { u16 targetCount, u16 flags, i32 targetRel[targetCount] }
    ///     </code>
    ///     Target offsets are RECORD-relative (the ARM9 scatter at ITCM 0x01FFDA6C
    ///     reads the array at record+4 and adds the record base); they are stored
    ///     here resolved to absolute file offsets. Variable targetCount is what made
    ///     the prologue stride look irregular: a channel scattered to several
    ///     display-list sites simply lists them all.
    /// </summary>
    private static NdsJointRecord[] ReadJoints(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        var joints = file.JointCount;
        if (joints == 0)
            return [];
        if (HeaderSize + joints * 4L > file.DisplayListStart)
            return [];

        var result = new NdsJointRecord[joints];
        for (var j = 0; j < joints; j++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(HeaderSize + j * 4)..]);
            if (offset == 0)
            {
                result[j] = new NdsJointRecord(0, []);
                continue;
            }

            if (offset < 0 || offset + 4 > data.Length)
                return [];

            int count = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
            if (offset + 4 + count * 4 > data.Length)
                return [];

            var targets = new int[count];
            for (var i = 0; i < count; i++)
            {
                var relative = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4 + i * 4)..]);
                var target = offset + relative;
                if (target < file.DisplayListStart || target + 4 > file.DisplayListEnd)
                    return [];
                targets[i] = target;
            }

            result[j] = new NdsJointRecord(flags, targets);
        }

        return result;
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
