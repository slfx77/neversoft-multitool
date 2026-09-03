using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     A build-pinned game-level alias-to-BFX table recovered from executable
///     code. This is intentionally separate from the cross-platform raw cue
///     fields: only the cue alias participates in this proven N64 join.
/// </summary>
public sealed record N64CompiledSfxAliasMap(
    string Build,
    string BootSha256,
    int LookupRoutineOffset,
    int LookupRoutineLength,
    string LookupRoutineSha256,
    int TableOffset,
    string TableSha256,
    int MaximumAliasInclusive,
    int EffectCount,
    int TableEntrySize,
    uint CueAliasMask,
    uint ExplicitNoTargetRaw,
    uint EffectIndexMask,
    uint RoutingFlagsMask,
    IReadOnlySet<uint>? AllowedRoutingFlagsRaw,
    IReadOnlyList<N64CompiledSfxEvidenceRange> PinnedEvidenceRanges,
    N64CompiledSfxCueOwnerLayout? CueOwnerLayout,
    IReadOnlyDictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>
        ContextualResolutions,
    IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule> DynamicOverrideRules,
    IReadOnlyList<uint> StaticTableRaw)
{
    public IReadOnlySet<uint> DynamicOverrideAliases { get; } =
        DynamicOverrideRules.Keys.ToFrozenSet();

    public N64CompiledSfxAliasResolution Resolve(
        uint alias,
        string? cueSource = null,
        string? cueBankSha256 = null)
    {
        var lookupAlias = alias & CueAliasMask;
        if (lookupAlias > (uint)MaximumAliasInclusive)
        {
            return new N64CompiledSfxAliasResolution(
                alias,
                lookupAlias,
                N64CompiledSfxAliasMapResolver.OutsidePinnedTableStatus,
                null,
                null,
                null,
                N64CompiledSfxAliasMapResolver.OutsidePinnedTableBasis,
                null);
        }

        if (cueSource != null && cueBankSha256 != null &&
            ContextualResolutions.TryGetValue(
                new N64CompiledSfxCueContextKey(cueSource, cueBankSha256, lookupAlias),
                out var contextual))
        {
            if (contextual.CompiledTargetRaw is { } contextualTarget)
            {
                return ResolveTarget(
                    alias,
                    lookupAlias,
                    contextualTarget,
                    contextual.ResolutionBasis);
            }

            return new N64CompiledSfxAliasResolution(
                alias,
                lookupAlias,
                N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
                null,
                null,
                null,
                contextual.ResolutionBasis,
                contextual.StateDependentRule);
        }

        if (DynamicOverrideRules.TryGetValue(lookupAlias, out var dynamicRule))
        {
            return new N64CompiledSfxAliasResolution(
                alias,
                lookupAlias,
                N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
                null,
                null,
                null,
                N64CompiledSfxAliasMapResolver.ExecutableStateBranchBasis,
                dynamicRule);
        }

        var raw = StaticTableRaw[(int)lookupAlias];
        return ResolveTarget(
            alias,
            lookupAlias,
            raw,
            N64CompiledSfxAliasMapResolver.CompiledTableBasis);
    }

    private N64CompiledSfxAliasResolution ResolveTarget(
        uint alias,
        uint lookupAlias,
        uint raw,
        string resolutionBasis)
    {
        if (raw == ExplicitNoTargetRaw)
        {
            return new N64CompiledSfxAliasResolution(
                alias,
                lookupAlias,
                N64CompiledSfxAliasMapResolver.ExplicitlyUnmappedStatus,
                raw,
                null,
                null,
                resolutionBasis,
                null);
        }

        return new N64CompiledSfxAliasResolution(
            alias,
            lookupAlias,
            N64CompiledSfxAliasMapResolver.ResolvedStatus,
            raw,
            checked((int)(raw & EffectIndexMask)),
            raw & RoutingFlagsMask,
            resolutionBasis,
            null);
    }

    public int? DecodeEffectIndex(uint compiledTargetRaw) =>
        compiledTargetRaw == ExplicitNoTargetRaw
            ? null
            : checked((int)(compiledTargetRaw & EffectIndexMask));

    public uint? DecodeRoutingFlags(uint compiledTargetRaw) =>
        compiledTargetRaw == ExplicitNoTargetRaw
            ? null
            : compiledTargetRaw & RoutingFlagsMask;
}

public sealed record N64CompiledSfxAliasResolution(
    uint Alias,
    uint LookupAlias,
    string Status,
    uint? CompiledTargetRaw,
    int? EffectIndex,
    uint? RoutingFlagsRaw,
    string ResolutionBasis,
    N64CompiledSfxDynamicAliasRule? DynamicRule);

public readonly record struct N64CompiledSfxCueContextKey(
    string Source,
    string BankSha256,
    uint Alias);

/// <summary>
///     A state branch narrowed only when the exact boot, carved source path,
///     bank bytes, alias, and runtime-owner lifetime all match independently
///     audited ownership evidence. The audited production maps currently leave
///     this collection empty and therefore require live state before narrowing.
/// </summary>
public sealed record N64CompiledSfxCueContextResolution(
    string Source,
    string BankSha256,
    uint Alias,
    string ResolutionBasis,
    uint OwnerLevelIndex,
    ushort OwnerSelector,
    uint? ActiveStateAddress,
    string OwnerStateBasis,
    uint? CompiledTargetRaw,
    N64CompiledSfxDynamicAliasRule? StateDependentRule);

