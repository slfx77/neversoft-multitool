using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Writes a deterministic inspection manifest for one BFX bank bound to a validated PTR bank.</summary>
internal static class N64SoundToolsFxBankJsonExporter
{
    internal const string SchemaName = "neversoft.n64.soundToolsFxBank";
    internal const int CurrentSchemaVersion = 1;

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
            BytecodeStatus = "opaque",
            SampleRate = null,
            CueMappingStatus = "unresolved",
            ComponentEntryTableSha256 = Hash(bank.ComponentEntryTableRaw),
            OpaqueComponentRegionSha256 = Hash(bank.OpaqueComponentRegionRaw),
            LocalWaveMapSha256 = Hash(bank.LocalWaveMapRaw),
            Components = bank.Components.Select(static component => new ComponentManifest
            {
                Index = component.Index,
                FxDataOffset = component.FxDataOffset,
                ByteLength = component.OpaqueData.Count,
                DefaultPriority = component.DefaultPriority,
                OpaqueDataRawHex = Convert.ToHexString(component.OpaqueData.ToArray()),
                OpaqueDataSha256 = Hash(component.OpaqueData)
            }).ToArray(),
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
    }

    private sealed class LocalWaveBindingManifest
    {
        public required int LocalWaveIndex { get; init; }
        public required ushort PointerWaveIndex { get; init; }
    }
}
