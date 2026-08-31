using System.Globalization;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core;

/// <summary>
///     Project 8 PS3 names every DATA file QbKey(lowercased filename).CHK while
///     keeping the real directory tree. QbKeyNames.P8Ps3Disc.txt preserves the
///     2026-08-25 harvest (sibling P8 X360/PS2/PSP trees, PG PS3's real-named
///     loose tree, the VRAM-twin suffix map, tip/cam pattern fans, and the P8
///     PS3 DWARF ELF's module strings) — 4,826 of the 4,835 shipped stems, each
///     accepted only on re-hash. These tests re-prove the whole resource and
///     pin the corpus rename's effect.
/// </summary>
public class P8Ps3ChkNamesTests(TestPaths paths)
{
    private const string P8Ps3Build = "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";

    [Fact]
    public void Resource_EveryPairRehashesToItsKey()
    {
        var assembly = typeof(P8Ps3ChkNames).Assembly;
        using var stream = assembly.GetManifestResourceStream("QbKeyNames.P8Ps3Disc.txt");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);

        var pairs = 0;
        var seen = new HashSet<uint>();
        while (reader.ReadLine() is { } line)
        {
            var eq = line.LastIndexOf("=0x", StringComparison.Ordinal);
            Assert.True(eq > 0, $"Malformed line: '{line}'");
            var name = line[..eq];
            var hash = uint.Parse(line[(eq + 3)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            // The harvest stores the exact lowercased string the console hashed;
            // re-hashing is the proof of every pair.
            Assert.Equal(name, name.ToLowerInvariant());
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain('/', name);
            Assert.Equal(hash, NeversoftMultitool.Core.QbKey.QbKey.HashLower(name));
            Assert.True(seen.Add(hash), $"Duplicate key 0x{hash:X8} ('{name}')");
            pairs++;
        }

        Assert.Equal(4826, pairs);
        Assert.Equal(4826, P8Ps3ChkNames.Count);
    }

    [Fact]
    public void TryResolve_KnownPairs_ReturnTheProvenNames()
    {
        Assert.True(P8Ps3ChkNames.TryResolve(0xDAA226C5, out var name));
        Assert.Equal("streamall.dat.ps3", name);

        // Movies hash their Xbox logical .xen name; MEMCARD art its bare name;
        // the fmod SPU module was read out of the DWARF ELF's strings.
        Assert.True(P8Ps3ChkNames.TryResolve(0x99A33296, out name));
        Assert.Equal("cas.png", name);
        Assert.True(P8Ps3ChkNames.TryResolve(0xA94816E6, out name));
        Assert.Equal("fmodex_spu.self", name);

        // VRAM twin split: one X360 pak becomes main + _vram on PS3.
        Assert.True(P8Ps3ChkNames.TryResolve(0x4BBBB39A, out name));
        Assert.Equal("z_world.pak.ps3", name);
        Assert.True(P8Ps3ChkNames.TryResolve(0x70BFBB6D, out name));
        Assert.Equal("z_world_vram.pak.ps3", name);

        Assert.True(P8Ps3ChkNames.TryResolveChkFileName("2B745D86.CHK", out name));
        Assert.Equal("standardkeyq.bin.ps3", name);
        Assert.False(P8Ps3ChkNames.TryResolveChkFileName("nothex.CHK", out _));
        Assert.False(P8Ps3ChkNames.TryResolveChkFileName("2B745D86.bin", out _));
    }

    [CorpusFact]
    public void Corpus_P8Ps3Build_IsRenamedWithOnlyThePinnedResidue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, P8Ps3Build, "PS3_GAME", "USRDIR");
        Assert.SkipWhen(!Directory.Exists(root), "P8 PS3 build not present");

        // Renamed files exist under their proven names...
        Assert.True(File.Exists(Path.Combine(root, "DATA", "ANIMS", "standardkeyq.bin.ps3")));
        Assert.True(File.Exists(Path.Combine(root, "DATA", "STREAMS", "streamall.dat.ps3")));
        Assert.True(File.Exists(Path.Combine(root, "DATA", "PS3MODULES", "fmodex_spu.self")));

        // ...and the only hash names left are the 9 unresolvable ones: 7 empty
        // cutscene placeholder tables and the 2 PS3-only SPU/SELF modules.
        var residue = Directory.EnumerateFiles(root, "*.CHK", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "DATA/CUTSCENES/03819B18.CHK",
            "DATA/CUTSCENES/12985C3F.CHK",
            "DATA/CUTSCENES/3D0B16A2.CHK",
            "DATA/CUTSCENES/659DFF07.CHK",
            "DATA/CUTSCENES/70A4304D.CHK",
            "DATA/CUTSCENES/7C31701C.CHK",
            "DATA/CUTSCENES/DECF4FE3.CHK",
            "DATA/PS3MODULES/8FE888BC.CHK",
            "DATA/PS3MODULES/E0424882.CHK"
        ], residue);
    }
}