/// <summary>
///     Structured executable layout describing runtime owner selectors and
///     optional per-owner active-state fields. It does not, by itself, bind a
///     carved cue bank to the live owner at playback time.
/// </summary>
public sealed record N64CompiledSfxCueOwnerLayout(
    uint OwnerIndexRamAddress,
    int DescriptorTableOffset,
    uint DescriptorTableRamAddress,
    int DescriptorEntryStride,
    uint? ActiveRecordBaseRamAddress,
    int? ActiveRecordStride,
    int? ActiveRecordFieldOffset);

/// <summary>
///     An additional SHA-pinned executable code or data range needed to prove
///     a compiled table's ownership, duplicated call path, or target encoding.
/// </summary>
public sealed record N64CompiledSfxEvidenceRange(
    string Kind,
    string Purpose,
    int Offset,
    int Length,
    string Sha256);

/// <summary>
///     State-dependent branches recovered from one SHA-pinned executable
///     lookup routine. Conditions deliberately name raw MIPS state reads rather
///     than assigning unproved gameplay meanings to them. A null case target is
///     an explicitly retained outcome that the pinned runtime-state evidence
///     does not establish.
/// </summary>
public sealed record N64CompiledSfxDynamicAliasRule(
    uint Alias,
    string SelectorBasis,
    IReadOnlyList<N64CompiledSfxDynamicAliasCase> Cases);

/// <summary>
///     One exhaustive branch of a dynamic alias lookup. A target of
///     the map's <c>ExplicitNoTargetRaw</c> value is an explicit no-target
///     return, not an unknown.
/// </summary>
public sealed record N64CompiledSfxDynamicAliasCase(
    string Condition,
    uint? CompiledTargetRaw);

/// <summary>
///     Resolves only executable tables independently pinned in the audited
///     final THPS2, THPS3, and Spider-Man N64 boots. THPS1 contains no carved
///     cue banks and is not assigned a speculative alias table.
/// </summary>
public static class N64CompiledSfxAliasMapResolver
{
    public const string DetectionBasis =
        "exactBootSha256PlaybackCodeCompiledAliasTableAndStateBranches";
    public const string ResolvedStatus = "resolved";
    public const string ExplicitlyUnmappedStatus = "explicitlyUnmapped";
    public const string DynamicOverrideStatus = "stateDependentRuntimeResolution";
    public const string OutsidePinnedTableStatus = "outsidePinnedTable";
    public const string CompiledTableBasis = "exactPinnedCompiledAliasTable";
    public const string ExecutableStateBranchBasis = "exactPinnedExecutableStateBranch";
    public const string ContextualOwnerBranchBasis =
        "exactPinnedCueBankRuntimeOwnerAndExecutableBranch";
    public const string OutsidePinnedTableBasis = "outsidePinnedCompiledAliasTable";
    public const string CodeEvidenceKind = "code";
    public const string DataEvidenceKind = "data";

    public const uint EffectIndexMask = 0x03FF;
    public const uint RoutingFlagsMask = 0xFC00;

    internal const int Thps2LookupRoutineOffset = 0x1219C;
    internal const int Thps2LookupRoutineLength = 0x230;
    internal const string Thps2LookupRoutineSha256 =
        "71D5DB520DC4985DCA0F775B3DFB035B7B02B4F077F52356792BA0CCD38E6C42";
    internal const int Thps2CueParserOffset = 0x12EAC;
    internal const int Thps2CueParserLength = 0x158;
    internal const string Thps2CueParserSha256 =
        "B5FE669E6E7F77FF72E1369AA23DD0F632D0D702B7F9A54C75B2AB0430F2E72A";
    internal const int Thps2TableOffset = 0xC4450;
    internal const int Thps2MaximumAliasInclusive = 395;
    internal const string Thps2TableSha256 =
        "C948CEC93EE6EA776E5B397E7A6C4CB1069F7BE2CD45E7C67502ECF6AFAE20AB";
    internal const int Thps2EffectCount = 322;
    internal const int Thps2OwnerDescriptorTableOffset = 0xC6550;
    internal const uint Thps2OwnerIndexRamAddress = 0x800E4A84;
    internal const uint Thps2OwnerDescriptorTableRamAddress = 0x800DD070;
    internal const int Thps2OwnerDescriptorEntryStride = 0x10;
    internal const uint Thps2ActiveRecordBaseRamAddress = 0x800F0E80;
    internal const int Thps2ActiveRecordStride = 0x180;
    internal const int Thps2ActiveRecordFieldOffset = 0x44;

    internal const int Thps3LookupRoutineOffset = 0x110BC;
    internal const int Thps3LookupRoutineLength = 0x54;
    internal const string Thps3LookupRoutineSha256 =
        "0E13279B2E559FD1EA027CC8B6E289E0B5FAF3190777B2897E8BCDAFAFDB2378";
    internal const int Thps3CueParserOffset = 0x11A48;
    internal const int Thps3CueParserLength = 0x158;
    internal const string Thps3CueParserSha256 =
        "E8D8371CD6FD58A2A86EE843B6264734D0BC347F897BA993E5CA8AAA5FF6A403";
    internal const int Thps3TableOffset = 0xC9160;
    internal const int Thps3MaximumAliasInclusive = 472;
    internal const string Thps3TableSha256 =
        "C7D992C17AD48D77A51BED25AFC90A9EAECD95443095C4E4AA682B3FE00F26FE";
    internal const int Thps3EffectCount = 186;

