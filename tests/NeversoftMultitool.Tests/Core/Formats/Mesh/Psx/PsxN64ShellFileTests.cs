using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Pins the carved N64 model shell reader (2026-08-06). The shells are
///     u32-byteswapped PS1 containers with the texture-hash tail stripped, so
///     the stock <see cref="PsxMeshFile.Parse(byte[], bool)" /> rejects them at the
///     version gate — which is why the CLI reported "No mesh data" for every
///     shell and why that message said nothing about their contents. They do
///     carry object tables, hierarchy, mesh name hashes and an animation
///     chunk.
/// </summary>
public sealed class PsxN64ShellFileTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    private static Dictionary<string, byte[]> CarveShells(TestPaths paths, string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets
            .Where(static asset => asset.Path.EndsWith("/geometry.psx.n64", StringComparison.Ordinal))
            .ToDictionary(static asset => asset.Path, static asset => asset.Data);
    }

    [Fact]
    public void Parse_ReadsObjectsAndHierarchy_FromACarvedShell()
    {
        var shells = CarveShells(paths, Thps2N64Build, RomName);
        var shell = PsxN64ShellFile.Parse(shells["models/000/geometry.psx.n64"]);

        Assert.NotNull(shell);
        Assert.Equal(4, shell!.Version);
        Assert.Equal(19, shell.Objects.Count);
        Assert.Equal(19, shell.MeshNameHashes.Length);
        Assert.True(shell.HasHierarchy);
        // The animation chunk is what marks a super; it is ~99% of the file.
        Assert.True(shell.IsSuperModel);
        // Header-only: geometry lives in the render bank, not here.
        Assert.Empty(shell.Meshes);
    }

    /// <summary>
    ///     Mutation guard: the byteswap is load-bearing. Feeding the raw carved
    ///     bytes to the PS1 reader must fail, or the swap could silently become
    ///     a no-op without any test noticing.
    /// </summary>
    [Fact]
    public void RawCarvedBytes_AreRejectedByThePs1Reader()
    {
        var shells = CarveShells(paths, Thps2N64Build, RomName);
        var raw = shells["models/000/geometry.psx.n64"];

        Assert.Null(PsxMeshFile.Parse(raw));
        Assert.Null(PsxMeshFile.ParseHeaderOnly(raw));
        Assert.NotNull(PsxN64ShellFile.Parse(raw));
    }

    /// <summary>
    ///     Corpus sweep. The pinned pair is (total shells, shells with content):
    ///     the remainder are authored EMPTY slots — mostly 24-byte stubs whose
    ///     object count is genuinely zero, which the PS1 reader also rejects.
    ///     The sweep asserts that emptiness is the ONLY reason a shell fails,
    ///     so a future parsing regression cannot hide inside the shortfall.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 80, 65)]
    [InlineData(Thps2N64Build, RomName, 141, 116)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 112, 87)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 261, 182)]
    public void EveryNonEmptyShell_Parses(
        string buildName,
        string romName,
        int expectedShells,
        int expectedParsed)
    {
        var shells = CarveShells(paths, buildName, romName);
        Assert.Equal(expectedShells, shells.Count);

        var parsed = 0;
        var withHierarchy = 0;
        foreach (var (path, data) in shells)
        {
            var shell = PsxN64ShellFile.Parse(data);
            if (shell == null)
            {
                // Only an authored-empty shell may fail: objectCount is the BE
                // u32 at +8 of the unswapped record.
                var objectCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    data.AsSpan(8));
                Assert.True(objectCount == 0,
                    $"{path}: parse failed but objectCount is {objectCount}, not an empty shell");
                continue;
            }

            parsed++;
            Assert.NotEmpty(shell.Objects);
            Assert.Empty(shell.Meshes);
            if (shell.HasHierarchy)
                withHierarchy++;
        }

        Assert.Equal(expectedParsed, parsed);
        Assert.True(withHierarchy > 0, "expected at least one hierarchical shell");
    }
}
