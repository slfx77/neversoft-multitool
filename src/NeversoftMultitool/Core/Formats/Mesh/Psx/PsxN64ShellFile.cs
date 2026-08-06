using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Reads the PSX "shell" carved out of an N64 ROM
///     (<c>models/NNN/geometry.psx.n64</c>) — the Edge of Reality ports keep
///     Neversoft's PS1 model container for the object table, hierarchy, names
///     and animation, while the actual render geometry moves to the N64-native
///     render bank (<c>group2/</c>). Two deviations from a PS1 file, both
///     established 2026-08-06 by measuring the carved corpus:
///     <list type="number">
///         <item>
///             The whole file is <b>u32-byteswapped</b> (N64 is big-endian).
///             Reversing each 4-byte word restores the PS1 byte layout exactly,
///             because every field in the header region is a u32, a u16 pair,
///             or 4 bytes — a per-word reverse is an involution over all three.
///         </item>
///         <item>
///             The trailing texture-hash array is <b>stripped</b>: the count
///             survives (equal to the mesh count) but the file ends there, so a
///             faithful reader runs off the end. Zero-padding the tail lets the
///             stock header reader consume it unchanged. The per-mesh
///             <c>meshTopPointers</c> array is stripped too, but that is
///             harmless — the reader seeks to <c>metaTop</c> immediately after
///             it, and the pointers are only consulted for v3 Apocalypse
///             probing and geometry, neither of which applies here.
///         </item>
///     </list>
///     Deliberately a thin adapter over <see cref="PsxMeshFile.ParseHeaderOnly(byte[])" />
///     rather than a new parse path: the PS1 reader stays untouched (no N64
///     special cases leaking into PS1 semantics), and the geometry reader is
///     never entered, which matters because it would dereference the stripped
///     pointer array.
/// </summary>
public static class PsxN64ShellFile
{
    private const int HeaderFixedSize = 12;
    private const int ObjectRecordSize = 36;

    /// <summary>
    ///     Bounds the zero-padding allocation for a garbage mesh count. Not a
    ///     format limit — real shells run to 837 objects (THPS2 model 051).
    /// </summary>
    private const uint MaxMeshCount = 65535;

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

        var swapped = SwapWords(data);
        if (!TryMeasureTail(swapped, out var padding))
            return null;

        if (padding > 0)
        {
            var padded = new byte[swapped.Length + padding];
            swapped.CopyTo(padded, 0);
            swapped = padded;
        }

        PsxMeshFile? shell;
        try
        {
            shell = PsxMeshFile.ParseHeaderOnly(swapped);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return null;
        }

        if (shell != null)
            PatchMeshIndices(shell, swapped);
        return shell;
    }

    /// <summary>
    ///     Restores each object's mesh index. A PS1 object keeps it in the HIGH
    ///     half of the word at +0x14, but a carved shell keeps it in the LOW
    ///     half, so the shared reader always sees zero — which silently turned
    ///     every model into "object i places mesh i". That is right for about
    ///     two thirds of the corpus and wrong for the rest: characters permute
    ///     their parts (hawk: 0,1,3,2,4,6,5...), and levels can carry more
    ///     meshes than objects (a THPS1 Downhill Jam shell has 642 objects for
    ///     883 meshes, so 241 are simply never placed).
    /// </summary>
    private static void PatchMeshIndices(PsxMeshFile shell, byte[] swapped)
    {
        const int meshIndexOffset = 0x14;
        for (var i = 0; i < shell.Objects.Count; i++)
        {
            var position = HeaderFixedSize + i * ObjectRecordSize + meshIndexOffset;
            if (position + 2 > swapped.Length)
                return;
            shell.Objects[i].MeshIndex = BinaryPrimitives.ReadUInt16LittleEndian(swapped.AsSpan(position));
        }
    }

    /// <summary>
    ///     Reverses each 4-byte word. Any trailing bytes that do not fill a
    ///     word are copied through — carved slices are word-aligned, but a
    ///     ragged tail must not throw.
    /// </summary>
    private static byte[] SwapWords(byte[] data)
    {
        var output = new byte[data.Length];
        var wordBytes = data.Length & ~3;
        for (var i = 0; i < wordBytes; i += 4)
        {
            output[i] = data[i + 3];
            output[i + 1] = data[i + 2];
            output[i + 2] = data[i + 1];
            output[i + 3] = data[i];
        }

        for (var i = wordBytes; i < data.Length; i++)
            output[i] = data[i];

        return output;
    }

    /// <summary>
    ///     Works out how many zero bytes the stripped texture-hash array needs.
    ///     The count stored in the file equals the mesh count, so padding
    ///     <c>4 × meshCount</c> (plus the count word and a little slack for the
    ///     0–4 byte carve alignment) is sufficient and bounded — no reliance on
    ///     a length field read from truncated data. Counts are validated
    ///     against the buffer rather than an invented ceiling: the object table
    ///     must physically fit, which is what separates a real 837-object model
    ///     from garbage.
    /// </summary>
    private static bool TryMeasureTail(byte[] swapped, out int padding)
    {
        padding = 0;
        if (swapped.Length < HeaderFixedSize)
            return false;

        var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(swapped.AsSpan(8));
        if (objectCount == 0)
            return false;

        // 36 bytes per object plus the mesh-count word must be present.
        var meshCountOffset = (long)HeaderFixedSize + (long)objectCount * ObjectRecordSize;
        if (meshCountOffset + 4 > swapped.Length)
            return false;

        var meshCount = BinaryPrimitives.ReadUInt32LittleEndian(swapped.AsSpan((int)meshCountOffset));
        if (meshCount is 0 or > MaxMeshCount)
            return false;

        padding = (int)(meshCount * 4) + 8;
        return true;
    }
}
