using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class VideoOutputStemPlannerTests
{
    [Fact]
    public void Plan_UniqueRecursiveAndCompoundInputsKeepNaturalStems()
    {
        VideoOutputStemInput[] inputs =
        [
            new("credits.bik.xen", "movies/credits.bik.xen"),
            new("intro.vid", "movies/intro.vid"),
            new("attract.str", "movies/attract.str")
        ];

        Assert.Equal(["credits", "intro", "attract"], VideoOutputStemPlanner.Plan(inputs));
    }

    [Fact]
    public void Plan_RecursiveCollisionsUseStableSourceRelativeSuffixes()
    {
        VideoOutputStemInput[] forward =
        [
            new("intro.sfd", "language/en/intro.sfd"),
            new("INTRO.BIK", @"language\fr\INTRO.BIK")
        ];
        var reverseInputs = forward.Reverse().ToArray();

        var first = VideoOutputStemPlanner.Plan(forward);
        var reverse = VideoOutputStemPlanner.Plan(reverseInputs);
        var reverseByIdentity = reverseInputs
            .Select((input, index) => (input.RelativePath, Stem: reverse[index]))
            .ToDictionary(static pair => pair.RelativePath, static pair => pair.Stem);

        Assert.All(first, static stem => Assert.Matches("^intro_[0-9a-f]{8}$", stem.ToLowerInvariant()));
        Assert.Equal(2, new HashSet<string>(first, StringComparer.OrdinalIgnoreCase).Count);
        for (var i = 0; i < forward.Length; i++)
            Assert.Equal(first[i], reverseByIdentity[forward[i].RelativePath]);
    }

    [Fact]
    public void Plan_ArchiveEntryCollisionsUseFullEntryIdentity()
    {
        VideoOutputStemInput[] inputs =
        [
            new("movie.tgr", "english/movies/movie.tgr"),
            new("movie.bik", "french/movies/movie.bik"),
            new("movie.bik.xen", "bonus/movie.bik.xen")
        ];

        var stems = VideoOutputStemPlanner.Plan(inputs);

        Assert.Equal(3, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
        Assert.All(stems, static stem => Assert.Matches("^movie_[0-9a-f]{8}$", stem));
    }

    [Fact]
    public void Plan_GeneratedSuffixCannotShadowCaseInsensitiveSingleton()
    {
        VideoOutputStemInput[] duplicates =
        [
            new("clip.sfd", "a/clip.sfd"),
            new("clip.bik", "b/clip.bik")
        ];
        var generated = VideoOutputStemPlanner.Plan(duplicates)[0];
        VideoOutputStemInput[] combined =
        [
            .. duplicates,
            new(generated.ToUpperInvariant() + ".vid", "singleton/" + generated + ".vid")
        ];

        var stems = VideoOutputStemPlanner.Plan(combined);

        Assert.Equal(generated.ToUpperInvariant(), stems[2]);
        Assert.Equal(3, new HashSet<string>(stems, StringComparer.OrdinalIgnoreCase).Count);
    }

    [Theory]
    [InlineData("CON.sfd", "_CON")]
    [InlineData("aux.bik.xen", "_aux")]
    [InlineData("...sfd", "video")]
    public void Plan_OutputStemsAreWindowsSafe(string fileName, string expected)
    {
        Assert.Equal([expected], VideoOutputStemPlanner.Plan([new(fileName, fileName)]));
    }
}
