using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64SfxInspectCommandTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string Thps1N64Rom = "Tony Hawk's Pro Skater (USA).z64";
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2N64Rom = "Tony Hawk's Pro Skater 2 (USA).z64";

    public static TheoryData<string, string, int, int> RomManifestExpectations() => new()
    {
        { Thps1N64Build, Thps1N64Rom, 0, 0 },
        { Thps2N64Build, Thps2N64Rom, 14, 671 }
    };

    [Fact]
    public void Resolver_ScansEveryAssetStrictlyAndSortsFullPathsOrdinal()
    {
        var suffixed = BuildBank(loopFlag: 0x00, note: 0x20);
        var misclassifiedBin = BuildBank(loopFlag: 0xFE, note: 0xFF);
        var malformedSuffix = BuildBank(loopFlag: 0x00, note: 0x20);
        malformedSuffix[12] = 1;
        N64AssetCarver.CarvedAsset[] assets =
        [
            new("sfx/010.sfx.n64", suffixed),
            new("misc/not-a-cue.bin", [1, 2, 3]),
            new("sfx/002.bin", misclassifiedBin),
            new("sfx/001.sfx.n64", malformedSuffix)
        ];

        var banks = N64SfxInspectCommand.SelectCarvedBanks(assets);

        Assert.Equal(["sfx/002.bin", "sfx/010.sfx.n64"],
            banks.Select(static bank => bank.Source));
        Assert.Equal(0xFE, banks[0].Bank.Records[0].LoopFlagRaw);
        Assert.Equal(0xFF, banks[0].Bank.Records[0].NoteRaw);
    }

    [Fact]
    public void Command_StandaloneWritesOneBankAggregateAndRejectsPairingOrPlaybackOptions()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cue.sfx.n64");
        var output = Path.Combine(temp.Path, "nested", "manifest.json");
        File.WriteAllBytes(input, BuildBank(loopFlag: 0xFE, note: 0x80));

        var command = N64SfxInspectCommand.Create();
        Assert.Equal("n64-sfx-inspect", command.Name);
        Assert.Equal(0, command.Parse([input, "-o", output]).Invoke());

        using (var json = JsonDocument.Parse(File.ReadAllText(output)))
        {
            var root = json.RootElement;
            Assert.Equal("cue.sfx.n64", root.GetProperty("inputSource").GetString());
            Assert.Equal("explicitFile", root.GetProperty("selectionBasis").GetString());
            Assert.Equal(1, root.GetProperty("bankCount").GetInt32());
            Assert.Equal(1, root.GetProperty("recordCount").GetInt32());
            var bank = Assert.Single(root.GetProperty("banks").EnumerateArray());
            Assert.Equal("cue.sfx.n64", bank.GetProperty("source").GetString());
            Assert.Equal(0x80, bank.GetProperty("records")[0].GetProperty("noteRaw").GetInt32());
        }

        foreach (var option in new[] { "--pointer", "--wave", "--sample-rate", "--target" })
        {
            var forbiddenOutput = Path.Combine(temp.Path, $"forbidden-{option[2..]}.json");
            Assert.NotEqual(0, N64SfxInspectCommand.Create()
                .Parse([input, option, "value", "-o", forbiddenOutput])
                .Invoke());
            Assert.False(File.Exists(forbiddenOutput));
        }

    }

    [Fact]
    public void Command_MalformedInputLeavesAbsentAndExistingDestinationsUntouched()
    {
        using var temp = new TempDirectory();
        var malformed = Path.Combine(temp.Path, "malformed.sfx.n64");
        var badData = BuildBank(loopFlag: 0x00, note: 0x20);
        badData[^1] = 0;
        File.WriteAllBytes(malformed, badData);

        var absent = Path.Combine(temp.Path, "absent", "manifest.json");
        Assert.Equal(1, N64SfxInspectCommand.Execute(malformed, absent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));

        var existing = Path.Combine(temp.Path, "existing.json");
        const string sentinel = "existing output must survive";
        File.WriteAllText(existing, sentinel);
        Assert.Equal(1, N64SfxInspectCommand.Execute(malformed, existing));
        Assert.Equal(sentinel, File.ReadAllText(existing));

        var missingOutput = Path.Combine(temp.Path, "missing.json");
        Assert.Equal(1, N64SfxInspectCommand.Execute(Path.Combine(temp.Path, "missing.sfx.n64"), missingOutput));
        Assert.False(File.Exists(missingOutput));
    }

    [Fact]
    public void ProgramRoute_RegistersCommandHelp()
    {
        Assert.Equal(0, Program.Main(["n64-sfx-inspect", "--help"]));
    }

    [CorpusTheory]
    [MemberData(nameof(RomManifestExpectations))]
    public void Command_RomWritesOneAggregateIncludingZeroBankAndMisclassifiedBinCases(
        string build,
        string rom,
        int expectedBankCount,
        int expectedRecordCount)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "manifest.json");

        Assert.Equal(0, N64SfxInspectCommand.Execute(romPath!, output));

        using var json = JsonDocument.Parse(File.ReadAllText(output));
        var root = json.RootElement;
        Assert.Equal(rom, root.GetProperty("inputSource").GetString());
        Assert.Equal("strictRomStructuralScan", root.GetProperty("selectionBasis").GetString());
        Assert.Equal(expectedBankCount, root.GetProperty("bankCount").GetInt32());
        Assert.Equal(expectedRecordCount, root.GetProperty("recordCount").GetInt32());
        var bankPaths = root.GetProperty("banks").EnumerateArray()
            .Select(static bank => bank.GetProperty("source").GetString()!)
            .ToArray();
        Assert.Equal(bankPaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(), bankPaths);

        if (rom == Thps1N64Rom)
        {
            Assert.Empty(bankPaths);
            return;
        }

        Assert.Contains("sfx/001.bin", bankPaths);
        Assert.Contains("sfx/003.bin", bankPaths);
    }

    private static byte[] BuildBank(byte loopFlag, byte note)
    {
        var data = new byte[20];
        data[0] = loopFlag;
        data[1] = 1;
        data[2] = 2;
        data[3] = note;
        data.AsSpan(16).Fill(0xFF);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-sfx-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
