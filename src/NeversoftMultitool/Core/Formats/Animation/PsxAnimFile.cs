using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parsed PSX character animation header — the per-anim entry table and the
///     frame data pool. Sits inside a hier/anim chunk in the PSX file: tag
///     <c>0x2A</c> for v1 (direct uncompressed SMatrix per bone per frame) or
///     <c>0x2C</c> for v2 (DecompressStream-compressed Euler+translation
///     streams). The on-disk entry table layout is identical between the two
///     variants; only the payload encoding differs.
/// </summary>
public sealed class PsxAnimFile
{
    // Threshold separating "real monolithic table" from "prototype layout that
    // happens to validate 1-2 entries by chance". Verified against the corpus:
    // genuine monolithic files (carnage 40, blackcat 16, bigswat 10) all clear
    // 5; prototype-layout files (hawk2, cab, etc.) score 2 or fewer.
    private const int MonolithicMinValidEntries = 5;

    /// <summary>
    ///     PSY-Q fixed-point divisor for rotation-matrix cells in the v1
    ///     direct-matrix payload: 4096 = 1.0.
    /// </summary>
    public const float DirectMatrixFixedPointDivisor = 4096f;

    public required PsxAnimLayoutVariant Layout { get; init; }

    public PsxAnimationFormatRevision FormatRevision { get; init; } =
        PsxAnimationFormatRevision.Unknown;

    /// <summary>
    ///     May be shorter than <see cref="NumStreamsDeclared" /> when only some
    ///     entries pass strict validation (trailing slack/garbage), or for
    ///     prototype layouts where only the first entry is recoverable.
    /// </summary>
    public required IReadOnlyList<PsxAnimEntry> Entries { get; init; }

    /// <summary><c>PoolOffset</c> values are relative to this slice.</summary>
    public required ReadOnlyMemory<byte> Pool { get; init; }

    public required int NumStreamsDeclared { get; init; }

    public uint ChunkTag { get; init; }

    public PsxCharacterRuntimeRevision MinimumRuntimeRevision { get; init; } =
        PsxCharacterRuntimeRevision.Unknown;

    public bool RequiresExtendedAnimationSlotIndex =>
        MinimumRuntimeRevision == PsxCharacterRuntimeRevision.ExtendedAnimSlots;

    /// <summary>
    ///     <c>true</c> when frame data is raw <c>numBones × 24-byte</c>
    ///     <c>SMatrix</c> blocks; <c>false</c> when frame data is
    ///     <c>DecompressStream</c>-compressed per-channel streams.
    /// </summary>
    public bool IsDirectMatrix => Layout == PsxAnimLayoutVariant.DirectMatrix;

