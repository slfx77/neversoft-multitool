using System.Text;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Tests.Core.Formats.Gob;

/// <summary>
///     Pins the GOB content sniffer's one load-bearing property: it must never put
///     a WRONG extension on a file.
///
///     The 6,235 proven names are the oracle — each pairs a true extension with
///     real bytes — and the corpus test asserts that not one of the 6,906 named
///     files across the three carts is mislabelled. Coverage of the UNNAMED bulk is
///     only ~4%, because the Vicarious Visions asset formats that make up the rest
///     are not identified yet; that is recorded as a measurement rather than
///     papered over with a guess.
/// </summary>
public sealed class GobContentTypesTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, string Gob)[] Carts =
    [
        ("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
            "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
            "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob")
    ];

    [Fact]
    public void Detect_RecognizesTheMagicsLearnedFromProvenNames()
    {
        Assert.Equal(".swav", GobContentTypes.Detect("SWAV\0\0\0\0"u8));
        Assert.Equal(".strm", GobContentTypes.Detect("STRM\0\0\0\0"u8));
        Assert.Equal(".hwas", GobContentTypes.Detect("sawh\0\0\0\0"u8));
        Assert.Equal(".prp", GobContentTypes.Detect("PFPF\0\0\0\0"u8));
        Assert.Equal(".comp", GobContentTypes.Detect("pmoc\0\0\0\0"u8));
        Assert.Equal(".lwc", GobContentTypes.Detect("LWC\0\0\0\0"u8));
        Assert.Equal(".sac", GobContentTypes.Detect([0x20, 0x00, 0x4B, 0x00, 0, 0, 0, 0]));
        Assert.Equal(".xml", GobContentTypes.Detect(Encoding.ASCII.GetBytes("<menusystem>\n")));
    }

    [Fact]
    public void Detect_DoesNotClaimPalettesFromSizeAlone()
    {
        // A 512-byte blob of BGR555-looking u16s is equally a 32x32 4bpp texel
        // blob; the withdrawn rule mislabelled 13 real ones. Palettes are named
        // only when the container names them.
        var palette = new byte[512];
        for (var i = 0; i < 256; i++)
            palette[i * 2 + 1] = 0x7F;
        Assert.Null(GobContentTypes.Detect(palette));
    }

    [Fact]
    public void Detect_DeclinesRatherThanGuessing()
    {
        // The two big Vicarious Visions families are deliberately unclaimed.
        Assert.Null(GobContentTypes.Detect([0x04, 0, 0, 0, 0x8A, 0x5C, 0x01, 0]));
        Assert.Null(GobContentTypes.Detect([0x30, 0, 0, 0, 0x0B, 0, 0, 0]));
        Assert.Null(GobContentTypes.Detect([1, 2, 3]));
    }

    [CorpusFact]
    public void RealCarts_NeverMislabelAFileWhoseRealExtensionIsKnown()
    {
        var checkedFiles = 0;
        var labelled = 0;
        var mislabelled = new List<string>();

        foreach (var (build, rom, gobPath) in Carts)
        {
            var romPath = paths.FindSampleFile(build, rom);
            Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

            using var cart = ArchiveFileSystem.TryOpen(romPath!);
            using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
            Assert.NotNull(gob);

            foreach (var entry in gob!.Entries)
            {
                var name = GobNames.TryResolve(entry.Crc);
                if (name == null)
                    continue;
                var dot = name.LastIndexOf('.');
                if (dot < 0)
                    continue;

                checkedFiles++;
                var truth = name[dot..].ToLowerInvariant();
                var guess = GobContentTypes.Detect(gob.ReadEntry(entry));
                if (guess == null)
                    continue;

                labelled++;
                if (!string.Equals(guess, truth, StringComparison.Ordinal))
                    mislabelled.Add($"{name} -> {guess}");
            }
        }

        Assert.Empty(mislabelled);
        Assert.Equal(6906, checkedFiles);
        Assert.Equal(1728, labelled);
    }
}
