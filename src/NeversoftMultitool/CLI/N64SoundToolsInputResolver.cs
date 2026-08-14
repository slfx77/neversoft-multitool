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
                N64SoundToolsBank.HasPointerMagic(asset.Data))
            .Where(static asset => IsValidPointer(asset.Data))
            .ToArray();
        if (pointerAssets.Length != 1)
        {
            throw new InvalidDataException(
                $"ROM carve must contain exactly one structurally valid Sound Tools PTR; found " +
                $"{pointerAssets.Length} PTR");
        }

        var pointerAsset = pointerAssets[0];
        var waveAssets = assets.Where(static asset =>
                N64SoundToolsBank.HasWaveMagic(asset.Data))
            .Where(asset => IsValidPair(pointerAsset.Data, asset.Data))
            .ToArray();
        if (waveAssets.Length != 1)
        {
            throw new InvalidDataException(
                $"ROM carve must contain exactly one structurally valid Sound Tools WBK; found " +
                $"{waveAssets.Length} WBK");
        }

        var waveAsset = waveAssets[0];
        return new N64SoundToolsInputSources(
            pointerAsset.Data,
            waveAsset.Data,
            Path.GetFileName(pointerAsset.Path),
            Path.GetFileName(waveAsset.Path));
    }

    private static bool IsValidPointer(ReadOnlySpan<byte> data)
    {
        try
        {
            N64SoundToolsBank.ParsePointer(data);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsValidPair(ReadOnlySpan<byte> pointerData, ReadOnlySpan<byte> waveData)
    {
        try
        {
            N64SoundToolsBank.Parse(pointerData, waveData);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

internal sealed record N64SoundToolsInputSources(
    byte[] PointerData,
    byte[] WaveData,
    string PointerSource,
    string WaveSource);
