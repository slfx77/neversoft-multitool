using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.CLI;

internal static class N64SoundToolsFxInputResolver
{
    internal const string CallerSuppliedBinding = "callerSupplied";
    internal const string RomUniqueSingletonBinding = "romUniqueSingleton";

    internal static N64SoundToolsFxInputSources Resolve(string input, string? pointerPath)
    {
        var classification = N64RomArchive.ClassifyRom(input);
        if (classification == "N64 ROM")
        {
            if (!string.IsNullOrWhiteSpace(pointerPath))
                throw new InvalidDataException("--pointer is not accepted for a ROM input; the unique carved PTR/BFX binding is used");

            var rom = File.ReadAllBytes(input);
            if (!N64AssetCarver.TryCarve(rom, out var assets))
                throw new InvalidDataException("the ROM has no supported Edge of Reality master asset directory");
            return SelectCarvedSources(assets);
        }

        if (classification is not null)
            throw new InvalidDataException(classification);
        if (string.IsNullOrWhiteSpace(pointerPath))
            throw new InvalidDataException("a standalone BFX input requires an explicit --pointer PTR path");
        if (!File.Exists(pointerPath))
            throw new FileNotFoundException("paired PTR file not found", pointerPath);

        var fxBankData = File.ReadAllBytes(input);
        var pointerData = File.ReadAllBytes(pointerPath);
        var pointerBank = N64SoundToolsBank.ParsePointer(pointerData);
        var fxBank = N64SoundToolsFxBank.Parse(fxBankData, pointerBank);
        return new N64SoundToolsFxInputSources(
            fxBankData,
            pointerData,
            Path.GetFileName(input),
            Path.GetFileName(pointerPath),
            CallerSuppliedBinding,
            fxBank,
            pointerBank);
    }

    internal static N64SoundToolsFxInputSources SelectCarvedSources(
        IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
    {
        var pointerAssets = assets.Where(static asset =>
            N64SoundToolsBank.HasPointerMagic(asset.Data)).ToArray();
        if (pointerAssets.Length != 1)
        {
            throw new InvalidDataException(
                $"ROM carve must contain exactly one Sound Tools PTR; found {pointerAssets.Length}");
        }

        var pointerAsset = pointerAssets[0];
        var pointerBank = N64SoundToolsBank.ParsePointer(pointerAsset.Data);
        var candidates = new List<(N64AssetCarver.CarvedAsset Asset, N64SoundToolsFxBank Bank)>();
        foreach (var asset in assets)
        {
            if (N64SoundToolsFxBank.TryParse(asset.Data, pointerBank, out var fxBank))
                candidates.Add((asset, fxBank!));
        }

        if (candidates.Count != 1)
        {
            throw new InvalidDataException(
                $"ROM carve must contain exactly one structurally valid Sound Tools BFX; found {candidates.Count}");
        }

        var candidate = candidates[0];
        return new N64SoundToolsFxInputSources(
            candidate.Asset.Data,
            pointerAsset.Data,
            Path.GetFileName(candidate.Asset.Path),
            Path.GetFileName(pointerAsset.Path),
            RomUniqueSingletonBinding,
            candidate.Bank,
            pointerBank);
    }
}

internal sealed record N64SoundToolsFxInputSources(
    byte[] FxBankData,
    byte[] PointerData,
    string FxBankSource,
    string PointerSource,
    string PointerBindingBasis,
    N64SoundToolsFxBank FxBank,
    N64SoundToolsPointerBank PointerBank);
