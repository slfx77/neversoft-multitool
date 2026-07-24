using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Finds the runtime claw model instanced at the tips of Ock's tentacle
///     splines. Retail Spider-Man ships it as a sibling <c>claw.psx</c>; the
///     February 2000 prototypes ship the same mesh (name hash 0xCAB96557)
///     inside the boss arena's level-object bank (<c>l8a4_o.psx</c>) instead.
///     A bank hit is repackaged as a single-mesh claw file so the writer's
///     claw path works unchanged; its textures resolve through the bank's own
///     companion library.
/// </summary>
internal static class PsxSplineClawLocator
{
    internal const uint ClawMeshHash = 0xCAB96557;

    internal sealed record ResolvedClaw(
        PsxMeshFile File,
        MeshChecksumTextureResolver? TextureProvider);

    internal static ResolvedClaw? Locate(AssetSource characterSource)
    {
        if (characterSource.TryReadCompanion("claw.psx") is { } clawBytes)
        {
            var clawFile = TryParse(clawBytes, bakeColourPulses: true);
            if (clawFile != null)
            {
                // The retail claw is a complete sibling PSX with its own
                // embedded texture slots; resolve them against the claw bytes,
                // not the Ock character that instances it.
                return new ResolvedClaw(
                    clawFile,
                    MeshCompanionResolver.BuildPsxTextureProvider(
                        characterSource, "claw.psx", clawBytes));
            }
        }

        foreach (var bankSource in EnumerateSiblingObjectBanks(characterSource))
        {
            byte[] bankBytes;
            PsxMeshFile? header;
            try
            {
                bankBytes = bankSource.ReadBytes();
                header = PsxMeshFile.ParseHeaderOnly(bankBytes);
            }
            catch
            {
                continue;
            }

            if (header == null || !header.MeshNameHashes.Contains(ClawMeshHash))
                continue;

            // Bank semantics: keep the raw serialized palette (see
            // PsxSurfaceAnimationReader — pulses don't bake for _o banks).
            var bank = TryParse(bankBytes, bakeColourPulses: false);
            if (bank == null)
                continue;

            var meshIndex = Array.IndexOf(bank.MeshNameHashes, ClawMeshHash);
            if (meshIndex < 0 || meshIndex >= bank.Meshes.Count)
                continue;

            return new ResolvedClaw(
                CreateSingleMeshClawFile(bank, meshIndex),
                MeshCompanionResolver.BuildPsxTextureProvider(
                    bankSource, bankSource.EntryName, bankBytes));
        }

        return null;
    }

    /// <summary>
    ///     Repackages one bank mesh as a standalone claw file. The bank
    ///     object's stored world position is deliberately dropped: the writer
    ///     places claw vertices through the spline tip transform, so the claw
    ///     must stay in local space (as the retail claw.psx ships it).
    /// </summary>
    private static PsxMeshFile CreateSingleMeshClawFile(PsxMeshFile bank, int meshIndex)
    {
        return new PsxMeshFile
        {
            Version = bank.Version,
            FormatRevision = bank.FormatRevision,
            Objects = [new PsxMeshObject { MeshIndex = 0 }],
            Meshes = [bank.Meshes[meshIndex]],
            MeshNameHashes = [ClawMeshHash],
            TextureHashes = bank.TextureHashes,
            GouraudPalette = bank.GouraudPalette,
            ScaleDivisor = bank.ScaleDivisor,
            TranslationDivisor = bank.TranslationDivisor
        };
    }

    private static IEnumerable<AssetSource> EnumerateSiblingObjectBanks(
        AssetSource characterSource)
    {
        if (characterSource.FileSystemPath is { } fsPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(fsPath));
            if (directory == null)
                yield break;

            foreach (var path in Directory.EnumerateFiles(directory, "*_o.psx")
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return new FileSystemAssetSource(path);
            }

            yield break;
        }

        if (characterSource is not ArchiveAssetSource archiveSource)
            yield break;

        foreach (var entry in archiveSource.Backend.Entries
                     .Where(static entry => entry.Name.EndsWith(
                         "_o.psx", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ArchiveAssetSource(archiveSource.Backend, entry);
        }
    }

    private static PsxMeshFile? TryParse(byte[] bytes, bool bakeColourPulses)
    {
        try
        {
            return PsxMeshFile.Parse(bytes, bakeColourPulses);
        }
        catch
        {
            return null;
        }
    }
}
