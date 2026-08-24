namespace NeversoftMultitool.Tests.CLI;

public sealed class ProgramCommandRegistryTests
{
    private static readonly string[] ExpectedCommandNames =
    [
        "psx",
        "rle",
        "archive",
        "cas",
        "wgt",
        "pvr",
        "ngctex",
        "n64tex",
        "n64-audio-inspect",
        "n64-audio-fx-inspect",
        "n64-audio-runtime-inspect",
        "n64-sfx-inspect",
        "n64-audio-decode",
        "ddm",
        "audio",
        "qbkey",
        "trg",
        "sfd",
        "vid",
        "str",
        "mesh",
        "psx-mesh",
        "psx-mesh-dump",
        "psxanim",
        "psx-anim-export",
        "psx-anim-trace",
        "psx-anim-survey",
        "ps2tex",
        "rwdff",
        "rwbsp",
        "col",
        "ngccol",
        "xbxtex",
        "unpack",
        "qb",
        "glb-render",
        "glb-gif",
        "gsdump",
        "ska",
        "thps2x-anim",
        "fnt",
        "gba-audio",
        "gba-image",
        "gba-music",
        "gba-level",
        "nds-texture",
        "nds-mesh"
    ];

    [Fact]
    public void BuildRootCommand_RegistersThePinnedPublicCatalogInOrder()
    {
        var rootCommand = Program.BuildRootCommand();
        var actualNames = rootCommand.Subcommands
            .Select(static command => command.Name)
            .ToArray();

        Assert.Equal(47, actualNames.Length);
        Assert.Equal(ExpectedCommandNames, actualNames);
        Assert.Equal(
            actualNames.Length,
            actualNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            rootCommand.Subcommands,
            static command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));

        // These legacy entry points were deliberately superseded by mesh.
        Assert.DoesNotContain("ps2scene", actualNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ps2geom", actualNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("xbxscene", actualNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsCliMode_RecognizesTheWholeCatalogWithoutWinUi()
    {
        foreach (var commandName in ExpectedCommandNames)
        {
            Assert.True(Program.IsCliMode([commandName]));
            Assert.True(Program.IsCliMode([commandName.ToUpperInvariant()]));
        }

        Assert.True(Program.IsCliMode(["-n"]));
        Assert.True(Program.IsCliMode(["--no-gui"]));
        Assert.False(Program.IsCliMode([]));
        Assert.False(Program.IsCliMode(["unknown-command"]));
        Assert.False(Program.IsCliMode(["ps2scene"]));
        Assert.False(Program.IsCliMode(["ps2geom"]));
        Assert.False(Program.IsCliMode(["xbxscene"]));
    }
}
