using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Writes a deterministic, inspection-only manifest for a paired Sound Tools PTR/WBK bank.</summary>
internal static class N64SoundToolsBankJsonExporter
{
    internal const string SchemaName = "neversoft.n64.soundToolsBank";
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(
        string outputPath,
        string pointerSource,
        string waveSource,
        N64SoundToolsBank bank)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, Serialize(pointerSource, waveSource, bank));
    }

    internal static string Serialize(
        string pointerSource,
        string waveSource,
        N64SoundToolsBank bank)
    {
        var pointer = bank.PointerBank;
        var waveBank = bank.WaveBank;
        var waves = pointer.Waves.Select(wave =>
        {
            var baseNote = pointer.BaseNotes[wave.Index];
            var fineTune = pointer.FineTuneCells[wave.Index];
            return new WaveManifest
            {
                Index = wave.Index,
                DescriptorOffset = wave.DescriptorOffset,
                WaveBase = wave.WaveBase,
                EncodedByteLength = wave.WaveLength,
                EncodedFrameCount = wave.WaveLength / 9,
                DecodedSampleCapacity = (long)(wave.WaveLength / 9) * 16,
                BaseNoteRaw = baseNote.Raw,
                CoarseTuneRawSigned = baseNote.CoarseTuneRawSigned,
                DetuneCellRaw = ToNumbers(fineTune.RawBytes),
                FineTuneCents = fineTune.FineTuneCents,
                RuntimePitchOffsetSemitones =
                    baseNote.RuntimeBasePitchOffsetSemitones + fineTune.FineTuneCents / 100.0,
                TypeRaw = wave.TypeRaw,
                FlagsRaw = wave.FlagsRaw,
                PadRaw = wave.PadRaw,
                LoopOffset = wave.LoopOffset,
                BookOffset = wave.BookOffset,
                DescriptorPaddingRaw = ToNumbers(wave.DescriptorPaddingRaw),
                DescriptorAlignmentPadding = ToNumbers(wave.DescriptorAlignmentPadding),
                WaveAlignmentPadding = ToNumbers(waveBank.WaveAlignmentPadding[wave.Index]),
                Book = new BookManifest
                {
                    Order = wave.Book.Order,
                    PredictorCount = wave.Book.PredictorCount,
                    Coefficients = wave.Book.Coefficients.ToArray()
                },
                Loop = wave.Loop is null
                    ? null
                    : new LoopManifest
                    {
                        Start = wave.Loop.Start,
                        End = wave.Loop.End,
                        CountRaw = wave.Loop.CountRaw,
                        State = wave.Loop.State.ToArray()
                    }
            };
        }).ToArray();

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Format = "N64 Sound Tools PTR/WBK",
            PointerSource = pointerSource,
            WaveSource = waveSource,
            WaveCount = waves.Length,
            LoopCount = waves.Count(static wave => wave.Loop != null),
            SampleRate = null,
            CueMappingStatus = "unresolved",
            PointerBank = new PointerBankManifest
            {
                SerializedSize = pointer.SerializedSize,
                LogicalSize = pointer.LogicalSize,
                FlagsRaw = pointer.FlagsRaw,
                WaveBankNameRaw = pointer.WaveBankNameRaw.ToArray(),
                DescriptorPointerTableOffset = pointer.DescriptorPointerTableOffset,
                BaseNoteTableOffset = pointer.BaseNoteTableOffset,
                FineTuneWorkspaceOffset = pointer.FineTuneWorkspaceOffset,
                BaseNotes = pointer.BaseNotes.Select(static value => new BaseNoteManifest
                {
                    Raw = value.Raw,
                    CoarseTuneRawSigned = value.CoarseTuneRawSigned,
                    RuntimeBasePitchOffsetSemitones = value.RuntimeBasePitchOffsetSemitones
                }).ToArray(),
                BaseNoteAlignmentPadding = ToNumbers(pointer.BaseNoteAlignmentPadding),
                FineTuneCells = pointer.FineTuneCells.Select(static value => new FineTuneManifest
                {
                    Raw = ToNumbers(value.RawBytes),
                    FineTuneCents = value.FineTuneCents
                }).ToArray(),
                OuterTrailingPadding = ToNumbers(pointer.OuterTrailingPadding)
            },
            WaveBank = new WaveBankManifest
            {
                SerializedSize = waveBank.SerializedSize,
                TrailingPadding = ToNumbers(waveBank.TrailingPadding)
            },
            Waves = waves
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static int[] ToNumbers(IEnumerable<byte> bytes) =>
        bytes.Select(static value => (int)value).ToArray();

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Format { get; init; }
        public required string PointerSource { get; init; }
        public required string WaveSource { get; init; }
        public required int WaveCount { get; init; }
        public required int LoopCount { get; init; }
        public required int? SampleRate { get; init; }
        public required string CueMappingStatus { get; init; }
        public required PointerBankManifest PointerBank { get; init; }
        public required WaveBankManifest WaveBank { get; init; }
        public required WaveManifest[] Waves { get; init; }
    }

    private sealed class PointerBankManifest
    {
        public required int SerializedSize { get; init; }
        public required int LogicalSize { get; init; }
        public required uint FlagsRaw { get; init; }
        public required uint[] WaveBankNameRaw { get; init; }
        public required int DescriptorPointerTableOffset { get; init; }
        public required int BaseNoteTableOffset { get; init; }
        public required int FineTuneWorkspaceOffset { get; init; }
        public required BaseNoteManifest[] BaseNotes { get; init; }
        public required int[] BaseNoteAlignmentPadding { get; init; }
        public required FineTuneManifest[] FineTuneCells { get; init; }
        public required int[] OuterTrailingPadding { get; init; }
    }

    private sealed class BaseNoteManifest
    {
        public required byte Raw { get; init; }
        public required sbyte CoarseTuneRawSigned { get; init; }
        public required sbyte RuntimeBasePitchOffsetSemitones { get; init; }
    }

    private sealed class FineTuneManifest
    {
        public required int[] Raw { get; init; }
        public required sbyte FineTuneCents { get; init; }
    }

    private sealed class WaveBankManifest
    {
        public required int SerializedSize { get; init; }
        public required int[] TrailingPadding { get; init; }
    }

    private sealed class WaveManifest
    {
        public required int Index { get; init; }
        public required int DescriptorOffset { get; init; }
        public required uint WaveBase { get; init; }
        public required uint EncodedByteLength { get; init; }
        public required uint EncodedFrameCount { get; init; }
        public required long DecodedSampleCapacity { get; init; }
        public required byte BaseNoteRaw { get; init; }
        public required sbyte CoarseTuneRawSigned { get; init; }
        public required int[] DetuneCellRaw { get; init; }
        public required sbyte FineTuneCents { get; init; }
        public required double RuntimePitchOffsetSemitones { get; init; }
        public required byte TypeRaw { get; init; }
        public required byte FlagsRaw { get; init; }
        public required ushort PadRaw { get; init; }
        public required uint LoopOffset { get; init; }
        public required uint BookOffset { get; init; }
        public required int[] DescriptorPaddingRaw { get; init; }
        public required int[] DescriptorAlignmentPadding { get; init; }
        public required int[] WaveAlignmentPadding { get; init; }
        public required BookManifest Book { get; init; }
        public required LoopManifest? Loop { get; init; }
    }

    private sealed class BookManifest
    {
        public required int Order { get; init; }
        public required int PredictorCount { get; init; }
        public required short[] Coefficients { get; init; }
    }

    private sealed class LoopManifest
    {
        public required uint Start { get; init; }
        public required uint End { get; init; }
        public required uint CountRaw { get; init; }
        public required short[] State { get; init; }
    }
}