    internal const int SpiderManLookupRoutineOffset = 0x19990;
    internal const int SpiderManLookupRoutineLength = 0xE8;
    internal const string SpiderManLookupRoutineSha256 =
        "871E6AF76CEAAB13E49DA0B826C8890DC48353E4FC7870BF4D3AE9CFF81912B5";
    internal const int SpiderManCueParserOffset = 0x19614;
    internal const int SpiderManCueParserLength = 0x134;
    internal const string SpiderManCueParserSha256 =
        "27D0336C7CDAB25437C9875096F50F41B90B87651C84FA50A05629EEDB38DFD2";
    internal const int SpiderManSecondLookupRoutineOffset = 0x19E5C;
    internal const int SpiderManSecondLookupRoutineLength = 0xE8;
    internal const string SpiderManSecondLookupRoutineSha256 =
        "C2FDAF2195751D3D62A75C6B51801ACF61205091A1185A938173F03E66D29CD6";
    internal const int SpiderManPackedClassHelperOffset = 0x18FF4;
    internal const int SpiderManPackedClassHelperLength = 0x620;
    internal const string SpiderManPackedClassHelperSha256 =
        "B02F14968AF95EA1AA170232C108242E7B137AB58DCDDC1B190BEAF0C682A286";
    internal const int SpiderManDirectLookupHelperOffset = 0x1A2F8;
    internal const int SpiderManDirectLookupHelperLength = 0xE0;
    internal const string SpiderManDirectLookupHelperSha256 =
        "5E3B08A20A3FACA95B7D6C99E7649B5917AD1B14CEEC5725230C5044893C9AE1";
    internal const int SpiderManTableOffset = 0xCFB58;
    internal const int SpiderManMaximumAliasInclusive = 481;
    internal const string SpiderManTableSha256 =
        "56102795512EC3FE2D1375CCFD5CEBD93A69CCC6156E23900D6223EA84235FE7";
    internal const int SpiderManEffectCount = 994;
    internal const uint SpiderManExplicitNoTargetRaw = 0x00000FA0;
    internal const uint SpiderManEffectIndexMask = 0x0000FFFF;
    internal const uint SpiderManRoutingFlagsMask = 0x001F0000;

    private static readonly IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule>
        Thps2DynamicOverrides = new Dictionary<uint, N64CompiledSfxDynamicAliasRule>
        {
            [0xF4] = new(
                0xF4,
                "f32@800DBF90 ordered comparison with zero",
                [
                    new("f32@800DBF90 <= 0; reset it from f32@80016E58", 0x0023),
                    new("otherwise (greater than zero or unordered)", 0xFFFF)
                ]),
            [0x13C] = new(
                0x13C,
                "u16@800E4A86 jump-table index",
                [
                    new("u16@800E4A86 == 0", 0x0139),
                    new("u16@800E4A86 == 1", 0x0003),
                    new("u16@800E4A86 == 2", 0xFFFF),
                    new("u16@800E4A86 == 3", 0x00AF),
                    new("u16@800E4A86 == 4", 0x0118),
                    new("u16@800E4A86 == 5", 0xFFFF),
                    new("u16@800E4A86 == 6", 0x00BE),
                    new("u16@800E4A86 > 6", 0xFFFF)
                ]),
            [0x156] = new(
                0x156,
                "u16@(800DD070 + u32@800E4A84 * 16)",
                [
                    new("selector == 0x0067", 0x008A),
                    new("selector == 0x0073", 0x0098),
                    new("selector == 0x007F", 0x0022),
                    new("otherwise", 0xFFFF)
                ]),
            [0x157] = new(
                0x157,
                "u16@(800DD070 + u32@800E4A84 * 16)",
                [
                    new("selector == 0x0067", 0x0086),
                    new("selector == 0x0073 or selector == 0x007F", 0x0080),
                    new("otherwise", 0xFFFF)
                ]),
            [0x158] = new(
                0x158,
                "guards u32@800E84D0 == 1 and " +
                "u32@(800F0E80 + u32@800E4A84 * 0x180 + 0x44) != 0, then " +
                "u16@(800DD070 + u32@800E4A84 * 16)",
                [
                    new("guards pass and selector == 0x0060", 0x0058),
                    new("guards pass and selector == 0x0079", 0x0119),
                    new("guards pass and selector == 0x0033", 0x00BF),
                    new("guards fail", 0xFFFF),
                    new(
                        "guards pass and selector has any other value; function return is not established",
                        null)
                ])
        }.ToFrozenDictionary();

    private static readonly IReadOnlyDictionary<
        N64CompiledSfxCueContextKey,
        N64CompiledSfxCueContextResolution> NoContextualResolutions =
        new Dictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>()
            .ToFrozenDictionary();

    // The THPS3 routine checks these four legacy aliases but returns FFFF for
    // every one before consulting the table. They are exact no-targets rather
    // than state-dependent overrides, and no audited THPS3 cue uses them.
    private static readonly IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule>
        NoDynamicOverrides = new Dictionary<uint, N64CompiledSfxDynamicAliasRule>()
            .ToFrozenDictionary();