    /// <summary>
    ///     Convenience overload that walks the PSX chunk chain to discover the
    ///     anim chunk tag and data offset, then forwards to
    ///     <see cref="Parse(byte[], int, int, uint)" />. Returns <c>null</c>
    ///     when the file has no hier/anim chunk (texture-only library) or when
    ///     the layout is unrecognised.
    /// </summary>
    public static PsxAnimFile? Parse(byte[] data, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!PsxMeshFile.TryGetAnimChunkTag(data, out var tag, out var chunkOffset))
            return null;
        return Parse(data, boneCount, chunkOffset, tag);
    }

    /// <summary>
    ///     Returns null when no recognizable layout is present (no animations,
    ///     or unknown layout).
    /// </summary>
    /// <param name="data">Entire PSX file bytes.</param>
    /// <param name="boneCount">Reserved for future prototype inline-pool walking.</param>
    /// <param name="chunkDataOffset">
    ///     Offset of the chunk data (= engine's <c>pAnimFile</c>). Use
    ///     <see cref="PsxMeshFile.TryGetAnimChunkTag" /> to locate it.
    /// </param>
    /// <param name="chunkTag">
    ///     <see cref="PsxMeshFile.HierChunkV1Tag" /> or
    ///     <see cref="PsxMeshFile.HierChunkV2Tag" />.
    /// </param>
    public static PsxAnimFile? Parse(byte[] data, int boneCount, int chunkDataOffset, uint chunkTag)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (chunkDataOffset <= 0 || chunkDataOffset >= data.Length) return null;
        if (data.Length - chunkDataOffset < 16) return null;
        _ = boneCount; // reserved for future prototype inline pool walking

        var hierBase = chunkDataOffset;
        var numStreams = ReadNumStreams(data, hierBase);
        if (numStreams is null) return null;

        var isDirectMatrix = chunkTag == PsxMeshFile.HierChunkV1Tag;

        // Every file uses the same monolithic 8-byte entry table — RunAnim
        // (PERFECT-matched) indexes it directly at runtime with no per-file
        // variation. Accept when every declared entry validates (small v2
        // tables like mj.psx declare only 2) or when at least
        // MonolithicMinValidEntries do (tolerance for stray invalid rows in
        // large tables). The old ≥5-only rule silently demoted small tables to
        // a fabricated "PrototypeSparse" layout that decoded entry 0's frame
        // count against entry 1's data; that variant was an artifact of the
        // earlier mis-anchored parse and does not exist on disc.
        var monolithic = TryParseMonolithic(data, hierBase, numStreams.Value, isDirectMatrix);
        if (monolithic is not null
            && (isDirectMatrix
                || monolithic.Entries.Count == numStreams.Value
                || monolithic.Entries.Count >= MonolithicMinValidEntries))
            return monolithic;

        return null;
    }

    // ─── Monolithic layout (both v1 / 0x2A and v2 / 0x2C) ──────────────────

    private static PsxAnimFile? TryParseMonolithic(byte[] data, int hierBase, int numStreams, bool isDirectMatrix)
    {
        var entriesStart = hierBase + 4;
        // Per-entry dataOffset values are relative to the chunk-data start
        // (the engine reads `pAnimFile + dataOff` directly — see
        // Decomp_GetAnimTransform's 0x2A path and DecompressStream's 0x2C
        // path). Pool therefore must point at hierBase, not past the entry
        // table, otherwise every entry's slice ends up overshooting by
        // `4 + numStreams*8` bytes — which still parses without crashing but
        // reads garbage rotation matrices.
        var poolStart = hierBase;
        var maxValidOffset = data.Length - hierBase;
        var firstDataOff = entriesStart + numStreams * 8 - hierBase;
        if (firstDataOff > maxValidOffset) return null;
        var entries = new List<PsxAnimEntry>(numStreams);

        for (var i = 0; i < numStreams; i++)
        {
            var off = entriesStart + i * 8;
            var poolOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            var frames = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 4));
            var tween = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 6));

            // First entry must live at or past the entry table (anything before
            // it can't be real data); cap by what's available.
            if (poolOff >= firstDataOff && poolOff < maxValidOffset && frames is >= 1 and <= 4096)
                entries.Add(new PsxAnimEntry(poolOff, frames, tween));
        }

        if (entries.Count == 0) return null;

        return new PsxAnimFile
        {
            Layout = isDirectMatrix ? PsxAnimLayoutVariant.DirectMatrix : PsxAnimLayoutVariant.Monolithic,
            FormatRevision = ClassifyFormatRevision(
                isDirectMatrix ? PsxAnimLayoutVariant.DirectMatrix : PsxAnimLayoutVariant.Monolithic,
                numStreams),
            Entries = entries,
            Pool = data.AsMemory(poolStart),
            NumStreamsDeclared = numStreams,
            ChunkTag = isDirectMatrix ? PsxMeshFile.HierChunkV1Tag : PsxMeshFile.HierChunkV2Tag,
            MinimumRuntimeRevision = ClassifyMinimumRuntimeRevision(numStreams)
        };
    }

    private static PsxAnimationFormatRevision ClassifyFormatRevision(
        PsxAnimLayoutVariant layout, int numStreams)
    {
        return layout switch
        {
            PsxAnimLayoutVariant.DirectMatrix => PsxAnimationFormatRevision.DirectMatrixV1,
            PsxAnimLayoutVariant.Monolithic when numStreams > byte.MaxValue =>
                PsxAnimationFormatRevision.CompressedV2ExtendedSlots,
            PsxAnimLayoutVariant.Monolithic => PsxAnimationFormatRevision.CompressedV2,
            _ => PsxAnimationFormatRevision.Unknown
        };
    }

    private static PsxCharacterRuntimeRevision ClassifyMinimumRuntimeRevision(int numStreams)
    {
        return numStreams > byte.MaxValue
            ? PsxCharacterRuntimeRevision.ExtendedAnimSlots
            : PsxCharacterRuntimeRevision.ClassicSuper;
    }

    private static int? ReadNumStreams(byte[] data, int hierBase)
    {
        if (hierBase + 4 > data.Length) return null;
        var numStreams = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hierBase));
        // Spidey/sp2099/spArmor etc. ship 300 streams — bumped the ceiling to
        // 4096 to cover those without making the validation toothless.
        return numStreams is 0 or > 4096 ? null : (int)numStreams;
    }
}
