using System.Text;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Complete late-PS1 two-player bank audit (2026-08-09). THPS3's Shaba
///     port added a third reduced-bank spelling, <c>aa&lt;stem&gt;2o.psx</c>.
///     Six such entries were still hash-named in extracted trees, so companion
///     resolution missed the bank selected by AUTOEXEC2 and fell back to the
///     full one-player <c>_o</c> bank. The table below pins every shipped
///     <c>*_2.psx</c> in both late ports against the shared TRG's actual boot
///     scripts and the selected bank's parsed geometry.
/// </summary>
public sealed class LatePs1VariantBankTests(TestPaths paths)
{
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)";
    private const string Thps4Build = "Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)";

    private static readonly VariantExpectation[] Expected =
    [
        new(Thps3Build, "aaair_2.psx", "AAAir_O", "AAAir2o", true, 4_784, 4, 93),
        new(Thps3Build, "aaburb_2.psx", "AAburb_O", "aaburb2o", true, 40_760, 1, 109),
        new(Thps3Build, "aacana_2.psx", "AACana_O", "AACana_O", false, 18_904, 8, 335),
        new(Thps3Build, "aadnhl_2.psx", "AADnhl_O", "aaDnhl2o", true, 36, 0, 0),
        new(Thps3Build, "aafoun_2.psx", "AAFoun_O", "aaFoun2O", true, 880, 1, 13),
        new(Thps3Build, "aala_2.psx", "AALA_O", "AALA2O", true, 4_368, 1, 98),
        new(Thps3Build, "aario_2.psx", "AARio_O", "AARio_O", true, 16_720, 5, 344),
        new(Thps3Build, "aaskil_2.psx", "AAskil_O", "AAskil2o", true, 180, 1, 1),
        new(Thps3Build, "skny_2.psx", "SkNY_O", "SkNY_O2", true, 3_976, 2, 19),
        new(Thps4Build, "skny_2.psx", "SkNY_O", "SkNY_O2", true, 3_976, 2, 19)
    ];

    [CorpusFact]
    public void EveryLatePs1TwoPlayerVariant_UsesItsBootScriptBank()
    {
        foreach (var build in new[] { Thps3Build, Thps4Build })
        {
            var shipped = paths.FindSampleFiles(build, "*_2.psx")
                .Select(Path.GetFileName)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.SkipWhen(shipped.Length == 0, $"{build} two-player samples not available");

            var expected = Expected
                .Where(row => row.Build == build)
                .Select(static row => row.Variant)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(expected, shipped);
        }

        foreach (var row in Expected)
        {
            var path = paths.FindSampleFile(row.Build, row.Variant);
            Assert.NotNull(path);

            var source = new FileSystemAssetSource(path!);
            var levelStem = Path.GetFileNameWithoutExtension(row.Variant)[..^2];
            var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(source, levelStem);
            Assert.NotNull(trg);

            Assert.Equal(
                row.HasAutoexec2,
                trg!.Nodes.Any(static node => node.TypeId == TrgNodeMetadata.TypeAutoexec2));
            Assert.True(PsxTrgBootScript.TryResolveBank(trg, false, out var onePlayer));
            Assert.Equal(row.OnePlayerBank, onePlayer.BankName);
            Assert.True(PsxTrgBootScript.TryResolveBank(trg, true, out var twoPlayer));
            Assert.Equal(row.TwoPlayerBank, twoPlayer.BankName);

            Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
                source, row.Variant, out var companions));
            Assert.Equal(levelStem, companions.LevelStem);
            Assert.True(companions.ApplyTriggerOverlay);

            var logicalBank = (row.TwoPlayerBank + ".psx").ToLowerInvariant();
            var hash = BinaryReaderExtensions.Crc32Neversoft(Encoding.ASCII.GetBytes(logicalBank));
            var hashAlias = $"{hash:X8}.dat";
            Assert.True(
                companions.BankCompanionName.Equals(logicalBank, StringComparison.OrdinalIgnoreCase)
                || companions.BankCompanionName.Equals(hashAlias, StringComparison.OrdinalIgnoreCase),
                $"{row.Build} {row.Variant} resolved {companions.BankCompanionName}; " +
                $"AUTOEXEC2 selected {logicalBank} ({hashAlias})");

            var bytes = source.TryReadCompanion(companions.BankCompanionName);
            Assert.NotNull(bytes);
            Assert.Equal(row.Bytes, bytes!.Length);
            var bank = PsxMeshFile.Parse(bytes!);
            if (row.Objects == 0)
            {
                Assert.Null(bank);
                continue;
            }

            Assert.NotNull(bank);
            Assert.Equal(row.Objects, bank!.Objects.Count);
            Assert.Equal(row.Faces, bank.Meshes.Sum(static mesh => mesh.Faces.Count));
        }
    }

    [CorpusFact]
    public void Thps3HashedHed_NamesEveryReducedAutoexec2Bank()
    {
        var wadPath = paths.FindSampleFile(Thps3Build, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "THPS3 PS1 CD.WAD sample not available");

        var entries = WadArchive.GetFileList(wadPath!);
        string[] reducedBanks =
        [
            "aaair2o.psx",
            "aaburb2o.psx",
            "aadnhl2o.psx",
            "aafoun2o.psx",
            "aala2o.psx",
            "aaskil2o.psx"
        ];

        foreach (var bankName in reducedBanks)
        {
            var expectedHash = BinaryReaderExtensions.Crc32Neversoft(
                Encoding.ASCII.GetBytes(bankName));
            Assert.Contains(entries, entry =>
                entry.Crc == expectedHash
                && entry.Name.Equals(bankName, StringComparison.Ordinal));
        }
    }

    private sealed record VariantExpectation(
        string Build,
        string Variant,
        string OnePlayerBank,
        string TwoPlayerBank,
        bool HasAutoexec2,
        int Bytes,
        int Objects,
        int Faces);
}