    private static readonly N64CompiledSfxCueOwnerLayout Thps2CueOwnerLayout = new(
        Thps2OwnerIndexRamAddress,
        Thps2OwnerDescriptorTableOffset,
        Thps2OwnerDescriptorTableRamAddress,
        Thps2OwnerDescriptorEntryStride,
        Thps2ActiveRecordBaseRamAddress,
        Thps2ActiveRecordStride,
        Thps2ActiveRecordFieldOffset);

    private static readonly IReadOnlySet<uint> SpiderManAllowedRoutingFlags = new uint[]
    {
        0,
        0x00010000,
        0x00020000,
        0x00040000,
        0x00080000,
        0x00100000
    }.ToFrozenSet();

    private static readonly IReadOnlyList<N64CompiledSfxEvidenceRange>
        SpiderManPinnedEvidenceRanges =
        [
            new(
                CodeEvidenceKind,
                "cue parser: big-endian record +8 u32 copied unchanged into the runtime alias list",
                SpiderManCueParserOffset,
                SpiderManCueParserLength,
                SpiderManCueParserSha256),
            new(
                CodeEvidenceKind,
                "second cue playback path: alias table load and full packed no-target comparison",
                SpiderManSecondLookupRoutineOffset,
                SpiderManSecondLookupRoutineLength,
                SpiderManSecondLookupRoutineSha256),
            new(
                CodeEvidenceKind,
                "packed target class-bit dispatch and effect-index handoff",
                SpiderManPackedClassHelperOffset,
                SpiderManPackedClassHelperLength,
                SpiderManPackedClassHelperSha256),
            new(
                CodeEvidenceKind,
                "direct full-u32 alias table index, no-target comparison, and packed target handoff",
                SpiderManDirectLookupHelperOffset,
                SpiderManDirectLookupHelperLength,
                SpiderManDirectLookupHelperSha256)
        ];

    private static readonly IReadOnlyList<N64CompiledSfxEvidenceRange>
        Thps2PinnedEvidenceRanges =
        [
            new(CodeEvidenceKind,
                "cue parser: first-boundary terminator, record stride, and low-u16 alias load",
                Thps2CueParserOffset, Thps2CueParserLength, Thps2CueParserSha256),
            new(DataEvidenceKind,
                "level descriptor selector table used by runtime state branches",
                Thps2OwnerDescriptorTableOffset, 0xA0,
                "696845B42634B95F560E983276B2D8CF481B3B3C1850396E8057D6BBA156C14C"),
            new(CodeEvidenceKind, "cue lookup caller 1 and FFFF no-play branch", 0x131E8, 0x20,
                "391E2D63C3B106A150B6C8ADE9D6A991CE67BCBA6475EEFA2FBAC560EC3904CF"),
            new(CodeEvidenceKind, "cue lookup caller 2 and FFFF no-play branch", 0x13390, 0x20,
                "192C99FAC3459FB81423C883A565ECD82B1ECAF10ABBA2A3673139D04EF9905C"),
            new(CodeEvidenceKind, "cue lookup caller 3 and FFFF no-play branch", 0x1362C, 0x20,
                "4BD3203476D11679FA0DD693ADB444CCE8073EF38493F7022153FB48AD63870F"),
            new(CodeEvidenceKind, "cue lookup caller 4 and FFFF no-play branch", 0x137F4, 0x20,
                "D92572E1C73617EE1C5B300231F7D76D566108173EE2C0838C267F2EE39555D3"),
            new(CodeEvidenceKind, "cue lookup caller 5 and FFFF no-play branch", 0x13C24, 0x20,
                "245457204A54DE36C58A616D99662D5E4927BA2DA59F0166E889E3652B4CDE58"),
            new(CodeEvidenceKind, "cue lookup caller 6 and FFFF no-play branch", 0x13DB0, 0x20,
                "2FEBF0464B4E69543229363DD8C4712B89E767238E5316503FF9D4FF74DD8736")
        ];

    private static readonly IReadOnlyList<N64CompiledSfxEvidenceRange>
        Thps3PinnedEvidenceRanges =
        [
            new(CodeEvidenceKind,
                "cue parser: first-boundary terminator, record stride, and low-u16 alias load",
                Thps3CueParserOffset, Thps3CueParserLength, Thps3CueParserSha256),
            new(CodeEvidenceKind, "cue lookup caller 1 and FFFF no-play branch", 0x11D84, 0x20,
                "C0A11D283BCB5BAE06ED66BBA4EBE250C278C5946FEB0B1D87BF2BB2A15B4D15"),
            new(CodeEvidenceKind, "cue lookup caller 2 and FFFF no-play branch", 0x11F2C, 0x20,
                "9E3BBD34687138D748C5DA77936F3B44AA4B371019A8C2B758C04A5DB862124A"),
            new(CodeEvidenceKind, "cue lookup caller 3 and FFFF no-play branch", 0x12174, 0x20,
                "BFFA39FECCE6B1FE5FA016D8EF0748BB5359831B63067C15F90965B18726155B"),
            new(CodeEvidenceKind, "cue lookup caller 4 and FFFF no-play branch", 0x1235C, 0x20,
                "E60C3334C2A63538C947D7C813D1D58026566CC3BB8524C85646AC32D8C2C631"),
            new(CodeEvidenceKind, "cue lookup caller 5 and FFFF no-play branch", 0x12774, 0x20,
                "82BEE920110BFD23CD7423ED46992FADDE2DCC283830F196D5874176E1E9E4B2"),
            new(CodeEvidenceKind, "cue lookup caller 6 and FFFF no-play branch", 0x12900, 0x20,
                "02A0DC677E656C18B33F7D6154E0E8356FAF58D051F5876BAF43EC421EEFA44F")
        ];

