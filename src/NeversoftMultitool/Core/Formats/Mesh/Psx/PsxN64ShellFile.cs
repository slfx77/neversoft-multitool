using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Reads the PSX "shell" carved out of an N64 ROM
///     (<c>models/NNN/NNN_&lt;name&gt;.psx.n64</c>) — the Edge of Reality ports keep
///     Neversoft's PS1 model container for the object table, hierarchy, names
///     and animation, while the actual render geometry moves to the N64-native
///     render bank (<c>group2/</c>). Two deviations from a PS1 file, both
///     established 2026-08-06 by measuring the carved corpus:
///     <list type="number">
///         <item>
///             The file is <b>big-endian</b> (N64), field for field. The PS1
///             header grammar is otherwise unchanged, so it is read by the same
///             reader with the byte order declared — see
///             <see cref="PsxMeshFile.ParseHeaderOnly(byte[], bool, bool)" />.
///         </item>
///         <item>
///             The trailing texture-hash array is <b>stripped</b>: the carve
///             cuts the file at a 4-byte boundary at or after the mesh-name
///             hashes, so a faithful reader runs off the end. Usually the count
///             word survives and only its values are gone (417 of 450 shells);
///             in 33 the file ends at the name hashes and the count is gone
///             too. Zero-padding the tail lets the stock header reader consume
///             either shape unchanged. The count is the number of distinct
///             textures and is <i>not</i> tied to the mesh count. The per-mesh
///             <c>meshTopPointers</c> array is stripped too, but that is
///             harmless — the reader seeks to <c>metaTop</c> immediately after
///             it, and the pointers are only consulted for v3 Apocalypse
///             probing and geometry, neither of which applies here.
///         </item>
///     </list>
///     <para>
///         This used to reverse every 4-byte word and hand the result to the
///         little-endian reader. That restores u32 fields but EXCHANGES the two
///         u16s packed inside a word, which is why it needed a correction
///         re-reading each object's mesh index from +0x14 — the field really
///         lives at +0x16, and the swap displaced it. Declaring the byte order
///         removes both the swap and the correction;
///         <c>PsxN64ShellEndianTests</c> pins that the two readings agree
///         everywhere else, across all four ROMs.
///     </para>
///     Still deliberately a thin adapter rather than a new parse path: the
///     geometry reader is never entered, which matters because it would
///     dereference the stripped pointer array.
/// </summary>
public static class PsxN64ShellFile
{
    private const int HeaderFixedSize = 12;
    private const int ObjectRecordSize = 36;
    private const int MaxTaggedChunks = 16;

    /// <summary>
    ///     Bounds the zero-padding allocation for a garbage mesh count. Not a
    ///     format limit — real shells run to 837 objects (THPS2 model 051).
    /// </summary>
    private const uint MaxMeshCount = 65535;

    /// <summary>
    ///     Bounds the zero-padding allocation for a garbage texture-hash count.
    ///     Also not a format limit — the largest real count measured across the
    ///     four ROMs is 1,217, needing 4,864 bytes of padding.
    /// </summary>
    private const uint MaxTextureHashCount = 65535;

