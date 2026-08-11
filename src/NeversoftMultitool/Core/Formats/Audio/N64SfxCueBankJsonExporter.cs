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
    internal const int CurrentSchemaVersion = 1;
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
        IReadOnlyList<N64SfxCueBankSource> banks)
    {
        // Materialize the complete document before creating a directory or
        // opening the destination. Callers likewise resolve every source bank
        // before entering this method.
        var json = Serialize(inputSource, selectionBasis, banks);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, json);
    }

    internal static string Serialize(
        string inputSource,
        string selectionBasis,
        IReadOnlyList<N64SfxCueBankSource> banks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSource);
        ArgumentNullException.ThrowIfNull(banks);
        if (selectionBasis is not (ExplicitFileSelection or StrictRomStructuralScanSelection))
            throw new ArgumentException("Unknown N64 SFX cue selection basis", nameof(selectionBasis));

        var orderedBanks = banks
            .OrderBy(static bank => bank.Source, StringComparer.Ordinal)
            .Select(static bank => ToManifest(bank))
            .ToArray();

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Format = "Neversoft N64 SFX cue tables",
            InputSource = inputSource,
            SelectionBasis = selectionBasis,
            BankCount = orderedBanks.Length,
            RecordCount = checked(orderedBanks.Sum(static bank => bank.RecordCount)),
            CueMappingStatus = "unresolved",
            SampleRate = null,
            PitchApplicationStatus = "notApplied",
            PlaybackStatus = "notExecuted",
            Banks = orderedBanks
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static BankManifest ToManifest(N64SfxCueBankSource source)
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
            Records = source.Bank.Records.Select(static record => new RecordManifest
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
                RecordSha256 = Convert.ToHexString(SHA256.HashData(record.RecordRaw.ToArray()))
            }).ToArray()
        };
    }

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
    }
}

internal sealed record N64SfxCueBankSource(string Source, N64SfxCueBank Bank);
