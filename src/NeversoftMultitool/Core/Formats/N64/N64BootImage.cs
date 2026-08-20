using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.N64;

/// <summary>
///     A carved <c>boot.bin</c> plus the RAM address it loads at, so the many
///     absolute pointers inside it can be followed.
///     <para>
///         The boot package decompresses to one CONTIGUOUS load image — every
///         ERZ block in it yields exactly 0x10000 bytes except the last — so
///         <c>N64RomArchive</c>'s concatenation already is the runtime layout
///         and a single base is all that was ever missing.
///     </para>
///     <para>
///         The base is DERIVED from the image, not tabled per game. The
///         libultra light-rig setup display list carries a RAM pointer to a
///         body whose file offset <see cref="Mesh.N64.N64LightRig" /> already
///         locates by locality, and the difference is the load base. That keeps
///         this working on any build carrying the rig instead of only on the
///         four audited ROMs, and it fails closed — an image with no rig, or an
///         ambiguous one, yields no base rather than a guess.
///     </para>
///     <para>
///         The derived values agree with both independent measurements on
///         record: the prologue vote in
///         <c>tools/reverse-engineering/n64/n64_disasm.py</c>, and the
///         dispatch-table arithmetic the N64 audio tests already pin
///         (<c>base + handlerOffset == dispatchEntry</c>). THPS1 0x80016990,
///         THPS2/THPS3 0x80016B20, Spider-Man 0x80016AE0.
///     </para>
/// </summary>
public sealed class N64BootImage
{
    /// <summary>
    ///     N64 KSEG0/KSEG1 addresses differ only in bit 29; masking it off
    ///     makes a cached and an uncached pointer to the same word resolve
    ///     identically.
    /// </summary>
    private const uint SegmentMask = 0xDFFF_FFFF;

    private readonly byte[] _data;

    private N64BootImage(byte[] data, uint loadBase)
    {
        _data = data;
        LoadBase = loadBase;
    }

    /// <summary>The RAM address the first byte of the image occupies.</summary>
    public uint LoadBase { get; }

    public ReadOnlySpan<byte> Data => _data;

    public int Length => _data.Length;

    /// <summary>
    ///     Opens a carved <c>boot.bin</c>, deriving its load base. Returns null
    ///     when the base cannot be established, which leaves every caller
    ///     unable to follow a pointer rather than following it to the wrong
    ///     place.
    /// </summary>
    public static N64BootImage? TryOpen(byte[] boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        var body = Mesh.N64.N64LightRig.TryFindBodyOffset(boot, out var ambientPointer);
        if (body < 0)
            return null;

        // The ambient body is the first structure the display list points at,
        // so its RAM address minus its file offset is where the image starts.
        var maskedPointer = ambientPointer & SegmentMask;
        if (maskedPointer < (uint)body)
            return null;

        var loadBase = maskedPointer - (uint)body;
        return new N64BootImage(boot, loadBase);
    }

    /// <summary>
    ///     Translates a RAM address to an offset inside the image. False when
    ///     the address lands outside it — which is the normal answer for a
    ///     pointer into RDRAM the boot package does not contain.
    /// </summary>
    public bool TryGetOffset(uint virtualAddress, out int offset)
    {
        offset = 0;
        var masked = virtualAddress & SegmentMask;
        var maskedBase = LoadBase & SegmentMask;
        if (masked < maskedBase)
            return false;

        var candidate = masked - maskedBase;
        if (candidate >= (uint)_data.Length)
            return false;

        offset = (int)candidate;
        return true;
    }

    /// <summary>
    ///     Reads the NUL-terminated ASCII string at a RAM address. Null when
    ///     the address is outside the image, the string is unterminated within
    ///     <paramref name="maxLength" />, or any byte is not printable ASCII —
    ///     a pointer array is only worth following where it really holds names.
    /// </summary>
    public string? TryReadCString(uint virtualAddress, int maxLength = 64)
    {
        if (!TryGetOffset(virtualAddress, out var offset))
            return null;

        var limit = Math.Min(offset + maxLength, _data.Length);
        for (var i = offset; i < limit; i++)
        {
            if (_data[i] == 0)
                return i == offset ? null : Encoding.ASCII.GetString(_data, offset, i - offset);
            if (_data[i] is < 0x20 or > 0x7E)
                return null;
        }

        return null;
    }

    public uint ReadUInt32(int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset));
    }
}