    /// <summary>
    ///     True when the buffer looks like a byteswapped PSX container — the
    ///     big-endian reading of the version/magic pair the PS1 loader expects
    ///     little-endian. Mirrors <c>N64AssetCarver.IsPsxHead</c>.
    /// </summary>
    public static bool IsN64Shell(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderFixedSize)
            return false;
        var head = BinaryPrimitives.ReadUInt32BigEndian(data);
        return head is 0x0002_0003 or 0x0002_0004 or 0x0002_0006;
    }

    /// <summary>
    ///     Parses the shell into the ordinary <see cref="PsxMeshFile" /> shape
    ///     (objects, hierarchy, mesh name hashes, super/scale classification)
    ///     with an empty mesh list. Returns null when the buffer is not a shell
    ///     or its counts are implausible.
    /// </summary>
    public static PsxMeshFile? Parse(byte[] data)
    {
        if (!IsN64Shell(data))
            return null;

        if (!TryMeasureTail(data, out var padding))
            return null;

        var buffer = data;
        if (padding > 0)
        {
            buffer = new byte[data.Length + padding];
            data.CopyTo(buffer, 0);
        }

        try
        {
            // hasGeometry: false — the shell keeps no mesh blocks, so the
            // Apocalypse-v3 probe has nothing real to read. Apocalypse never
            // shipped on N64 either; the four carts are THPS1/2/3 and
            // Spider-Man.
            return PsxMeshFile.ParseHeaderOnly(buffer, bigEndian: true, hasGeometry: false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Works out how many zero bytes the stripped texture-hash values need.
    ///     The tagged chain, mesh-name hashes, and texture-count word remain
    ///     physical in a compact shell; only the texture-hash values may be
    ///     absent. The surviving texture count equals the mesh count. Requiring
    ///     those structural fields before padding prevents a short buffer from
    ///     acquiring invented name hashes or an invented zero texture count.
    ///     Counts are validated against the buffer rather than an invented
    ///     ceiling: the object table must physically fit, which is what
    ///     separates a real 837-object model from garbage.
    /// </summary>
    private static bool TryMeasureTail(byte[] data, out int padding)
    {
        padding = 0;
        if (data.Length < HeaderFixedSize)
            return false;

        var metaTopValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (metaTopValue > int.MaxValue)
            return false;
        var metaTop = (int)metaTopValue;

        var objectCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        if (objectCount == 0)
            return false;

        // 36 bytes per object plus the mesh-count word must be present.
        var meshCountOffset = (long)HeaderFixedSize + (long)objectCount * ObjectRecordSize;
        if (meshCountOffset + 4 > data.Length)
            return false;

        var meshCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)meshCountOffset));
        if (meshCount is 0 or > MaxMeshCount)
            return false;

        var metadataMinimum = meshCountOffset + sizeof(uint);
        if (metaTop < metadataMinimum || metaTop > data.Length - sizeof(uint))
            return false;

        var cursor = metaTop;
        var taggedChunkCount = 0;
        while (true)
        {
            if (cursor > data.Length - sizeof(uint))
                return false;

            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
            cursor += sizeof(uint);
            if (tag == uint.MaxValue)
                break;

            if (taggedChunkCount++ >= MaxTaggedChunks
                || cursor > data.Length - sizeof(uint))
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
            cursor += sizeof(uint);
            var chunkEnd = (long)cursor + length;
            if (chunkEnd > data.Length)
                return false;
            cursor = (int)chunkEnd;
        }

        var meshNameBytes = (long)meshCount * sizeof(uint);
        var textureCountOffset = (long)cursor + meshNameBytes;

        // The mesh-name hashes always survive the carve — none of the 450
        // non-empty shells across the four ROMs is truncated inside them — so a
        // buffer too short to hold them is not a shell we understand.
        if (textureCountOffset > data.Length)
            return false;

        if (textureCountOffset + sizeof(uint) > data.Length)
        {
            // The count word itself was cut. Every carved shell's trailing
            // region is 4-byte aligned, so a shell that gets here ends exactly
            // at the name hashes (33 of 450) rather than part-way through the
            // word. Refuse the part-way case instead of assembling a count out
            // of real bytes plus padding, then let the zero padding supply the
            // zero count the reader consumed before this measurement existed.
            if (textureCountOffset != data.Length)
                return false;

            padding = sizeof(uint);
            return true;
        }

        // The count is the number of DISTINCT TEXTURES, which the format does
        // not tie to the mesh count: across 2,620 parseable PS1 files — the
        // container this one is a byteswapped copy of — the two agree in only
        // 210, and are unequal in 2,410. Requiring equality rejected 54 real
        // shells outright. Read the count the file actually stores.
        var textureCount = BinaryPrimitives.ReadUInt32BigEndian(
            data.AsSpan((int)textureCountOffset));
        if (textureCount > MaxTextureHashCount)
            return false;

        var textureValuesOffset = textureCountOffset + sizeof(uint);
        var physicalTextureBytes = data.Length - textureValuesOffset;
        var missingTextureBytes = (long)textureCount * sizeof(uint) - physicalTextureBytes;
        padding = missingTextureBytes > 0 ? (int)missingTextureBytes : 0;
        return true;
    }
}