    public static bool TryResolve(
        ReadOnlySpan<byte> bootData,
        int effectCount,
        out N64CompiledSfxAliasMap? map)
    {
        var bootSha256 = Convert.ToHexString(SHA256.HashData(bootData));
        switch (bootSha256)
        {
            case N64SoundToolsRuntimeProfileResolver.Thps2BootSha256:
                map = ResolveForEvidence(
                    bootData,
                    "Tony Hawk's Pro Skater 2 (USA, final)",
                    bootSha256,
                    Thps2LookupRoutineOffset,
                    Thps2LookupRoutineLength,
                    Thps2LookupRoutineSha256,
                    Thps2TableOffset,
                    Thps2MaximumAliasInclusive,
                    Thps2TableSha256,
                    Thps2EffectCount,
                    effectCount,
                    Thps2DynamicOverrides,
                    NoContextualResolutions,
                    Thps2PinnedEvidenceRanges,
                    Thps2CueOwnerLayout);
                return true;
            case N64SoundToolsRuntimeProfileResolver.Thps3BootSha256:
                map = ResolveForEvidence(
                    bootData,
                    "Tony Hawk's Pro Skater 3 (USA, final)",
                    bootSha256,
                    Thps3LookupRoutineOffset,
                    Thps3LookupRoutineLength,
                    Thps3LookupRoutineSha256,
                    Thps3TableOffset,
                    Thps3MaximumAliasInclusive,
                    Thps3TableSha256,
                    Thps3EffectCount,
                    effectCount,
                    NoDynamicOverrides,
                    pinnedEvidenceRanges: Thps3PinnedEvidenceRanges);
                return true;
            case N64SoundToolsRuntimeProfileResolver.SpiderManBootSha256:
                map = ResolvePackedForEvidence(
                    bootData,
                    "Spider-Man (USA, final)",
                    bootSha256,
                    SpiderManLookupRoutineOffset,
                    SpiderManLookupRoutineLength,
                    SpiderManLookupRoutineSha256,
                    SpiderManTableOffset,
                    SpiderManMaximumAliasInclusive,
                    SpiderManTableSha256,
                    SpiderManEffectCount,
                    effectCount,
                    sizeof(uint),
                    uint.MaxValue,
                    SpiderManExplicitNoTargetRaw,
                    SpiderManEffectIndexMask,
                    SpiderManRoutingFlagsMask,
                    SpiderManAllowedRoutingFlags,
                    SpiderManPinnedEvidenceRanges,
                    NoContextualResolutions,
                    NoDynamicOverrides);
                return true;
            default:
                map = null;
                return false;
        }
    }

    internal static N64CompiledSfxAliasMap ResolveForEvidence(
        ReadOnlySpan<byte> bootData,
        string build,
        string expectedBootSha256,
        int lookupRoutineOffset,
        int lookupRoutineLength,
        string expectedLookupRoutineSha256,
        int tableOffset,
        int maximumAliasInclusive,
        string expectedTableSha256,
        int expectedEffectCount,
        int actualEffectCount,
        IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule> dynamicOverrideRules,
        IReadOnlyDictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>?
            contextualResolutions = null,
        IReadOnlyList<N64CompiledSfxEvidenceRange>? pinnedEvidenceRanges = null,
        N64CompiledSfxCueOwnerLayout? cueOwnerLayout = null) =>
        ResolvePackedForEvidence(
            bootData,
            build,
            expectedBootSha256,
            lookupRoutineOffset,
            lookupRoutineLength,
            expectedLookupRoutineSha256,
            tableOffset,
            maximumAliasInclusive,
            expectedTableSha256,
            expectedEffectCount,
            actualEffectCount,
            sizeof(ushort),
            ushort.MaxValue,
            ushort.MaxValue,
            EffectIndexMask,
            RoutingFlagsMask,
            allowedRoutingFlagsRaw: null,
            pinnedEvidenceRanges: pinnedEvidenceRanges ?? [],
            contextualResolutions: contextualResolutions ?? NoContextualResolutions,
            dynamicOverrideRules: dynamicOverrideRules,
            cueOwnerLayout: cueOwnerLayout);

