using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool.Tests.Core.Formats;

public sealed class ScriptOutputPathPlannerTests
{
    [Fact]
    public void Plan_UniqueInputsKeepHistoricalNamesAndCallerOrder()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(Path.Combine("scripts", "shared.trg"), ScriptOutputKind.Trg),
            new(Path.Combine("scripts", "section.qb.ps2"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "shared.qb"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "carved.trg.n64"), ScriptOutputKind.Trg)
        ];

        var planned = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal(
            ["shared.json", "section.qb.q", "shared.q", "carved.trg.json"],
            planned);
    }

    [Fact]
    public void Plan_VirtualPathsUseEntryLeafAfterFinalArchiveQualifier()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(@"C:\game\data.wad::level.qb.ps2", ScriptOutputKind.Qb),
            new(@"C:\game\outer.wad::inner.pre::goals.trg", ScriptOutputKind.Trg),
            new(@"C:\game\outer.wad::inner.pre::scripts\park.trg.ps2", ScriptOutputKind.Trg)
        ];

        var planned = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal(["level.qb.q", "goals.json", "park.trg.json"], planned);
    }

    [Fact]
    public void Plan_RootAndNestedArchiveEntriesWithSameLeafGetStableUniqueNames()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(@"C:\game\data.wad::level.qb", ScriptOutputKind.Qb),
            new(@"C:\game\data.wad::inner.pre::level.qb", ScriptOutputKind.Qb)
        ];

        var forward = ScriptOutputPathPlanner.Plan(inputs);
        var reversed = ScriptOutputPathPlanner.Plan(inputs.Reverse().ToArray());

        Assert.Equal(["level_2.q", "level.q"], forward);
        Assert.Equal(["level.q", "level_2.q"], reversed);
        Assert.Equal(inputs.Length, forward.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
    }

    [Fact]
    public void Plan_CompoundAliasesAreCaseInsensitiveAndStableWhenReversed()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(Path.Combine("Scripts", "Level.qb.wpc"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "level.trg.ps2"), ScriptOutputKind.Trg),
            new(Path.Combine("scripts", "level.qb.ps2"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "level.trg.n64"), ScriptOutputKind.Trg)
        ];

        var forward = ScriptOutputPathPlanner.Plan(inputs);
        var reversedInputs = inputs.Reverse().ToArray();
        var reversed = ScriptOutputPathPlanner.Plan(reversedInputs);
        var reversedBySource = reversedInputs
            .Select((input, index) => (input.SourcePath, OutputName: reversed[index]))
            .ToDictionary(static pair => pair.SourcePath, static pair => pair.OutputName);

        Assert.Equal(inputs.Length, forward.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
        Assert.Equal("Level.qb_2.q", forward[0]);
        Assert.Equal("level.trg_2.json", forward[1]);
        Assert.Equal("level.qb.q", forward[2]);
        Assert.Equal("level.trg.json", forward[3]);
        for (var index = 0; index < inputs.Length; index++)
            Assert.Equal(forward[index], reversedBySource[inputs[index].SourcePath]);
    }

    [Fact]
    public void Plan_GeneratedOrdinalCannotTakeAnotherInputsPreferredName()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(Path.Combine("scripts", "level.qb.wpc"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "level.qb_2.qb"), ScriptOutputKind.Qb),
            new(Path.Combine("scripts", "level.qb.ps2"), ScriptOutputKind.Qb)
        ];

        var planned = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal(
            ["level.qb_3.q", "level.qb_2.q", "level.qb.q"],
            planned);
        Assert.Equal(inputs.Length, planned.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
    }

    [Fact]
    public void Plan_UnsafeStemsBecomeSingleWindowsSafeComponents()
    {
        ScriptOutputPathInput[] inputs =
        [
            new(@"C:\input\CON.qb", ScriptOutputKind.Qb),
            new(@"C:\input\bad<name>?.trg", ScriptOutputKind.Trg),
            new(@"C:\input\trail. .qb", ScriptOutputKind.Qb),
            new(@"C:\input\LPT9.report.qb", ScriptOutputKind.Qb),
            new("control\u0001name.qb", ScriptOutputKind.Qb)
        ];

        var planned = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal(
            ["_CON.q", "bad_name__.json", "trail.q", "_LPT9.report.q", "control_name.q"],
            planned);
        Assert.All(planned, static outputName =>
        {
            Assert.DoesNotContain('/', outputName);
            Assert.DoesNotContain('\\', outputName);
            Assert.DoesNotContain(':', outputName);
            Assert.True(outputName.Length <= 255);
        });
    }

    [Fact]
    public void Plan_LongBareTrgUsesBoundedDeterministicHashWithoutSplittingSurrogate()
    {
        var asciiStem = new string('a', 251);
        var surrogateStem = new string('b', 240) + "\U0001F600" + new string('c', 9);
        ScriptOutputPathInput[] inputs =
        [
            new(asciiStem + ".trg", ScriptOutputKind.Trg),
            new(surrogateStem + ".trg", ScriptOutputKind.Trg)
        ];

        var first = ScriptOutputPathPlanner.Plan(inputs);
        var second = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal(first, second);
        Assert.Equal(255, first[0].Length);
        Assert.All(first, static outputName =>
        {
            Assert.True(outputName.Length <= 255);
            Assert.Matches("_[0-9a-f]{8}\\.json$", outputName);
            Assert.True(HasWellFormedSurrogates(outputName));
        });
    }

    [Fact]
    public void Plan_LongCollisionStillReservesNaturalOrdinalName()
    {
        var longStem = new string('z', 260);
        ScriptOutputPathInput[] inputs =
        [
            new(longStem + ".qb.wpc", ScriptOutputKind.Qb),
            new(longStem + ".qb_2.qb", ScriptOutputKind.Qb),
            new(longStem + ".qb.ps2", ScriptOutputKind.Qb)
        ];
        var expectedBase = Assert.Single(ScriptOutputPathPlanner.Plan(
            [new(longStem + ".qb.ps2", ScriptOutputKind.Qb)]));
        var expectedOrdinal2 = Assert.Single(ScriptOutputPathPlanner.Plan(
            [new(longStem + ".qb_2.qb", ScriptOutputKind.Qb)]));
        var expectedOrdinal3 = Assert.Single(ScriptOutputPathPlanner.Plan(
            [new(longStem + ".qb_3.qb", ScriptOutputKind.Qb)]));

        var planned = ScriptOutputPathPlanner.Plan(inputs);

        Assert.Equal([expectedOrdinal3, expectedOrdinal2, expectedBase], planned);
        Assert.All(planned, static outputName => Assert.True(outputName.Length <= 255));
        Assert.Equal(inputs.Length, planned.ToHashSet(StringComparer.OrdinalIgnoreCase).Count);
    }

    private static bool HasWellFormedSurrogates(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    return false;

                index += 2;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
            else
            {
                index++;
            }
        }

        return true;
    }
}
