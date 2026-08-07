using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Trg;

/// <summary>
///     Pins that one TRG reader serves both byte orders (2026-08-07).
///     <para>
///         The N64 ports keep the trigger grammar field for field and re-encode
///         it big-endian, so the reader takes its byte order from the file's own
///         magic — a PS1 file spells it <c>_TRG</c>, its N64 counterpart
///         <c>GRT_</c>, which is the same u32 read the other way round.
///     </para>
///     <para>
///         Two fields needed more than a byte swap, and both for the same
///         reason: the version pair is ONE u32 (major low, minor high), not two
///         u16s. Reading it as a word agrees across platforms; reading it as a
///         pair makes the halves appear exchanged, which is what produced
///         "version 0.2" on the first attempt. The PSX model header has the
///         identical shape at its own leading word — when a u16 pair looks
///         exchanged between these platforms, it was a u32 all along.
///     </para>
/// </summary>
public sealed class TrgEndianTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater (USA).z64";

    private List<byte[]> CarveTriggers()
    {
        var romPath = paths.FindSampleFile(Thps1N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS1 N64 ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets
            .Where(static asset => asset.Path.EndsWith(".trg.n64", StringComparison.Ordinal))
            .Select(static asset => asset.Data)
            .ToList();
    }

    private static TrgFile ParseBytes(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        using var reader = new BinaryReader(stream);
        return TrgFile.Parse(reader, "carved.trg.n64");
    }

    /// <summary>
    ///     Every carved trigger parses, at the right version, with no node left
    ///     unrecognised. Before the byte order was declared, the container
    ///     walked but every u16 node type came back byte-reversed — 0x0041 read
    ///     as 0x4100 — so the file "parsed" into thousands of UNKNOWN_* nodes.
    ///     Zero is the assertion that separates the two.
    /// </summary>
    [CorpusFact]
    public void EveryCarvedTrigger_ParsesWithNoUnknownNodeTypes()
    {
        var triggers = CarveTriggers();
        Assert.NotEmpty(triggers);

        var nodes = 0;
        var unknown = 0;
        foreach (var data in triggers)
        {
            var trg = ParseBytes(data);
            Assert.Equal(2, trg.VersionMajor);
            Assert.NotEmpty(trg.Nodes);

            foreach (var node in trg.Nodes)
            {
                nodes++;
                if (node.Type.StartsWith("UNKNOWN", StringComparison.Ordinal))
                    unknown++;
            }
        }

        Assert.True(nodes > 1000, $"expected a substantial node corpus, got {nodes}");
        Assert.Equal(0, unknown);
    }

    /// <summary>
    ///     The N64 triggers must not invent node types the PS1 grammar does not
    ///     have. This is the check that a wrong byte order cannot pass: garbage
    ///     type words would land outside the PS1 vocabulary. Measured across
    ///     THPS1 and Spider-Man, the N64 set is a strict subset of the PS1 one
    ///     (Spider-Man's is identical, 13 types on both sides).
    /// </summary>
    [CorpusFact]
    public void N64NodeTypes_AreAllKnownToThePs1Grammar()
    {
        var ps1Types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in paths.FindSampleFiles(
                     "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "*.trg"))
        {
            foreach (var node in TrgFile.Parse(file).Nodes)
                ps1Types.Add(node.Type);
        }

        Assert.SkipWhen(ps1Types.Count == 0, "no PS1 TRG corpus available to compare against");

        var n64Types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var data in CarveTriggers())
        {
            foreach (var node in ParseBytes(data).Nodes)
                n64Types.Add(node.Type);
        }

        Assert.NotEmpty(n64Types);
        var invented = n64Types.Except(ps1Types).ToList();
        Assert.True(invented.Count == 0, $"N64 produced types the PS1 grammar lacks: {string.Join(", ", invented)}");
    }

    /// <summary>
    ///     A PS1 trigger must still read as little-endian through the same
    ///     entry point — the sniff keys on the magic, so a PS1 file cannot be
    ///     mistaken for a big-endian one.
    /// </summary>
    [Fact]
    public void Ps1Trigger_StillReadsLittleEndian()
    {
        var file = paths
            .FindSampleFiles("Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "*.trg")
            .FirstOrDefault();
        Assert.SkipWhen(file == null, "no PS1 TRG sample available");

        var trg = TrgFile.Parse(file!);
        Assert.Equal(2, trg.VersionMajor);
        Assert.NotEmpty(trg.Nodes);
        Assert.DoesNotContain(trg.Nodes, n => n.Type.StartsWith("UNKNOWN", StringComparison.Ordinal));
    }
}
