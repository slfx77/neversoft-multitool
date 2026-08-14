using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public class AudioOutputStemPlannerTests
{
    private const string WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    private readonly TestPaths _paths = new();

    [Fact]
    public void Plan_SingletonStemsRemainUnchanged()
    {
        AudioOutputStemInput[] inputs =
        [
            new("CarBrakeSqueal.snd", "Sounds/Shared/CarBrakeSqueal.snd"),
            new("music.vag", "Streams/music.vag")
        ];

        Assert.Equal(["CarBrakeSqueal", "music"], AudioOutputStemPlanner.Plan(inputs));
    }

    [Fact]
    public void Plan_DuplicateStemsUseStableRelativePathHashes()
    {
        AudioOutputStemInput[] inputs =
        [
            new("ExtraTrick.snd", "sounds/Shared/Goals/ExtraTrick.snd"),
            new("extratrick.SND", @"sounds\Skater\ExtraTrick.snd")
        ];

        var forward = AudioOutputStemPlanner.Plan(inputs);
        var reverseInputs = inputs.Reverse().ToArray();
        var reverse = AudioOutputStemPlanner.Plan(reverseInputs);
        var reverseByPath = reverseInputs
            .Select((input, index) => (input.RelativePath, Stem: reverse[index]))
            .ToDictionary(static pair => pair.RelativePath, static pair => pair.Stem);

        Assert.All(forward, static stem => Assert.Matches("^[^/\\\\]+_[0-9a-f]{8}$", stem));
        Assert.Equal(2, new HashSet<string>(forward, StringComparer.OrdinalIgnoreCase).Count);
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(forward[i], reverseByPath[inputs[i].RelativePath]);
    }

    [Fact]
    public void Plan_NormalizedPathHashCollisionUsesDeterministicFallback()
    {
        AudioOutputStemInput[] inputs =
        [
            new("Hit.snd", "Folder/Hit.snd"),
            new("hit.SND", @"folder\.\HIT.snd")
        ];

        var stems = AudioOutputStemPlanner.Plan(inputs);

        Assert.Equal(2, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
        Assert.Contains(stems, static stem => stem.EndsWith("_2", StringComparison.Ordinal));
        Assert.Equal(stems, AudioOutputStemPlanner.Plan(inputs));
    }

    [Fact]
    public void Plan_GeneratedSuffixCannotShadowSingletonStem()
    {
        AudioOutputStemInput[] duplicates =
        [
            new("Hit.snd", "A/Hit.snd"),
            new("Hit.snd", "B/Hit.snd")
        ];
        var generatedCandidate = AudioOutputStemPlanner.Plan(duplicates)[0];
        AudioOutputStemInput[] combined =
        [
            .. duplicates,
            new(generatedCandidate + ".snd", "Singleton/" + generatedCandidate + ".snd")
        ];

        var stems = AudioOutputStemPlanner.Plan(combined);

        Assert.Equal(generatedCandidate, stems[2]);
        Assert.Equal(3, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
    }

    [Fact]
    public void Plan_VidTrackNamespaceCannotShadowAnotherInput()
    {
        AudioOutputStemInput[] inputs =
        [
            new("foo.vid", "Video/foo.vid"),
            new("foo_track1.snd", "Sounds/foo_track1.snd")
        ];

        var stems = AudioOutputStemPlanner.Plan(inputs);

        Assert.Equal(2, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
        Assert.False(
            $"{stems[0]}_track1".Equals(stems[1], StringComparison.OrdinalIgnoreCase),
            "VID track 1 would overwrite the other input's primary WAV");
        Assert.NotEqual("foo", stems[0]);
        Assert.NotEqual("foo_track1", stems[1]);
    }

    [Theory]
    [InlineData("CON.snd", "_CON")]
    [InlineData("aux.vag", "_aux")]
    [InlineData("NUL.sound.snd", "_NUL.sound")]
    [InlineData("COM1.pcm", "_COM1")]
    [InlineData("lpt9.vid", "_lpt9")]
    [InlineData("clock$.snd", "_clock$")]
    [InlineData("COM¹.snd", "_COM¹")]
    [InlineData("lPt².vag", "_lPt²")]
    [InlineData("CoM³.track.snd", "_CoM³.track")]
    public void Plan_WindowsDeviceNamesAreMadeSafe(string fileName, string expectedStem)
    {
        var stems = AudioOutputStemPlanner.Plan([new(fileName, "Sounds/" + fileName)]);

        Assert.Equal([expectedStem], stems);
    }

    [Theory]
    [InlineData("COM0.snd", "COM0")]
    [InlineData("LPT10.vag", "LPT10")]
    [InlineData("COM⁴.snd", "COM⁴")]
    public void Plan_NonDeviceNamesRemainUnchanged(string fileName, string expectedStem)
    {
        var stems = AudioOutputStemPlanner.Plan([new(fileName, "Sounds/" + fileName)]);

        Assert.Equal([expectedStem], stems);
    }

    [Fact]
    public void Plan_LongNamesStayWithinTheWavComponentLimitAndRemainDistinct()
    {
        var sharedPrefix = new string('a', 245);
        AudioOutputStemInput[] inputs =
        [
            new(sharedPrefix + "x.vid", "A/" + sharedPrefix + "x.vid"),
            new(sharedPrefix + "y.vid", "B/" + sharedPrefix + "y.vid")
        ];

        var stems = AudioOutputStemPlanner.Plan(inputs);

        Assert.Equal(2, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
        Assert.All(stems, static stem =>
            Assert.True(
                (stem + "_track2147483647.wav").Length <= 255,
                $"Generated VID track component is too long: {stem.Length} stem characters"));
    }

    [Fact]
    public void Plan_CaseOnlyPathsKeepTheSameFallbackAssignmentWhenReversed()
    {
        AudioOutputStemInput[] forward =
        [
            new("hit.snd", "Archive/A/hit.snd"),
            new("hit.snd", "Archive/a/hit.snd")
        ];
        var reversed = forward.Reverse().ToArray();

        var firstPlan = AudioOutputStemPlanner.Plan(forward);
        var first = forward
            .Select((input, index) => (input.RelativePath, Stem: firstPlan[index]))
            .ToDictionary(static pair => pair.RelativePath, static pair => pair.Stem);
        var secondPlan = AudioOutputStemPlanner.Plan(reversed);
        var second = reversed
            .Select((input, index) => (input.RelativePath, Stem: secondPlan[index]))
            .ToDictionary(static pair => pair.RelativePath, static pair => pair.Stem);

        Assert.Equal(first["Archive/A/hit.snd"], second["Archive/A/hit.snd"]);
        Assert.Equal(first["Archive/a/hit.snd"], second["Archive/a/hit.snd"]);
    }

    [Fact]
    public void Plan_CanonicallyEquivalentStemsReceiveDistinctStableSuffixes()
    {
        AudioOutputStemInput[] inputs =
        [
            new("caf\u00E9.snd", "A/caf\u00E9.snd"),
            new("cafe\u0301.snd", "B/cafe\u0301.snd")
        ];

        var forward = AudioOutputStemPlanner.Plan(inputs);
        var reverseInputs = inputs.Reverse().ToArray();
        var reverse = AudioOutputStemPlanner.Plan(reverseInputs);
        var reverseByPath = reverseInputs
            .Select((input, index) => (input.RelativePath, Stem: reverse[index]))
            .ToDictionary(static pair => pair.RelativePath, static pair => pair.Stem);

        Assert.All(forward, static stem =>
        {
            Assert.True(stem.IsNormalized(NormalizationForm.FormC));
            Assert.Matches("^caf\u00E9_[0-9a-f]{8}$", stem);
        });
        Assert.Equal(2, new HashSet<string>(forward, StringComparer.OrdinalIgnoreCase).Count);
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(forward[i], reverseByPath[inputs[i].RelativePath]);
    }

    [Fact]
    public void Plan_DecomposedSingletonReturnsFormCStem()
    {
        var stems = AudioOutputStemPlanner.Plan(
            [new("cafe\u0301.snd", "Sounds/cafe\u0301.snd")]);

        Assert.Equal(["caf\u00E9"], stems);
        Assert.True(stems[0].IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Plan_StripsPathComponentsAndTraversalFromOutputStems()
    {
        AudioOutputStemInput[] inputs =
        [
            new(@"..\..\name.snd", "../outside/name.snd"),
            new("../../NAME.SND", @"..\elsewhere\NAME.SND")
        ];

        var stems = AudioOutputStemPlanner.Plan(inputs);

        Assert.All(stems, static stem =>
        {
            Assert.DoesNotContain("..", stem, StringComparison.Ordinal);
            Assert.DoesNotContain('/', stem);
            Assert.DoesNotContain('\\', stem);
        });
        Assert.Equal(2, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
    }

    [CorpusFact]
    public void PlanAndConvert_RealExtraTrickPairWritesTwoDistinctWavs()
    {
        Assert.SkipWhen(_paths.SampleBuildsDir is null, "Sample/Builds is not available");
        Assert.SkipWhen(_paths.TestOutputDir is null, "TestOutput is not available");
        var buildDir = Path.Combine(_paths.SampleBuildsDir!, WindowsBuild);
        var files = Directory.Exists(buildDir)
            ? Directory.EnumerateFiles(buildDir, "ExtraTrick.snd", SearchOption.AllDirectories).ToList()
            : [];
        Assert.SkipWhen(files.Count == 0, "ExtraTrick.snd fixtures are not present in Sample/Builds");
        Assert.Equal(2, files.Count);

        var inputs = files
            .Select(path => new AudioOutputStemInput(
                Path.GetFileName(path),
                Path.GetRelativePath(buildDir, path)))
            .ToList();
        var stems = AudioOutputStemPlanner.Plan(inputs);
        Assert.Equal(2, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);

        var outputDir = Path.Combine(
            _paths.TestOutputDir!,
            "audio-output-stem-planner-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var result = Thug2PcSndDecoder.ConvertToWav(
                    File.ReadAllBytes(files[i]),
                    stems[i],
                    outputDir);
                Assert.True(result.Success, result.ErrorMessage);
            }

            var wavs = Directory.GetFiles(outputDir, "*.wav");
            Assert.Equal(2, wavs.Length);
            Assert.Equal(2, wavs
                .Select(static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                .Distinct(StringComparer.Ordinal)
                .Count());
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }
}
