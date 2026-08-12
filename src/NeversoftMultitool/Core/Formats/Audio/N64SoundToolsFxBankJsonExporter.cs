using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Writes a deterministic inspection manifest for one BFX bank bound to a validated PTR bank.</summary>
internal static class N64SoundToolsFxBankJsonExporter
{
    internal const string SchemaName = "neversoft.n64.soundToolsFxBank";
    internal const int CurrentSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(
        string outputPath,
        string fxBankSource,
        string pointerSource,
        string pointerBindingBasis,
        N64SoundToolsFxBank bank,
        N64SoundToolsPointerBank pointerBank)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath,
            Serialize(fxBankSource, pointerSource, pointerBindingBasis, bank, pointerBank));
    }

    internal static string Serialize(
        string fxBankSource,
        string pointerSource,
        string pointerBindingBasis,
        N64SoundToolsFxBank bank,
        N64SoundToolsPointerBank pointerBank)
    {
        var components = bank.Components.Select(component =>
        {
            var binding = N64SoundToolsFxInitialWaveResolver.Resolve(
                bank, pointerBank, component);
            var initialEvent = N64SoundToolsFxInitialEventResolver.Resolve(
                bank, pointerBank, component);
            var continuation = N64SoundToolsFxContinuationResolver.Resolve(
                bank, pointerBank, component);
            var uninterpretedAfterStopRawHex = continuation?.UninterpretedAfterStopRaw is { } raw
                ? Convert.ToHexString(raw.ToArray())
                : null;
            return new ComponentManifest
            {
                Index = component.Index,
                FxDataOffset = component.FxDataOffset,
                ByteLength = component.OpaqueData.Count,
                DefaultPriority = component.DefaultPriority,
                OpaqueDataRawHex = Convert.ToHexString(component.OpaqueData.ToArray()),
                OpaqueDataSha256 = Hash(component.OpaqueData),
                InitialWaveBinding = binding == null
                    ? null
                    : new InitialWaveBindingManifest
                    {
                        Basis = binding.Basis,
                        PrefixByteLength = binding.PrefixByteLength,
                        LocalWaveIndex = binding.LocalWaveIndex,
                        PointerWaveIndex = binding.PointerWaveIndex,
                        PointerDescriptorOffset = binding.PointerWaveDescriptor.DescriptorOffset
                    },
                InitialEvent = initialEvent == null
                    ? null
                    : new InitialEventManifest
                    {
                        Basis = initialEvent.Basis,
                        EncodedByteLength = initialEvent.EncodedByteLength,
                        EndOffset = initialEvent.EndOffset,
                        LeadingLoopCountRaw = initialEvent.LeadingLoopCountRaw,
                        Envelope = new EnvelopeManifest
                        {
                            SpeedRaw = initialEvent.Envelope.SpeedRaw,
                            InitialVolumeRaw = initialEvent.Envelope.InitialVolumeRaw,
                            AttackSpeedRaw = initialEvent.Envelope.AttackSpeedRaw,
                            PeakVolumeRaw = initialEvent.Envelope.PeakVolumeRaw,
                            DecaySpeedRaw = initialEvent.Envelope.DecaySpeedRaw,
                            SustainVolumeRaw = initialEvent.Envelope.SustainVolumeRaw,
                            ReleaseSpeedRaw = initialEvent.Envelope.ReleaseSpeedRaw
                        },
                        PanOperandRaw = initialEvent.PanOperandRaw,
                        RuntimePan = initialEvent.RuntimePan,
                        VolumeOperandRaw = initialEvent.VolumeOperandRaw,
                        NoteValueRaw = initialEvent.NoteValueRaw,
                        NoteKind = initialEvent.NoteKind,
                        LengthRaw = initialEvent.LengthRaw,
                        LengthEncodingByteLength = initialEvent.LengthEncodingByteLength,
                        LengthMode = initialEvent.LengthMode
                    },
                Continuation = continuation == null
                    ? null
                    : new ContinuationManifest
                    {
                        Classification = continuation.Classification,
                        RecognizedByteLength = continuation.RecognizedByteLength,
                        UninterpretedAfterStopOffset = continuation.UninterpretedAfterStopOffset,
                        UninterpretedAfterStopRawHex = uninterpretedAfterStopRawHex
                    }
            };
        }).ToArray();
        var resolvedInitialWaveBindingCount = components.Count(static component =>
            component.InitialWaveBinding != null);
        var resolvedInitialEventCount = components.Count(static component =>
            component.InitialEvent != null);
        var classifiedContinuationCount = components.Count(static component =>
            component.Continuation != null);

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Format = "N64 Sound Tools BFX",
            Magic = null,
            FxBankSource = fxBankSource,
            PointerSource = pointerSource,
            PointerBindingBasis = pointerBindingBasis,
            SerializedSize = bank.SerializedSize,
            ComponentCount = bank.ComponentCount,
            EffectCount = bank.EffectCount,
            LocalWaveCount = bank.LocalWaveCount,
            PointerWaveCount = pointerBank.Waves.Count,
            FlagsRaw = bank.FlagsRaw,
            PointerBankAddressRaw = bank.PointerBankAddressRaw,
            ComponentDataOffset = bank.ComponentDataOffset,
            WaveTableOffset = bank.WaveTableOffset,
            BytecodeStatus = "opaqueBeyondInitialEvent",
            SampleRate = null,
            CueMappingStatus = "unresolved",
            PitchApplicationStatus = "notApplied",
            LoopSchedulingStatus = "notApplied",
            PlaybackStatus = "notExecuted",
            InitialWaveBindingStatus = resolvedInitialWaveBindingCount switch
            {
                0 => "unresolved",
                _ when resolvedInitialWaveBindingCount == components.Length => "resolved",
                _ => "partial"
            },
            ResolvedInitialWaveBindingCount = resolvedInitialWaveBindingCount,
            InitialEventStatus = resolvedInitialEventCount switch
            {
                0 => "unresolved",
                _ when resolvedInitialEventCount == components.Length => "resolved",
                _ => "partial"
            },
            ResolvedInitialEventCount = resolvedInitialEventCount,
            ContinuationClassificationStatus = classifiedContinuationCount switch
            {
                0 => "unresolved",
                _ when classifiedContinuationCount == components.Length => "classified",
                _ => "partial"
            },
            ClassifiedContinuationCount = classifiedContinuationCount,
            ComponentEntryTableSha256 = Hash(bank.ComponentEntryTableRaw),
            OpaqueComponentRegionSha256 = Hash(bank.OpaqueComponentRegionRaw),
            LocalWaveMapSha256 = Hash(bank.LocalWaveMapRaw),
            Components = components,
            LocalWaveMap = bank.LocalWaveMap.Select(static binding => new LocalWaveBindingManifest
            {
                LocalWaveIndex = binding.LocalWaveIndex,
                PointerWaveIndex = binding.PointerWaveIndex
            }).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static string Hash(IEnumerable<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes.ToArray()));

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Format { get; init; }
        public required string? Magic { get; init; }
        public required string FxBankSource { get; init; }
        public required string PointerSource { get; init; }
        public required string PointerBindingBasis { get; init; }
        public required int SerializedSize { get; init; }
        public required int ComponentCount { get; init; }
        public required int EffectCount { get; init; }
        public required int LocalWaveCount { get; init; }
        public required int PointerWaveCount { get; init; }
        public required uint FlagsRaw { get; init; }
        public required uint PointerBankAddressRaw { get; init; }
        public required int ComponentDataOffset { get; init; }
        public required int WaveTableOffset { get; init; }
        public required string BytecodeStatus { get; init; }
        public required int? SampleRate { get; init; }
        public required string CueMappingStatus { get; init; }
        public required string PitchApplicationStatus { get; init; }
        public required string LoopSchedulingStatus { get; init; }
        public required string PlaybackStatus { get; init; }
        public required string InitialWaveBindingStatus { get; init; }
        public required int ResolvedInitialWaveBindingCount { get; init; }
        public required string InitialEventStatus { get; init; }
        public required int ResolvedInitialEventCount { get; init; }
        public required string ContinuationClassificationStatus { get; init; }
        public required int ClassifiedContinuationCount { get; init; }
        public required string ComponentEntryTableSha256 { get; init; }
        public required string OpaqueComponentRegionSha256 { get; init; }
        public required string LocalWaveMapSha256 { get; init; }
        public required ComponentManifest[] Components { get; init; }
        public required LocalWaveBindingManifest[] LocalWaveMap { get; init; }
    }

    private sealed class ComponentManifest
    {
        public required int Index { get; init; }
        public required int FxDataOffset { get; init; }
        public required int ByteLength { get; init; }
        public required int DefaultPriority { get; init; }
        public required string OpaqueDataRawHex { get; init; }
        public required string OpaqueDataSha256 { get; init; }
        public required InitialWaveBindingManifest? InitialWaveBinding { get; init; }
        public required InitialEventManifest? InitialEvent { get; init; }
        public required ContinuationManifest? Continuation { get; init; }
    }

    private sealed class InitialWaveBindingManifest
    {
        public required string Basis { get; init; }
        public required int PrefixByteLength { get; init; }
        public required int LocalWaveIndex { get; init; }
        public required ushort PointerWaveIndex { get; init; }
        public required int PointerDescriptorOffset { get; init; }
    }

    private sealed class InitialEventManifest
    {
        public required string Basis { get; init; }
        public required int EncodedByteLength { get; init; }
        public required int EndOffset { get; init; }
        public required byte? LeadingLoopCountRaw { get; init; }
        public required EnvelopeManifest Envelope { get; init; }
        public required byte PanOperandRaw { get; init; }
        public required byte RuntimePan { get; init; }
        public required byte VolumeOperandRaw { get; init; }
        public required byte NoteValueRaw { get; init; }
        public required string NoteKind { get; init; }
        public required int LengthRaw { get; init; }
        public required int LengthEncodingByteLength { get; init; }
        public required string LengthMode { get; init; }
    }

    private sealed class EnvelopeManifest
    {
        public required byte SpeedRaw { get; init; }
        public required byte InitialVolumeRaw { get; init; }
        public required byte AttackSpeedRaw { get; init; }
        public required byte PeakVolumeRaw { get; init; }
        public required byte DecaySpeedRaw { get; init; }
        public required byte SustainVolumeRaw { get; init; }
        public required byte ReleaseSpeedRaw { get; init; }
    }

    private sealed class ContinuationManifest
    {
        public required string Classification { get; init; }
        public required int RecognizedByteLength { get; init; }
        public required int? UninterpretedAfterStopOffset { get; init; }
        public required string? UninterpretedAfterStopRawHex { get; init; }
    }

    private sealed class LocalWaveBindingManifest
    {
        public required int LocalWaveIndex { get; init; }
        public required ushort PointerWaveIndex { get; init; }
    }
}
