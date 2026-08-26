using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Tests.Core.Formats.Gob;

/// <summary>
///     Pins the GOB name dictionary. Every pair must re-hash — the resource is
///     supposed to hold only names proven against a file's own key, so a single
///     entry that does not reproduce its hash means the harvest let a coincidence
///     through.
/// </summary>
public sealed class GobNamesTests
{
    [Fact]
    public void EveryStoredNameReHashesToItsKey()
    {
        Assert.Equal(22819, GobNames.Count);

        var checked_ = 0;
        foreach (var (key, name) in EnumerateResource())
        {
            Assert.Equal(key, GobNames.Hash(name));
            checked_++;
        }

        Assert.Equal(GobNames.Count, checked_);
    }

    [Fact]
    public void ResolvesKnownNamesAndDeclinesUnknownOnes()
    {
        // ".\sound_ui.xml" is spelled in Sk8land's ARM9 and hashes to this key.
        Assert.Equal(0x90E49828u, GobNames.Hash(".\\sound_ui.xml"));
        Assert.Equal(".\\sound_ui.xml", GobNames.TryResolve(0x90E49828));
        Assert.Null(GobNames.TryResolve(0));
    }

    [Fact]
    public void HashIsCaseInsensitiveBecauseTheLoaderLowercasesFirst()
    {
        Assert.Equal(GobNames.Hash(".\\Sound_UI.XML"), GobNames.Hash(".\\sound_ui.xml"));
    }

    [Theory]
    [InlineData(".\\sfx\\env\\bells.swav", "sfx/env/bells.swav")]
    [InlineData(".\\sound_ui.xml", "sound_ui.xml")]
    [InlineData("plain.bin", "plain.bin")]
    public void ToRelativePath_DropsTheLoaderPrefixAndNormalizesSeparators(string name, string expected)
    {
        Assert.Equal(expected, GobNames.ToRelativePath(name));
    }

    private static IEnumerable<(uint Key, string Name)> EnumerateResource()
    {
        using var stream = typeof(GobNames).Assembly.GetManifestResourceStream("GobNames.txt");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"malformed line: {line}");
            yield return (Convert.ToUInt32(line[(separator + 3)..], 16), line[..separator]);
        }
    }
}
