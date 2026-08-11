using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.CLI;

internal static class N64SoundToolsInputResolver
{
    internal static N64SoundToolsInputSources Resolve(string input, string? wavePath)
    {
        var classification = N64RomArchive.ClassifyRom(input);
        if (classification == "N64 ROM")
        {
            if (!string.IsNullOrWhiteSpace(wavePath))
                throw new InvalidDataException("--wave is not accepted for a ROM input; the unique carved PTR/WBK pair is used");

            var rom = File.ReadAllBytes(input);
            if (!N64AssetCarver.TryCarve(rom, out var assets))
                throw new InvalidDataException("the ROM has no supported Edge of Reality master asset directory");
            return SelectCarvedPair(assets);
        }

        if (classification is not null)
            throw new InvalidDataException(classification);
        if (string.IsNullOrWhiteSpace(wavePath))
            throw new InvalidDataException("a standalone PTR input requires an explicit --wave WBK path");
        if (!File.Exists(wavePath))
            throw new FileNotFoundException("paired WBK file not found", wavePath);

        return new N64SoundToolsInputSources(
            File.ReadAllBytes(input),
            File.ReadAllBytes(wavePath),
            Path.GetFileName(input),
            Path.GetFileName(wavePath));
    }

    internal static N64SoundToolsInputSources SelectCarvedPair(
        IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
    {
        var pointerAssets = assets.Where(static asset =>
            N64SoundToolsBank.HasPointerMagic(asset.Data)).ToArray();
        var waveAssets = assets.Where(static asset =>
            N64SoundToolsBank.HasWaveMagic(asset.Data)).ToArray();
        if (pointerAssets.Length != 1 || waveAssets.Length != 1)
        {
            throw new InvalidDataException(
                $"ROM carve must contain exactly one Sound Tools PTR and one WBK; found " +
                $"{pointerAssets.Length} PTR and {waveAssets.Length} WBK");
        }

        return new N64SoundToolsInputSources(
            pointerAssets[0].Data,
            waveAssets[0].Data,
            Path.GetFileName(pointerAssets[0].Path),
            Path.GetFileName(waveAssets[0].Path));
    }
}

internal sealed record N64SoundToolsInputSources(
    byte[] PointerData,
    byte[] WaveData,
    string PointerSource,
    string WaveSource);