    internal static N64CompiledSfxAliasMap ResolvePackedForEvidence(
        ReadOnlySpan<byte> bootData,
        string build,
        string expectedBootSha256,
        int lookupRoutineOffset,
        int lookupRoutineLength,
        string expectedLookupRoutineSha256,
        int tableOffset,
        int maximumAliasInclusive,
        string expectedTableSha256,
        int expectedEffectCount,
        int actualEffectCount,
        int tableEntrySize,
        uint cueAliasMask,
        uint explicitNoTargetRaw,
        uint effectIndexMask,
        uint routingFlagsMask,
        IReadOnlySet<uint>? allowedRoutingFlagsRaw,
        IReadOnlyList<N64CompiledSfxEvidenceRange> pinnedEvidenceRanges,
        IReadOnlyDictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>
            contextualResolutions,
        IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule> dynamicOverrideRules,
        N64CompiledSfxCueOwnerLayout? cueOwnerLayout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBootSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLookupRoutineSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTableSha256);
        ArgumentNullException.ThrowIfNull(pinnedEvidenceRanges);
        ArgumentNullException.ThrowIfNull(contextualResolutions);
        ArgumentNullException.ThrowIfNull(dynamicOverrideRules);
        ArgumentOutOfRangeException.ThrowIfNegative(lookupRoutineOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lookupRoutineLength);
        ArgumentOutOfRangeException.ThrowIfNegative(tableOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumAliasInclusive);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedEffectCount);
        Require(tableEntrySize is sizeof(ushort) or sizeof(uint),
            "compiled SFX alias table entry size must be 2 or 4 bytes");
        Require(cueAliasMask is ushort.MaxValue or uint.MaxValue,
            "compiled SFX cue alias mask must select the low 16 or all 32 bits");
        Require((uint)maximumAliasInclusive <= cueAliasMask,
            "compiled SFX cue alias mask excludes part of the pinned table range");
        Require(effectIndexMask != 0, "compiled SFX alias effect-index mask is empty");
        Require((effectIndexMask & routingFlagsMask) == 0,
            "compiled SFX alias effect and routing masks overlap");
        if (allowedRoutingFlagsRaw != null)
        {
            Require(allowedRoutingFlagsRaw.Count != 0,
                "compiled SFX alias allowed-routing set is empty");
            Require(allowedRoutingFlagsRaw.All(value => (value & ~routingFlagsMask) == 0),
                "compiled SFX alias allowed-routing value lies outside its mask");
        }

        var actualBootSha256 = Convert.ToHexString(SHA256.HashData(bootData));
        Require(StringComparer.Ordinal.Equals(actualBootSha256, expectedBootSha256),
            $"compiled SFX alias map boot SHA-256 is {actualBootSha256}, expected {expectedBootSha256}");
        Require(actualEffectCount == expectedEffectCount,
            $"compiled SFX alias map expects {expectedEffectCount} BFX effects, found {actualEffectCount}");

        var lookupEnd = checked((long)lookupRoutineOffset + lookupRoutineLength);
        Require(lookupEnd <= bootData.Length,
            "compiled SFX alias lookup routine is truncated");
        var actualLookupHash = Convert.ToHexString(SHA256.HashData(
            bootData.Slice(lookupRoutineOffset, lookupRoutineLength)));
        Require(StringComparer.Ordinal.Equals(actualLookupHash, expectedLookupRoutineSha256),
            $"compiled SFX alias lookup routine SHA-256 is {actualLookupHash}, " +
            $"expected {expectedLookupRoutineSha256}");

        var frozenEvidenceRanges = new N64CompiledSfxEvidenceRange[pinnedEvidenceRanges.Count];
        for (var index = 0; index < frozenEvidenceRanges.Length; index++)
        {
            frozenEvidenceRanges[index] = ValidateEvidenceRange(
                bootData,
                pinnedEvidenceRanges[index]);
        }

        var entryCount = checked(maximumAliasInclusive + 1);
        var tableLength = checked(entryCount * tableEntrySize);
        var tableEnd = checked((long)tableOffset + tableLength);
        Require(tableEnd <= bootData.Length, "compiled SFX alias table is truncated");
        var tableData = bootData.Slice(tableOffset, tableLength);
        var actualTableHash = Convert.ToHexString(SHA256.HashData(tableData));
        Require(StringComparer.Ordinal.Equals(actualTableHash, expectedTableSha256),
            $"compiled SFX alias table SHA-256 is {actualTableHash}, expected {expectedTableSha256}");

        Require(dynamicOverrideRules.Keys.All(alias => alias <= (uint)maximumAliasInclusive),
            "compiled SFX dynamic override alias is outside the pinned table range");
        foreach (var (alias, rule) in dynamicOverrideRules)
        {
            ValidateDynamicRule(
                alias,
                rule,
                explicitNoTargetRaw,
                effectIndexMask,
                routingFlagsMask,
                allowedRoutingFlagsRaw,
                actualEffectCount);
        }

        var frozenDynamicRules = dynamicOverrideRules
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { Cases = pair.Value.Cases.ToArray() })
            .ToFrozenDictionary();

        Require(contextualResolutions.Count == 0 || cueOwnerLayout != null,
            "compiled SFX contextual resolutions have no structured owner layout");
        if (cueOwnerLayout != null)
            ValidateCueOwnerLayout(bootData, cueOwnerLayout);

        foreach (var (key, contextual) in contextualResolutions)
        {
            Require(StringComparer.Ordinal.Equals(key.Source, contextual.Source) &&
                    StringComparer.Ordinal.Equals(key.BankSha256, contextual.BankSha256) &&
                    key.Alias == contextual.Alias,
                "compiled SFX contextual resolution key does not match its value");
            Require(!string.IsNullOrWhiteSpace(contextual.Source),
                "compiled SFX contextual resolution has no source");
            Require(contextual.BankSha256.Length == 64 &&
                    contextual.BankSha256.All(Uri.IsHexDigit) &&
                    StringComparer.Ordinal.Equals(
                        contextual.BankSha256,
                        contextual.BankSha256.ToUpperInvariant()),
                $"compiled SFX contextual alias {contextual.Alias} has an invalid bank SHA-256");
            Require(!string.IsNullOrWhiteSpace(contextual.ResolutionBasis),
                $"compiled SFX contextual alias {contextual.Alias} has no resolution basis");
            Require(!string.IsNullOrWhiteSpace(contextual.OwnerStateBasis),
                $"compiled SFX contextual alias {contextual.Alias} has no owner state basis");
            ValidateContextOwner(bootData, cueOwnerLayout!, contextual);
            Require(contextual.Alias <= (uint)maximumAliasInclusive,
                "compiled SFX contextual alias is outside the pinned table range");
            Require(frozenDynamicRules.ContainsKey(contextual.Alias),
                $"compiled SFX contextual alias {contextual.Alias} does not specialize a state branch");
            Require(contextual.CompiledTargetRaw.HasValue ^ (contextual.StateDependentRule != null),
                $"compiled SFX contextual alias {contextual.Alias} must have exactly one outcome kind");
            var globalTargets = frozenDynamicRules[contextual.Alias].Cases
                .Where(static item => item.CompiledTargetRaw.HasValue)
                .Select(static item => item.CompiledTargetRaw!.Value)
                .ToHashSet();
            if (contextual.CompiledTargetRaw is { } contextualTarget)
            {
                ValidateTarget(
                    contextualTarget,
                    explicitNoTargetRaw,
                    effectIndexMask,
                    routingFlagsMask,
                    allowedRoutingFlagsRaw,
                    actualEffectCount,
                    $"compiled SFX contextual alias {contextual.Alias}");
                Require(globalTargets.Contains(contextualTarget),
                    $"compiled SFX contextual alias {contextual.Alias} target is absent from its global branch");
            }
            else
            {
                ValidateDynamicRule(
                    contextual.Alias,
                    contextual.StateDependentRule!,
                    explicitNoTargetRaw,
                    effectIndexMask,
                    routingFlagsMask,
                    allowedRoutingFlagsRaw,
                    actualEffectCount);
                Require(contextual.StateDependentRule!.Cases
                        .Where(static item => item.CompiledTargetRaw.HasValue)
                        .All(item => globalTargets.Contains(item.CompiledTargetRaw!.Value)),
                    $"compiled SFX contextual alias {contextual.Alias} rule has a target absent from its global branch");
            }
        }

        var frozenContextualResolutions = contextualResolutions
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with
                {
                    StateDependentRule = pair.Value.StateDependentRule is { } rule
                        ? rule with { Cases = rule.Cases.ToArray() }
                        : null
                })
            .ToFrozenDictionary();
        var entries = new uint[entryCount];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = tableData[(index * tableEntrySize)..];
            var raw = tableEntrySize == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16BigEndian(entry)
                : BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (!frozenDynamicRules.ContainsKey((uint)index))
            {
                ValidateTarget(
                    raw,
                    explicitNoTargetRaw,
                    effectIndexMask,
                    routingFlagsMask,
                    allowedRoutingFlagsRaw,
                    actualEffectCount,
                    $"compiled SFX alias {index}");
            }
            entries[index] = raw;
        }

        return new N64CompiledSfxAliasMap(
            build,
            actualBootSha256,
            lookupRoutineOffset,
            lookupRoutineLength,
            actualLookupHash,
            tableOffset,
            actualTableHash,
            maximumAliasInclusive,
            actualEffectCount,
            tableEntrySize,
            cueAliasMask,
            explicitNoTargetRaw,
            effectIndexMask,
            routingFlagsMask,
            allowedRoutingFlagsRaw?.ToFrozenSet(),
            frozenEvidenceRanges,
            cueOwnerLayout,
            frozenContextualResolutions,
            frozenDynamicRules,
            entries);
    }

    private static N64CompiledSfxEvidenceRange ValidateEvidenceRange(
        ReadOnlySpan<byte> bootData,
        N64CompiledSfxEvidenceRange evidence)
    {
        Require(evidence.Kind is CodeEvidenceKind or DataEvidenceKind,
            $"compiled SFX alias evidence '{evidence.Purpose}' has unknown kind '{evidence.Kind}'");
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence.Sha256);
        ArgumentOutOfRangeException.ThrowIfNegative(evidence.Offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(evidence.Length);
        var end = checked((long)evidence.Offset + evidence.Length);
        Require(end <= bootData.Length,
            $"compiled SFX alias evidence '{evidence.Purpose}' is truncated");
        var actualHash = Convert.ToHexString(SHA256.HashData(
            bootData.Slice(evidence.Offset, evidence.Length)));
        Require(StringComparer.Ordinal.Equals(actualHash, evidence.Sha256),
            $"compiled SFX alias evidence '{evidence.Purpose}' SHA-256 is {actualHash}, " +
            $"expected {evidence.Sha256}");
        return evidence;
    }

    private static void ValidateCueOwnerLayout(
        ReadOnlySpan<byte> bootData,
        N64CompiledSfxCueOwnerLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layout.DescriptorTableOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layout.DescriptorEntryStride);
        Require(layout.DescriptorEntryStride >= sizeof(ushort),
            "compiled SFX owner descriptor stride is smaller than its selector");
        Require(layout.OwnerIndexRamAddress != 0 && layout.DescriptorTableRamAddress != 0,
            "compiled SFX owner layout has a zero runtime address");
        Require(checked((long)layout.DescriptorTableOffset + sizeof(ushort)) <= bootData.Length,
            "compiled SFX owner descriptor table is truncated");
        var activeFields = new object?[]
        {
            layout.ActiveRecordBaseRamAddress,
            layout.ActiveRecordStride,
            layout.ActiveRecordFieldOffset
        };
        Require(activeFields.All(static value => value != null) ||
                activeFields.All(static value => value == null),
            "compiled SFX owner active-record layout is incomplete");
        if (layout.ActiveRecordStride is { } activeStride)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeStride);
            ArgumentOutOfRangeException.ThrowIfNegative(layout.ActiveRecordFieldOffset!.Value);
        }
    }

    private static void ValidateContextOwner(
        ReadOnlySpan<byte> bootData,
        N64CompiledSfxCueOwnerLayout layout,
        N64CompiledSfxCueContextResolution contextual)
    {
        var descriptorOffset = checked(
            (long)layout.DescriptorTableOffset +
            (long)contextual.OwnerLevelIndex * layout.DescriptorEntryStride);
        Require(descriptorOffset + sizeof(ushort) <= bootData.Length,
            $"compiled SFX contextual alias {contextual.Alias} owner descriptor is truncated");
        var actualSelector = BinaryPrimitives.ReadUInt16BigEndian(
            bootData.Slice(checked((int)descriptorOffset), sizeof(ushort)));
        Require(actualSelector == contextual.OwnerSelector,
            $"compiled SFX contextual alias {contextual.Alias} owner selector is " +
            $"0x{actualSelector:X4}, expected 0x{contextual.OwnerSelector:X4}");

        var expectedBasis =
            $"u32@{layout.OwnerIndexRamAddress:X8} == {contextual.OwnerLevelIndex} and " +
            $"u16@({layout.DescriptorTableRamAddress:X8} + {contextual.OwnerLevelIndex} * " +
            $"{layout.DescriptorEntryStride}) == 0x{contextual.OwnerSelector:X4}";
        Require(StringComparer.Ordinal.Equals(contextual.OwnerStateBasis, expectedBasis),
            $"compiled SFX contextual alias {contextual.Alias} owner state basis does not match " +
            "its structured owner fields");

        if (contextual.ActiveStateAddress is not { } activeStateAddress)
            return;

        Require(layout.ActiveRecordBaseRamAddress.HasValue,
            $"compiled SFX contextual alias {contextual.Alias} has an active-state address " +
            "without an active-record layout");
        var expectedActiveStateAddress = checked(
            (ulong)layout.ActiveRecordBaseRamAddress!.Value +
            (ulong)contextual.OwnerLevelIndex * checked((uint)layout.ActiveRecordStride!.Value) +
            checked((uint)layout.ActiveRecordFieldOffset!.Value));
        Require(expectedActiveStateAddress <= uint.MaxValue &&
                activeStateAddress == (uint)expectedActiveStateAddress,
            $"compiled SFX contextual alias {contextual.Alias} active-state address does not " +
            "match its owner index");
    }

    private static void ValidateTarget(
        uint raw,
        uint explicitNoTargetRaw,
        uint effectIndexMask,
        uint routingFlagsMask,
        IReadOnlySet<uint>? allowedRoutingFlagsRaw,
        int effectCount,
        string context)
    {
        if (raw == explicitNoTargetRaw)
            return;

        var routingFlags = raw & routingFlagsMask;
        Require((raw & ~(effectIndexMask | routingFlagsMask)) == 0,
            $"{context} target 0x{raw:X8} has bits outside its pinned encoding");
        Require(allowedRoutingFlagsRaw == null || allowedRoutingFlagsRaw.Contains(routingFlags),
            $"{context} target 0x{raw:X8} has unsupported routing flags 0x{routingFlags:X8}");
        var effectIndex = raw & effectIndexMask;
        Require(effectIndex < checked((uint)effectCount),
            $"{context} targets out-of-range BFX effect {effectIndex}");
    }

    private static void ValidateDynamicRule(
        uint alias,
        N64CompiledSfxDynamicAliasRule rule,
        uint explicitNoTargetRaw,
        uint effectIndexMask,
        uint routingFlagsMask,
        IReadOnlySet<uint>? allowedRoutingFlagsRaw,
        int effectCount)
    {
        Require(rule.Alias == alias,
            $"compiled SFX dynamic rule key {alias} does not match rule alias {rule.Alias}");
        Require(!string.IsNullOrWhiteSpace(rule.SelectorBasis),
            $"compiled SFX dynamic alias {alias} has no selector basis");
        Require(rule.Cases.Count != 0,
            $"compiled SFX dynamic alias {alias} has no state cases");
        Require(rule.Cases.All(static item => !string.IsNullOrWhiteSpace(item.Condition)),
            $"compiled SFX dynamic alias {alias} has a blank case condition");
        Require(rule.Cases.Select(static item => item.Condition).Distinct(StringComparer.Ordinal).Count()
                == rule.Cases.Count,
            $"compiled SFX dynamic alias {alias} repeats a case condition");
        foreach (var item in rule.Cases)
        {
            if (item.CompiledTargetRaw is not { } target)
                continue;
            ValidateTarget(
                target,
                explicitNoTargetRaw,
                effectIndexMask,
                routingFlagsMask,
                allowedRoutingFlagsRaw,
                effectCount,
                $"compiled SFX dynamic alias {alias}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
