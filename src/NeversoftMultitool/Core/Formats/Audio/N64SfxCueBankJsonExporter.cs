using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Writes one deterministic aggregate inspection manifest for strict N64
///     SFX cue tables. A standalone table is represented by a one-item bank
///     array; a supported ROM may produce any number of banks, including zero.
/// </summary>
internal static class N64SfxCueBankJsonExporter
{
    internal const string SchemaName = "neversoft.n64.sfxCueBanks";
    internal const int CurrentSchemaVersion = 3;
    internal const string ExplicitFileSelection = "explicitFile";
    internal const string StrictRomStructuralScanSelection = "strictRomStructuralScan";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(
        string outputPath,
        string inputSource,
        string selectionBasis,
        IReadOnlyList<N64SfxCueBankSource> banks,
        N64CompiledSfxAliasMap? compiledAliasMap = null,
        N64SfxCueEffectBankBindingProvenance? effectBankBinding = null)
    {
        // Materialize the complete document before creating a directory or
        // opening the destination. Callers likewise resolve every source bank
        // before entering this method.
        var json = Serialize(
            inputSource,
            selectionBasis,
            banks,
            compiledAliasMap,
            effectBankBinding);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, json);
    }

    internal static string Serialize(
        string inputSource,
        string selectionBasis,
        IReadOnlyList<N64SfxCueBankSource> banks,
        N64CompiledSfxAliasMap? compiledAliasMap = null,
        N64SfxCueEffectBankBindingProvenance? effectBankBinding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSource);
        ArgumentNullException.ThrowIfNull(banks);
        if (selectionBasis is not (ExplicitFileSelection or StrictRomStructuralScanSelection))
            throw new ArgumentException("Unknown N64 SFX cue selection basis", nameof(selectionBasis));
        if ((compiledAliasMap is null) != (effectBankBinding is null))
        {
            throw new ArgumentException(
                "A compiled N64 cue alias map and its exact BFX/PTR binding provenance must be supplied together");
        }

        var orderedBanks = banks
            .OrderBy(static bank => bank.Source, StringComparer.Ordinal)
            .Select(bank => ToManifest(bank, compiledAliasMap))
            .ToArray();
        var mappingSummary = N64SfxCueMappingSummary.Create(banks, compiledAliasMap);

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Format = "Neversoft N64 SFX cue tables",
            InputSource = inputSource,
            SelectionBasis = selectionBasis,
            BankCount = orderedBanks.Length,
            RecordCount = checked(orderedBanks.Sum(static bank => bank.RecordCount)),
            CueMappingStatus = mappingSummary.CueMappingStatus,
            ResolvedTargetCount = mappingSummary.ResolvedTargetCount,
            ExplicitlyUnmappedCount = mappingSummary.ExplicitlyUnmappedCount,
            DynamicOverrideCount = mappingSummary.DynamicOverrideCount,
            StateDependentUnknownCount = mappingSummary.StateDependentUnknownCount,
            OutsidePinnedTableCount = mappingSummary.OutsidePinnedTableCount,
            CompiledAliasMap = compiledAliasMap == null
                ? null
                : new CompiledAliasMapManifest
                {
                    DetectionBasis = N64CompiledSfxAliasMapResolver.DetectionBasis,
                    Build = compiledAliasMap.Build,
                    BootSha256 = compiledAliasMap.BootSha256,
                    LookupRoutineOffset = compiledAliasMap.LookupRoutineOffset,
                    LookupRoutineLength = compiledAliasMap.LookupRoutineLength,
                    LookupRoutineSha256 = compiledAliasMap.LookupRoutineSha256,
                    TableOffset = compiledAliasMap.TableOffset,
                    TableSha256 = compiledAliasMap.TableSha256,
                    MaximumAliasInclusive = compiledAliasMap.MaximumAliasInclusive,
                    EffectCount = compiledAliasMap.EffectCount,
                    EffectBankBinding = new EffectBankBindingManifest
                    {
                        BindingBasis = effectBankBinding!.BindingBasis,
                        BfxSource = effectBankBinding.BfxSource,
                        BfxSerializedSize = effectBankBinding.BfxSerializedSize,
                        BfxSha256 = effectBankBinding.BfxSha256,
                        PointerSource = effectBankBinding.PointerSource,
                        PointerSerializedSize = effectBankBinding.PointerSerializedSize,
                        PointerSha256 = effectBankBinding.PointerSha256
                    },
                    TableEntrySize = compiledAliasMap.TableEntrySize,
                    CueAliasMask = compiledAliasMap.CueAliasMask,
                    ExplicitNoTargetRaw = compiledAliasMap.ExplicitNoTargetRaw,
                    EffectIndexMask = compiledAliasMap.EffectIndexMask,
                    RoutingFlagsMask = compiledAliasMap.RoutingFlagsMask,
                    AllowedRoutingFlagsRaw = compiledAliasMap.AllowedRoutingFlagsRaw?
                        .Order()
                        .ToArray(),
                    PinnedEvidenceRanges = compiledAliasMap.PinnedEvidenceRanges
                        .Select(static evidence => new EvidenceRangeManifest
                        {
                            Kind = evidence.Kind,
                            Purpose = evidence.Purpose,
                            Offset = evidence.Offset,
                            Length = evidence.Length,
                            Sha256 = evidence.Sha256
                        }).ToArray(),
                    CueOwnerLayout = compiledAliasMap.CueOwnerLayout is not { } ownerLayout
                        ? null
                        : new CueOwnerLayoutManifest
                        {
                            OwnerIndexRamAddress = ownerLayout.OwnerIndexRamAddress,
                            DescriptorTableOffset = ownerLayout.DescriptorTableOffset,
                            DescriptorTableRamAddress = ownerLayout.DescriptorTableRamAddress,
                            DescriptorEntryStride = ownerLayout.DescriptorEntryStride,
                            ActiveRecordBaseRamAddress = ownerLayout.ActiveRecordBaseRamAddress,
                            ActiveRecordStride = ownerLayout.ActiveRecordStride,
                            ActiveRecordFieldOffset = ownerLayout.ActiveRecordFieldOffset
                        },
                    ContextualResolutions = compiledAliasMap.ContextualResolutions.Values
                        .OrderBy(static item => item.Source, StringComparer.Ordinal)
                        .ThenBy(static item => item.Alias)
                        .ThenBy(static item => item.BankSha256, StringComparer.Ordinal)
                        .Select(item => new ContextualResolutionManifest
                        {
                            Source = item.Source,
                            BankSha256 = item.BankSha256,
                            Alias = item.Alias,
                            ResolutionBasis = item.ResolutionBasis,
                            OwnerLevelIndex = item.OwnerLevelIndex,
                            OwnerSelector = item.OwnerSelector,
                            ActiveStateAddress = item.ActiveStateAddress,
                            OwnerStateBasis = item.OwnerStateBasis,
                            CompiledTargetRaw = item.CompiledTargetRaw,
                            BfxEffectIndex = item.CompiledTargetRaw is { } target
                                ? compiledAliasMap.DecodeEffectIndex(target)
                                : null,
                            RoutingFlagsRaw = item.CompiledTargetRaw is { } routedTarget
                                ? compiledAliasMap.DecodeRoutingFlags(routedTarget)
                                : null,
                            StateDependentRule = item.StateDependentRule == null
                                ? null
                                : ToManifest(compiledAliasMap, item.StateDependentRule)
                        }).ToArray(),
                    DynamicOverrideAliases = compiledAliasMap.DynamicOverrideAliases
                        .Order()
                        .ToArray(),
                    StateDependentRules = compiledAliasMap.DynamicOverrideRules.Values
                        .OrderBy(static rule => rule.Alias)
                        .Select(rule => ToManifest(compiledAliasMap, rule))
                        .ToArray()
                },
            SampleRate = null,
            PitchApplicationStatus = "notApplied",
            PlaybackStatus = "notExecuted",
            Banks = orderedBanks
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static BankManifest ToManifest(
        N64SfxCueBankSource source,
        N64CompiledSfxAliasMap? compiledAliasMap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Source);
        ArgumentNullException.ThrowIfNull(source.Bank);

        return new BankManifest
        {
            Source = source.Source,
            SerializedSize = source.Bank.SerializedSize,
            SerializedSha256 = source.Bank.SerializedSha256,
            ByteOrder = "bigEndian",
            RecordSize = N64SfxCueBank.RecordSize,
            RecordCount = source.Bank.Records.Count,
            TerminatorOffset = source.Bank.TerminatorOffset,
            TerminatorRawHex = Convert.ToHexString(source.Bank.TerminatorRaw.ToArray()),
            Records = source.Bank.Records.Select(record =>
            {
                var resolution = compiledAliasMap?.Resolve(
                    record.AliasRaw,
                    source.Source,
                    source.Bank.SerializedSha256);
                return new RecordManifest
                {
                    Index = record.Index,
                    Offset = record.Offset,
                    LoopFlagRaw = record.LoopFlagRaw,
                    ProgramRaw = record.ProgramRaw,
                    CategoryRaw = record.CategoryRaw,
                    NoteRaw = record.NoteRaw,
                    PitchRaw = record.PitchRaw,
                    VolumeRaw = record.VolumeRaw,
                    AliasRaw = record.AliasRaw,
                    PadRaw = record.PadRaw.Select(static value => (int)value).ToArray(),
                    RecordRawHex = Convert.ToHexString(record.RecordRaw.ToArray()),
                    RecordSha256 = Convert.ToHexString(SHA256.HashData(record.RecordRaw.ToArray())),
                    CompiledAliasResolution = resolution == null
                        ? null
                        : new CompiledAliasResolutionManifest
                        {
                            LookupAlias = resolution.LookupAlias,
                            Status = resolution.Status,
                            CompiledTargetRaw = resolution.CompiledTargetRaw,
                            BfxEffectIndex = resolution.EffectIndex,
                            RoutingFlagsRaw = resolution.RoutingFlagsRaw,
                            ResolutionBasis = resolution.ResolutionBasis,
                            StateDependentRule = resolution.DynamicRule == null
                                ? null
                                : ToManifest(compiledAliasMap!, resolution.DynamicRule)
                        }
                };
            }).ToArray()
        };
    }

    private static StateDependentRuleManifest ToManifest(
        N64CompiledSfxAliasMap map,
        N64CompiledSfxDynamicAliasRule rule) =>
        new()
        {
            Alias = rule.Alias,
            SelectorBasis = rule.SelectorBasis,
            Cases = rule.Cases.Select(item => new StateDependentCaseManifest
            {
                Condition = item.Condition,
                CompiledTargetRaw = item.CompiledTargetRaw,
                Outcome = item.CompiledTargetRaw switch
                {
                    null => StateDependentCaseManifest.NotEstablishedOutcome,
                    var outcomeTarget when outcomeTarget == map.ExplicitNoTargetRaw =>
                        "explicitNoTarget",
                    _ => "target"
                },
                BfxEffectIndex = item.CompiledTargetRaw is { } effectTarget
                    ? map.DecodeEffectIndex(effectTarget)
                    : null,
                RoutingFlagsRaw = item.CompiledTargetRaw is { } routedTarget
                    ? map.DecodeRoutingFlags(routedTarget)
                    : null
            }).ToArray()
        };

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Format { get; init; }
        public required string InputSource { get; init; }
        public required string SelectionBasis { get; init; }
        public required int BankCount { get; init; }
        public required int RecordCount { get; init; }
        public required string CueMappingStatus { get; init; }
        public required int ResolvedTargetCount { get; init; }
        public required int ExplicitlyUnmappedCount { get; init; }
        public required int DynamicOverrideCount { get; init; }
        public required int StateDependentUnknownCount { get; init; }
        public required int OutsidePinnedTableCount { get; init; }
        public required CompiledAliasMapManifest? CompiledAliasMap { get; init; }
        public required int? SampleRate { get; init; }
        public required string PitchApplicationStatus { get; init; }
        public required string PlaybackStatus { get; init; }
        public required BankManifest[] Banks { get; init; }
    }

    private sealed class BankManifest
    {
        public required string Source { get; init; }
        public required int SerializedSize { get; init; }
        public required string SerializedSha256 { get; init; }
        public required string ByteOrder { get; init; }
        public required int RecordSize { get; init; }
        public required int RecordCount { get; init; }
        public required int TerminatorOffset { get; init; }
        public required string TerminatorRawHex { get; init; }
        public required RecordManifest[] Records { get; init; }
    }

    private sealed class RecordManifest
    {
        public required int Index { get; init; }
        public required int Offset { get; init; }
        public required byte LoopFlagRaw { get; init; }
        public required byte ProgramRaw { get; init; }
        public required byte CategoryRaw { get; init; }
        public required byte NoteRaw { get; init; }
        public required ushort PitchRaw { get; init; }
        public required ushort VolumeRaw { get; init; }
        public required uint AliasRaw { get; init; }
        public required int[] PadRaw { get; init; }
        public required string RecordRawHex { get; init; }
        public required string RecordSha256 { get; init; }
        public required CompiledAliasResolutionManifest? CompiledAliasResolution { get; init; }
    }

    private sealed class CompiledAliasMapManifest
    {
        public required string DetectionBasis { get; init; }
        public required string Build { get; init; }
        public required string BootSha256 { get; init; }
        public required int LookupRoutineOffset { get; init; }
        public required int LookupRoutineLength { get; init; }
        public required string LookupRoutineSha256 { get; init; }
        public required int TableOffset { get; init; }
        public required string TableSha256 { get; init; }
        public required int MaximumAliasInclusive { get; init; }
        public required int EffectCount { get; init; }
        public required EffectBankBindingManifest EffectBankBinding { get; init; }
        public required int TableEntrySize { get; init; }
        public required uint CueAliasMask { get; init; }
        public required uint ExplicitNoTargetRaw { get; init; }
        public required uint EffectIndexMask { get; init; }
        public required uint RoutingFlagsMask { get; init; }
        public required uint[]? AllowedRoutingFlagsRaw { get; init; }
        public required EvidenceRangeManifest[] PinnedEvidenceRanges { get; init; }
        public required CueOwnerLayoutManifest? CueOwnerLayout { get; init; }
        public required ContextualResolutionManifest[] ContextualResolutions { get; init; }
        public required uint[] DynamicOverrideAliases { get; init; }
        public required StateDependentRuleManifest[] StateDependentRules { get; init; }
    }

    private sealed class EffectBankBindingManifest
    {
        public required string BindingBasis { get; init; }
        public required string BfxSource { get; init; }
        public required int BfxSerializedSize { get; init; }
        public required string BfxSha256 { get; init; }
        public required string PointerSource { get; init; }
        public required int PointerSerializedSize { get; init; }
        public required string PointerSha256 { get; init; }
    }

    private sealed class EvidenceRangeManifest
    {
        public required string Kind { get; init; }
        public required string Purpose { get; init; }
        public required int Offset { get; init; }
        public required int Length { get; init; }
        public required string Sha256 { get; init; }
    }

    private sealed class CueOwnerLayoutManifest
    {
        public required uint OwnerIndexRamAddress { get; init; }
        public required int DescriptorTableOffset { get; init; }
        public required uint DescriptorTableRamAddress { get; init; }
        public required int DescriptorEntryStride { get; init; }
        public required uint? ActiveRecordBaseRamAddress { get; init; }
        public required int? ActiveRecordStride { get; init; }
        public required int? ActiveRecordFieldOffset { get; init; }
    }

    private sealed class ContextualResolutionManifest
    {
        public required string Source { get; init; }
        public required string BankSha256 { get; init; }
        public required uint Alias { get; init; }
        public required string ResolutionBasis { get; init; }
        public required uint OwnerLevelIndex { get; init; }
        public required ushort OwnerSelector { get; init; }
        public required uint? ActiveStateAddress { get; init; }
        public required string OwnerStateBasis { get; init; }
        public required uint? CompiledTargetRaw { get; init; }
        public required int? BfxEffectIndex { get; init; }
        public required uint? RoutingFlagsRaw { get; init; }
        public required StateDependentRuleManifest? StateDependentRule { get; init; }
    }

    private sealed class CompiledAliasResolutionManifest
    {
        public required uint LookupAlias { get; init; }
        public required string Status { get; init; }
        public required uint? CompiledTargetRaw { get; init; }
        public required int? BfxEffectIndex { get; init; }
        public required uint? RoutingFlagsRaw { get; init; }
        public required string ResolutionBasis { get; init; }
        public required StateDependentRuleManifest? StateDependentRule { get; init; }
    }

    private sealed class StateDependentRuleManifest
    {
        public required uint Alias { get; init; }
        public required string SelectorBasis { get; init; }
        public required StateDependentCaseManifest[] Cases { get; init; }
    }

    private sealed class StateDependentCaseManifest
    {
        internal const string NotEstablishedOutcome = "runtimeOutcomeNotEstablished";

        public required string Condition { get; init; }
        public required uint? CompiledTargetRaw { get; init; }
        public required string Outcome { get; init; }
        public required int? BfxEffectIndex { get; init; }
        public required uint? RoutingFlagsRaw { get; init; }
    }
}

