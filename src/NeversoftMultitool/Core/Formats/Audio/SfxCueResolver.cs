using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Engine-exact cue-to-sample resolution: the VAB tone-table walk
///     (playSFX) and the KAT direct program mapping.
/// </summary>
internal static class SfxCueResolver
{
    // VAB header/tone-table geometry as playSFX walks it (THPS2 PSX proto decomp):
    // 32-byte header + 128 × 16-byte program attrs end at 0x820, then one 0x200-byte
    // block of 16 × 32-byte tone attrs per *used* program, indexed by raw program number.
    private const uint VabMagic = 0x56414270; // "pBAV"
    private const int VabProgramCountOffset = 0x12;
    private const int VabVagCountOffset = 0x16;
    private const int VabToneTablesStart = 0x820;
    private const int VabToneTableStride = 0x200;
    private const int VabToneEntrySize = 0x20;
    private const int VabToneCenterOffset = 0x04;
    private const int VabToneShiftOffset = 0x05;
    private const int VabToneVagOffset = 0x16;
    private const int MaxToneCategory = 15; // tone mask is 16 bits (1 << category)

    /// <summary>
    ///     Parses the flat cue-record table. Records are 16 bytes starting at offset 0;
    ///     parsing stops at the 0xFFFFFFFF terminator word (SFX_ParseSFXFile) or at a
    ///     fully-zero record (sector padding on prototype disc rips).
    /// </summary>
    internal static bool TryParseCues(byte[] data, out List<SfxCue> cues, out string error)
    {
        cues = [];

        if (data.Length < SfxExtractor.EntrySize)
        {
            error = "Invalid SFX file layout";
            return false;
        }

        for (var offset = 0; offset + SfxExtractor.EntrySize <= data.Length; offset += SfxExtractor.EntrySize)
        {
            if (SfxAliasResolver.ReadUInt32LittleEndian(data, offset) == SfxExtractor.CueTerminator)
                break;

            if (SfxAliasResolver.IsZeroedEntry(data, offset))
                break;

            // The 6 trailing bytes of every real cue record are zero pad; a nonzero
            // pad byte means this is not a THPS2-era cue table.
            for (var padIndex = 10; padIndex < SfxExtractor.EntrySize; padIndex++)
            {
                if (data[offset + padIndex] != 0)
                {
                    cues = [];
                    error = "SFX cue record padding is nonzero (not a cue table)";
                    return false;
                }
            }

            cues.Add(new SfxCue(
                cues.Count,
                data[offset] == SfxExtractor.LoopMarker,
                data[offset + 1],
                data[offset + 2],
                data[offset + 3],
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 8, 2))));
        }

        if (cues.Count == 0)
        {
            error = "SFX file contains no cue entries";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    ///     Builds cue mappings for the resolved companion bank: VAB companions get the
    ///     exact playSFX tone walk, KAT companions get the Spider-Man-style direct rule,
    ///     and anything unresolved falls back to full companion-bank extraction.
    /// </summary>
    internal static List<SfxCueMapping> CreateCueMappings(IReadOnlyList<SfxCue> cues, SfxBankSource bankSource)
    {
        switch (bankSource.BankFormat)
        {
            case "VAB" when TryCreateVabCueMappings(cues, bankSource, out var vabMappings):
                return vabMappings;
            case "KAT" when TryCreateKatDirectMappings(cues, bankSource, out var katMappings):
                return katMappings;
            default:
                return SfxPathResolver.CreateFullBankMappings(bankSource);
        }
    }

    /// <summary>
    ///     Exact cue→VAG resolution per playSFX: tone attrs at
    ///     vab + program*0x200 + 0x820 + category*0x20, VAG index at tone +0x16.
    ///     Cues pointing at programs/tones the bank does not define are silent at
    ///     runtime and are skipped here.
    /// </summary>
    private static bool TryCreateVabCueMappings(
        IReadOnlyList<SfxCue> cues, SfxBankSource bankSource, out List<SfxCueMapping> mappings)
    {
        mappings = [];

        var vab = bankSource.BankData;
        if (vab == null && !string.IsNullOrEmpty(bankSource.BankPath))
        {
            try
            {
                vab = File.ReadAllBytes(bankSource.BankPath);
            }
            catch (IOException)
            {
                return false;
            }
        }

        if (vab == null || vab.Length < VabToneTablesStart ||
            SfxAliasResolver.ReadUInt32LittleEndian(vab, 0) != VabMagic)
        {
            return false;
        }

        int programCount = BinaryPrimitives.ReadUInt16LittleEndian(vab.AsSpan(VabProgramCountOffset, 2));
        int vagCount = BinaryPrimitives.ReadUInt16LittleEndian(vab.AsSpan(VabVagCountOffset, 2));
        var samplesByIndex = bankSource.Samples.ToDictionary(static sample => sample.ExternalIndex);

        foreach (var cue in cues)
        {
            var program = cue.Program & 0x7F;
            var toneAttr = VabToneTablesStart + program * VabToneTableStride + cue.Category * VabToneEntrySize;
            if (cue.Category > MaxToneCategory || program >= programCount ||
                toneAttr + VabToneEntrySize > vab.Length)
            {
                continue;
            }

            var vagIndex = BinaryPrimitives.ReadInt16LittleEndian(vab.AsSpan(toneAttr + VabToneVagOffset, 2));
            if (vagIndex < 1 || vagIndex > vagCount || !samplesByIndex.TryGetValue(vagIndex, out var sample))
                continue;

            var sampleRate = EstimateCueSampleRate(
                cue.Note, vab[toneAttr + VabToneCenterOffset], vab[toneAttr + VabToneShiftOffset]);
            mappings.Add(new SfxCueMapping(cue.CueIndex, cue, sample, bankSource.BankFormat, sampleRate));
        }

        return mappings.Count > 0;
    }

    /// <summary>
    ///     Spider-Man-style direct rule for KAT/PC banks: one sample per cue, selected by
    ///     the cue's program field with category 0. THPS2 DC banks route program+category
    ///     through tone tables that only the PSX VAB companion carries, so any nonzero
    ///     category (or an out-of-range program) rejects the rule and the caller falls
    ///     back to full-bank extraction.
    /// </summary>
    private static bool TryCreateKatDirectMappings(
        IReadOnlyList<SfxCue> cues, SfxBankSource bankSource, out List<SfxCueMapping> mappings)
    {
        mappings = [];

        if (cues.Any(static cue => cue.Category != 0))
            return false;

        var samplesByIndex = bankSource.Samples.ToDictionary(static sample => sample.ExternalIndex);
        var result = new List<SfxCueMapping>(cues.Count);
        foreach (var cue in cues)
        {
            if (!samplesByIndex.TryGetValue(cue.Program, out var sample))
                return false;

            result.Add(new SfxCueMapping(cue.CueIndex, cue, sample, bankSource.BankFormat));
        }

        mappings = result;
        return true;
    }

    /// <summary>
    ///     SsPitchFromNote playback rate for a cue: 44100 × 2^((note − center − shift/128)/12),
    ///     matching <see cref="VabExtractor" />'s per-VAG estimate but at the cue's own note
    ///     instead of the neutral 60.
    /// </summary>
    private static int EstimateCueSampleRate(int note, byte center, byte shift)
    {
        var semitoneOffset = note - center - shift / 128.0;
        var sampleRate = 44100.0 * Math.Pow(2.0, semitoneOffset / 12.0);
        return Math.Clamp((int)Math.Round(sampleRate), 2000, 96000);
    }
}
