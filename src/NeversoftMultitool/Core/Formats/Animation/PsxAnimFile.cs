using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Per-game variant of the PSX character animation entry table.
/// </summary>
public enum PsxAnimLayoutVariant
{
    /// <summary>
    ///     Monolithic table used by THPS1 final, THPS2 final / Spider-Man / SM2:EE
    ///     and Apocalypse:
    ///     <c>+0x00 u32 numStreams; +0x04 + i*8: (u32 poolOffset, u32 frameCount)</c>;
    ///     pool starts at <c>+0x04 + numStreams*8</c>. The same on-disk layout
    ///     appears in every shipped game we have surveyed; the file's PSX
    ///     <c>Version</c> field (0x03 vs 0x04) is reported separately by
    ///     <see cref="Mesh.Psx.PsxMeshFile"/>.
    /// </summary>
    Monolithic,

    /// <summary>
    ///     Sparse table used by THPS2 PSX prototype (and possibly THPS1
    ///     prototype): only one entry header fits before the compressed pool.
    ///     <c>+0x00 u32 numStreams; +0x04 u32 poolByteSize; +0x08 (u32 frameCount, u32 poolOffset);
    ///     +0x10 pool data</c>. Subsequent entries are not yet recoverable from
    ///     the table alone — decomp research outstanding.
    /// </summary>
    PrototypeSparse,
}

/// <summary>
///     One animation slot's entry in the per-PSX-file animation table:
///     where its compressed stream lives in the pool, and how many frames it spans.
/// </summary>
public sealed record PsxAnimEntry(int PoolOffset, int FrameCount);

/// <summary>
///     Parsed PSX character animation header — the per-anim entry table and the
///     compressed stream pool. Sits in the post-mesh region of a <c>.psx</c> file.
/// </summary>
public sealed class PsxAnimFile
{
    public required PsxAnimLayoutVariant Layout { get; init; }

    /// <summary>
    ///     May be shorter than <see cref="NumStreamsDeclared" /> when only some
    ///     entries pass strict validation (trailing slack/garbage), or for
    ///     prototype layouts where only the first entry is recoverable.
    /// </summary>
    public required IReadOnlyList<PsxAnimEntry> Entries { get; init; }

    /// <summary><c>PoolOffset</c> values are relative to this slice.</summary>
    public required ReadOnlyMemory<byte> Pool { get; init; }

    public required int NumStreamsDeclared { get; init; }

    /// <summary>
    ///     Returns null when no recognizable layout is present (no animations,
    ///     or unknown layout).
    /// </summary>
    /// <param name="boneCount">Reserved for future prototype inline-pool walking.</param>
    /// <param name="meshBlockEnd">From <c>PsxMeshFile.GetMeshBlockEnd</c>.</param>
    public static PsxAnimFile? Parse(byte[] data, int boneCount, long meshBlockEnd)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (meshBlockEnd <= 0 || meshBlockEnd >= data.Length) return null;
        var hierBase = (int)meshBlockEnd;
        if (data.Length - hierBase < 16) return null;
        _ = boneCount; // reserved for future prototype inline pool walking

        var numStreams = ReadNumStreams(data, hierBase);
        if (numStreams is null) return null;

        // Try monolithic first. Accept only if at least MonolithicMinValidEntries
        // entries validate — both layouts can produce 1-2 "lucky" valid entries
        // by chance under the wrong interpretation, but a real monolithic table
        // has many sequential valid entries.
        var monolithic = TryParseMonolithic(data, hierBase, numStreams.Value);
        if (monolithic is not null && monolithic.Entries.Count >= MonolithicMinValidEntries)
            return monolithic;

        // Fall to prototype-sparse: numStreams + poolByteSize + (frameCount, poolOffset)
        // + pool data, with only one entry recoverable from the table.
        return TryParsePrototypeSparse(data, hierBase, numStreams.Value);
    }

    // Threshold separating "real monolithic table" from "prototype layout that
    // happens to validate 1-2 entries by chance". Verified against the corpus:
    // genuine monolithic files (carnage 40, blackcat 16, bigswat 10) all clear
    // 5; prototype-layout files (hawk2, cab, etc.) score 2 or fewer.
    private const int MonolithicMinValidEntries = 5;

    // ─── Monolithic layout (Spider-Man, SM2:EE, Apocalypse, some THPS finals) ──

    private static PsxAnimFile? TryParseMonolithic(byte[] data, int hierBase, int numStreams)
    {
        var entriesStart = hierBase + 4;
        var poolStart = entriesStart + numStreams * 8;
        if (poolStart > data.Length) return null;
        var poolBytes = data.Length - poolStart;
        var entries = new List<PsxAnimEntry>(numStreams);

        for (var i = 0; i < numStreams; i++)
        {
            var off = entriesStart + i * 8;
            var poolOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            var frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));

            if (poolOff >= 0 && poolOff < poolBytes && frames is >= 1 and <= 1024)
                entries.Add(new PsxAnimEntry(poolOff, frames));
        }

        if (entries.Count == 0) return null;

        return new PsxAnimFile
        {
            Layout = PsxAnimLayoutVariant.Monolithic,
            Entries = entries,
            Pool = data.AsMemory(poolStart),
            NumStreamsDeclared = numStreams,
        };
    }

    // ─── Prototype sparse layout (THPS2 PSX prototype, possibly THPS1 PSX prototype) ──

    private static PsxAnimFile? TryParsePrototypeSparse(byte[] data, int hierBase, int numStreams)
    {
        // Layout (per tools/diagnostics/psx-anim-format.md):
        //   +0x00 u32 numStreams
        //   +0x04 u32 poolByteSize
        //   +0x08 (u32 frameCount, u32 poolOffset) — first entry only
        //   +0x10 pool data begins
        if (hierBase + 0x10 > data.Length) return null;

        var poolStart = hierBase + 0x10;
        var poolBytes = data.Length - poolStart;
        if (poolBytes <= 0) return null;

        var frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hierBase + 0x08));
        var poolOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hierBase + 0x0C));

        if (poolOff < 0 || poolOff >= poolBytes) return null;
        if (frames is < 1 or > 1024) return null;

        return new PsxAnimFile
        {
            Layout = PsxAnimLayoutVariant.PrototypeSparse,
            Entries = [new PsxAnimEntry(poolOff, frames)],
            Pool = data.AsMemory(poolStart),
            NumStreamsDeclared = numStreams,
        };
    }

    private static int? ReadNumStreams(byte[] data, int hierBase)
    {
        if (hierBase + 4 > data.Length) return null;
        var numStreams = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hierBase));
        return numStreams is 0 or > 256 ? null : (int)numStreams;
    }
}