internal sealed record N64SfxCueBankSource(string Source, N64SfxCueBank Bank);

internal sealed record N64SfxCueEffectBankBindingProvenance(
    string BindingBasis,
    string BfxSource,
    int BfxSerializedSize,
    string BfxSha256,
    string PointerSource,
    int PointerSerializedSize,
    string PointerSha256)
{
    internal static N64SfxCueEffectBankBindingProvenance Create(
        string bindingBasis,
        string bfxSource,
        ReadOnlySpan<byte> bfxData,
        string pointerSource,
        ReadOnlySpan<byte> pointerData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingBasis);
        ArgumentException.ThrowIfNullOrWhiteSpace(bfxSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointerSource);
        if (bfxData.IsEmpty)
            throw new ArgumentException("N64 cue BFX provenance data is empty", nameof(bfxData));
        if (pointerData.IsEmpty)
            throw new ArgumentException("N64 cue PTR provenance data is empty", nameof(pointerData));

        return new(
            bindingBasis,
            bfxSource,
            bfxData.Length,
            Convert.ToHexString(SHA256.HashData(bfxData)),
            pointerSource,
            pointerData.Length,
            Convert.ToHexString(SHA256.HashData(pointerData)));
    }
}

internal sealed record N64SfxCueMappingSummary(
    string CueMappingStatus,
    int ResolvedTargetCount,
    int ExplicitlyUnmappedCount,
    int DynamicOverrideCount,
    int StateDependentUnknownCount,
    int OutsidePinnedTableCount)
{
    internal int ExhaustiveStateDependentCount =>
        DynamicOverrideCount - StateDependentUnknownCount;

    internal static N64SfxCueMappingSummary Create(
        IReadOnlyList<N64SfxCueBankSource> banks,
        N64CompiledSfxAliasMap? compiledAliasMap)
    {
        ArgumentNullException.ThrowIfNull(banks);
        if (compiledAliasMap == null)
            return new("unresolved", 0, 0, 0, 0, 0);

        var resolutions = banks
            .SelectMany(source => source.Bank.Records.Select(record =>
                compiledAliasMap.Resolve(
                    record.AliasRaw,
                    source.Source,
                    source.Bank.SerializedSha256)))
            .ToArray();
        var resolvedTargetCount = resolutions.Count(static resolution =>
            resolution.Status == N64CompiledSfxAliasMapResolver.ResolvedStatus);
        var explicitlyUnmappedCount = resolutions.Count(static resolution =>
            resolution.Status == N64CompiledSfxAliasMapResolver.ExplicitlyUnmappedStatus);
        var dynamicOverrideCount = resolutions.Count(static resolution =>
            resolution.Status == N64CompiledSfxAliasMapResolver.DynamicOverrideStatus);
        var stateDependentUnknownCount = resolutions.Count(static resolution =>
            resolution.DynamicRule?.Cases.Any(static item =>
                item.CompiledTargetRaw == null) == true);
        var outsidePinnedTableCount = resolutions.Count(static resolution =>
            resolution.Status == N64CompiledSfxAliasMapResolver.OutsidePinnedTableStatus);
        var cueMappingStatus = outsidePinnedTableCount != 0
            ? "partialOutsidePinnedTable"
            : stateDependentUnknownCount != 0
                ? "partialStateDependentOutcomeNotEstablished"
                : dynamicOverrideCount != 0
                    ? "resolvedIncludingStateDependentChoicesAndExplicitNoTarget"
                    : "resolvedIncludingExplicitNoTarget";

        return new(
            cueMappingStatus,
            resolvedTargetCount,
            explicitlyUnmappedCount,
            dynamicOverrideCount,
            stateDependentUnknownCount,
            outsidePinnedTableCount);
    }
}
