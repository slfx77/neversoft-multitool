using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     THPS1/THPS2 mode-variant regions (<c>&lt;base&gt;_2</c> two player,
///     <c>&lt;base&gt;_h</c> H-O-R-S-E) ship no companions under their own stem:
///     the SHARED <c>&lt;base&gt;_t.trg</c> spools them and the bank is the
///     reduced <c>&lt;base&gt;_o2.psx</c> / squeezed <c>&lt;base&gt;o2.psx</c>,
///     falling back to the one-player <c>&lt;base&gt;_o.psx</c>. Added 2026-08-03
///     — before this rule every variant converted with zero bank objects, zero
///     pickups, and no sky classification.
/// </summary>
public sealed class ThpsVariantLevelCompanionTests(TestPaths paths)
{
    private const string Thps1Final = "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";

    [Theory]
    // THPS1 final: 8.3-squeezed reduced bank (skmall_o2 was too long).
    [InlineData("skmall_2.psx", "skmall", "skmallo2.psx",
        new[] { "skmallo2.psx", "skmall_o.psx", "skmall_t.trg" })]
    // Underscore spelling (THPS2 and THPS1 sksf) wins over the 1P fallback.
    [InlineData("sksf_2.psx", "sksf", "sksf_o2.psx",
        new[] { "sksf_o2.psx", "sksf_o.psx", "sksf_t.trg" })]
    // No reduced bank shipped: fall back to the one-player bank (skburn_t's
    // AUTOEXEC2 sets SkBurn_O).
    [InlineData("skburn_2.psx", "skburn", "skburn_o.psx",
        new[] { "skburn_o.psx", "skburn_t.trg" })]
    // HORSE regions always run the one-player AUTOEXEC bank, even when a
    // two-player o2 bank ships (skros_t's AUTOEXEC sets SkRos_O).
    [InlineData("skros_h.psx", "skros", "skros_o.psx",
        new[] { "skroso2.psx", "skros_o.psx", "skros_t.trg" })]
    public void Companions_ThpsModeVariants_ResolveSharedTriggerAndReducedBank(
        string fileName,
        string expectedStem,
        string expectedBank,
        string[] siblings)
    {
        Assert.True(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new SyntheticCompanionSource(siblings), fileName, out var companions));
        Assert.Equal(expectedStem, companions.LevelStem);
        Assert.Equal(expectedBank, companions.BankCompanionName);
        Assert.True(companions.ApplyTriggerOverlay);
    }

    [Theory]
    // Without the shared base trigger nothing resolves (a lone o2 bank is not
    // enough — e.g. an unrelated file that merely ends in _2).
    [InlineData("skmall_2.psx", new[] { "skmallo2.psx", "skmall_o.psx" })]
    // With the trigger but no bank of any spelling, the variant branch defers
    // (Apocalypse chunks like city_2.psx take this path and stay rejected).
    [InlineData("city_2.psx", new[] { "city_obj.psx", "city_t.trg" })]
    public void Companions_ThpsModeVariants_RejectIncompleteSiblingSets(
        string fileName,
        string[] siblings)
    {
        Assert.False(MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new SyntheticCompanionSource(siblings), fileName, out _));
    }

    [Fact]
    public void SkMall2_Conversion_GainsBankObjectsAndPickups()
    {
        var path = paths.FindSampleFile(Thps1Final, "skmall_2.psx");
        Assert.SkipWhen(path == null, "THPS1 final skmall_2.psx sample not available");

        var document = ParseDocument(path!);

        // 6,951 authored triangles + the skmallo2.psx bank layer + the 20
        // POWERUP pickups + the secret tape (414c7cd). Overlay/lift changes may
        // move node/mesh counts but never triangles.
        Assert.Equal(8_155, document.TriangleCount);
        Assert.Contains(document.Meshes, static mesh =>
            mesh.Name.StartsWith("obj_cafe_chair01", StringComparison.Ordinal));
        Assert.Contains(document.Meshes, static mesh =>
            mesh.Name.StartsWith("itm_", StringComparison.Ordinal));
    }

    [Fact]
    public void SkSf2_Conversion_GainsTheSkyClassification()
    {
        // sksf_o2.psx carries the level's TRG-registered background layer, so
        // the two-player region now classifies a camera-locked sky (skmall's
        // TRG registers no background — its variant correctly stays sky-less).
        var path = paths.FindSampleFile(Thps1Final, "sksf_2.psx");
        Assert.SkipWhen(path == null, "THPS1 final sksf_2.psx sample not available");

        var document = ParseDocument(path!);

        Assert.Contains(document.Meshes, static mesh =>
            mesh.Name.StartsWith("sky__", StringComparison.Ordinal));
    }

    [CorpusTheory]
    // Every *_2.psx / *_h.psx region in the four THPS PSX/DC builds resolves
    // its shared trigger, and its bank is whatever the TRG boot script names
    // (counts measured 2026-08-03; the enumeration matches the diagnosis'
    // 47-file blast radius).
    //
    // Re-scoped 2026-08-04: this used to assert that EVERY variant resolves an
    // existing bank, which pinned the filename guess it was written against.
    // Nine of these regions ship an AUTOEXEC2 that deliberately names no bank
    // (skdown_2/_h, skbul_2, skmar_2, skven_2 across the builds), so "no bank"
    // is now a correct outcome and the assertion is that a NAMED bank exists —
    // a bank we invent for a region that never loads one is the defect this
    // sweep should catch.
    [InlineData("Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)", 4)]
    [InlineData(Thps1Final, 11)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)", 1)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", 21)]
    [InlineData("Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)", 10)]
    public void Companions_ThpsVariantSweep_ResolvesEveryShippedVariant(
        string buildName,
        int expectedVariants)
    {
        var variants = paths.FindSampleFiles(buildName, "*_2.psx")
            .Concat(paths.FindSampleFiles(buildName, "*_2.PSX"))
            .Concat(paths.FindSampleFiles(buildName, "*_h.psx"))
            .Concat(paths.FindSampleFiles(buildName, "*_h.PSX"))
            .DistinctBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.SkipWhen(variants.Length == 0, $"{buildName} variant samples not available");

        Assert.Equal(expectedVariants, variants.Length);
        Assert.All(variants, file =>
        {
            var source = new FileSystemAssetSource(file);
            Assert.True(
                MeshCompanionResolver.TryResolvePsxLevelCompanions(
                    source, Path.GetFileName(file), out var companions),
                $"{Path.GetFileName(file)} did not resolve");
            if (companions.BankCompanionName.Length == 0)
                return;

            Assert.True(
                source.CompanionExists(companions.BankCompanionName),
                $"{Path.GetFileName(file)} resolved missing bank {companions.BankCompanionName}");
        });

        // The bankless regions are named, not merely tolerated: a build that
        // silently stopped resolving banks everywhere would otherwise pass.
        var bankless = variants
            .Where(file => MeshCompanionResolver.TryResolvePsxLevelCompanions(
                       new FileSystemAssetSource(file), Path.GetFileName(file), out var companions)
                   && companions.BankCompanionName.Length == 0)
            .Select(static file => Path.GetFileNameWithoutExtension(file).ToLowerInvariant())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedBanklessVariants(buildName), bankless);
    }

    /// <summary>
    ///     The regions whose boot script names no object bank, per build —
    ///     transcribed from the shipped TRGs, not from this tool's output
    ///     (<c>tools/diagnostics/psx_variant_bank_report.py</c> lists them).
    /// </summary>
    private static string[] ExpectedBanklessVariants(string buildName)
    {
        return buildName switch
        {
            Thps1Final => ["skdown_2", "skdown_h"],
            "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)" => ["skmar_2"],
            "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)" =>
                ["skbul_2", "skdown_2", "skdown_h", "skmar_2", "skven_2"],
            "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)" =>
                ["skbul_2", "skmar_2", "skven_2"],
            // The 1999-4-9 prototype predates the reduced two-player banks: all
            // four of its variants run the one-player AUTOEXEC bank.
            _ => []
        };
    }

    private static ModelDocument ParseDocument(string path)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path),
            FileName = Path.GetFileName(path),
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = true
        });
    }

    private sealed class SyntheticCompanionSource(params string[] companionNames) : AssetSource
    {
        public override string DisplayName => "synthetic";
        public override string EntryName => "variant.psx";

        public override byte[] ReadBytes()
        {
            return [];
        }

        public override bool CompanionExists(string nameWithExtension)
        {
            return companionNames.Any(name =>
                string.Equals(name, nameWithExtension, StringComparison.OrdinalIgnoreCase));
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            return null;
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            return null;
        }
    }
}
