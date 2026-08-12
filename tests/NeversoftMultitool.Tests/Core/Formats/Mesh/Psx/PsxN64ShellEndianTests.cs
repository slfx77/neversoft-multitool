using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

/// <summary>
///     Pins how a carved N64 shell is read, and what changed when it stopped
///     being read through a whole-file word swap (2026-08-07).
///     <para>
///         The N64 ports keep the PS1 model container and re-encode it
///         big-endian. Reversing every 4-byte word gets u32 fields right, but it
///         EXCHANGES the two u16s packed inside a word — so every u16 lands in
///         its neighbour's slot. The old path compensated for exactly one of
///         those (the object mesh index, re-read from +0x14 instead of its real
///         +0x16) and silently mis-read the rest.
///     </para>
///     <para>
///         The one that mattered is the HIER parent array: under the swap, only
///         4 of 28 THPS1 character shells produced a rooted acyclic hierarchy —
///         the other 24 came out with cycles, because adjacent parents were
///         exchanged pairwise. Read big-endian, all 28 are well formed. That
///         defect was invisible in the exports because N64 placement is
///         object-driven rather than skeleton-driven, so nothing that ships today
///         reads the broken parents; it would have surfaced the moment animation
///         was wired up.
///     </para>
///     <para>
///         So these tests assert two different things: the fields both readings
///         agree on must stay identical (the swap path is kept below as that
///         oracle), and the fields it got wrong must now be correct on their own
///         terms.
///     </para>
/// </summary>
public sealed class PsxN64ShellEndianTests(TestPaths paths)
{
    private const int HeaderFixedSize = 12;
    private const int ObjectRecordSize = 36;

    /// <summary>Where a word swap displaces the mesh index to, from its real +0x16.</summary>
    private const int SwappedMeshIndexOffset = 0x14;

    public static TheoryData<string, string> Roms =>
        new()
        {
            { "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64" },
            { "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64" },
            { "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)", "Tony Hawk's Pro Skater 3 (USA).z64" },
            { "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64" }
        };

    /// <summary>
    ///     The retired path: reverse every 4-byte word, zero-pad the stripped
    ///     texture-hash tail, parse little-endian, then correct the mesh index
    ///     from the half of the word the swap moved it to.
    /// </summary>
    /// <summary>
    ///     The swap path's shell plus the mesh indices it corrected by hand.
    ///     They are returned separately because the production property is
    ///     init-only now that nothing patches it after the fact.
    /// </summary>
    private sealed record SwapPathResult(PsxMeshFile Shell, ushort[] MeshIndices);

    private static SwapPathResult? ParseByWordSwap(byte[] data)
    {
        var swapped = SwapWords(data);
        if (!TryMeasureTail(swapped, out var padding))
            return null;

        if (padding > 0)
        {
            var padded = new byte[swapped.Length + padding];
            swapped.CopyTo(padded, 0);
            swapped = padded;
        }

        PsxMeshFile? shell;
        try
        {
            shell = PsxMeshFile.ParseHeaderOnly(swapped);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            return null;
        }

        if (shell == null)
            return null;

        var meshIndices = new ushort[shell.Objects.Count];
        for (var i = 0; i < shell.Objects.Count; i++)
        {
            var position = HeaderFixedSize + i * ObjectRecordSize + SwappedMeshIndexOffset;
            if (position + 2 > swapped.Length)
                break;
            meshIndices[i] = BitConverter.ToUInt16(swapped, position);
        }

        return new SwapPathResult(shell, meshIndices);
    }

    private static byte[] SwapWords(byte[] data)
    {
        var output = new byte[data.Length];
        var wordBytes = data.Length & ~3;
        for (var i = 0; i < wordBytes; i += 4)
        {
            output[i] = data[i + 3];
            output[i + 1] = data[i + 2];
            output[i + 2] = data[i + 1];
            output[i + 3] = data[i];
        }

        for (var i = wordBytes; i < data.Length; i++)
            output[i] = data[i];

        return output;
    }

    private static bool TryMeasureTail(byte[] swapped, out int padding)
    {
        padding = 0;
        if (swapped.Length < HeaderFixedSize)
            return false;

        var objectCount = BitConverter.ToUInt32(swapped, 8);
        if (objectCount == 0)
            return false;

        var meshCountOffset = (long)HeaderFixedSize + (long)objectCount * ObjectRecordSize;
        if (meshCountOffset + 4 > swapped.Length)
            return false;

        var meshCount = BitConverter.ToUInt32(swapped, (int)meshCountOffset);
        if (meshCount is 0 or > 65535)
            return false;

        padding = (int)(meshCount * 4) + 8;
        return true;
    }

    private static List<byte[]> CarveShells(TestPaths paths, string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets
            .Where(static asset => asset.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            .Select(static asset => asset.Data)
            .ToList();
    }

    /// <summary>
    ///     Everything a word swap could not get wrong must be unchanged: u32
    ///     fields survive both readings, so objects' flags and positions, the
    ///     mesh-name hashes and the super/scale classification have to match
    ///     exactly. The mesh index is included because the old path corrected it
    ///     by hand — the correction and the big-endian read must agree.
    ///     <para>
    ///         Parent indices are deliberately NOT compared here: that is the
    ///         field the swap got wrong, and it is asserted on its own terms by
    ///         <see cref="Hierarchy_IsWellFormed_ForEveryShell" />.
    ///     </para>
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(Roms))]
    public void BigEndianReading_ReproducesTheWordSwapPath(string build, string rom)
    {
        var shells = CarveShells(paths, build, rom);
        Assert.NotEmpty(shells);

        var compared = 0;
        foreach (var data in shells)
        {
            var reference = ParseByWordSwap(data);
            var actual = PsxN64ShellFile.Parse(data);

            if (reference == null)
            {
                Assert.Null(actual);
                continue;
            }

            var expected = reference.Shell;
            Assert.NotNull(actual);
            Assert.Equal(expected.Version, actual!.Version);
            Assert.Equal(expected.IsSuperModel, actual.IsSuperModel);

            // A v3 shell's Apocalypse-vs-Neversoft classification is the one
            // thing the old path could not have known: it probes a mesh header,
            // and a shell has no meshes. Pinned separately below.
            if (expected.Version != 0x03)
            {
                Assert.Equal(expected.FormatRevision, actual.FormatRevision);
                Assert.Equal(expected.ScaleDivisor, actual.ScaleDivisor);
            }

            Assert.Equal(expected.MeshNameHashes, actual.MeshNameHashes);
            Assert.Equal(expected.Objects.Count, actual.Objects.Count);

            for (var i = 0; i < expected.Objects.Count; i++)
            {
                var left = expected.Objects[i];
                var right = actual.Objects[i];
                Assert.Equal(left.Flags, right.Flags);
                Assert.Equal(left.RawX, right.RawX);
                Assert.Equal(left.RawY, right.RawY);
                Assert.Equal(left.RawZ, right.RawZ);
                Assert.Equal(reference.MeshIndices[i], right.MeshIndex);
            }

            compared++;
        }

        Assert.True(compared > 0, "no shell decoded under either reading");
    }

    /// <summary>
    ///     The v3 shells must NOT be classified as Apocalypse. That flag means
    ///     the 1998 exporter revision, which never shipped on N64 — the four
    ///     carts are THPS1/2/3 and Spider-Man — and the test that decides it
    ///     reads a mesh header the shell does not contain, so it was answering
    ///     from whatever bytes followed the stripped pointer array (measured
    ///     values 42, 300, 1).
    ///     <para>
    ///         This matters beyond tidiness: both affected shells carry an
    ///         animation chunk, so the classification also picks their scale
    ///         divisor (2.25 vs 36) — a 16x swing decided by junk. Spider-Man is
    ///         the only cart with v3 shells, and it has exactly two.
    ///     </para>
    ///     <para>
    ///         Measured outcome: both exported at LEVEL scale before (max extent
    ///         31,580 and 24,562, inside the corpus's p95–p100 band) and at prop
    ///         scale after (1,974 and 1,579, below the median of 2,482). They are
    ///         a 1-object and a 12-object ANIMATED model, so prop scale is the
    ///         plausible one — and it is what their own super flag asks for,
    ///         since the ×16 exists precisely for supers and the Apocalypse
    ///         branch is the exception that skips it.
    ///     </para>
    /// </summary>
    [CorpusFact]
    public void V3Shells_AreNotClassifiedAsApocalypse()
    {
        var shells = CarveShells(
            paths, "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64");

        var v3 = shells
            .Select(PsxN64ShellFile.Parse)
            .Where(static shell => shell is { Version: 0x03 })
            .ToList();

        Assert.Equal(2, v3.Count);
        Assert.All(v3, shell =>
            Assert.Equal(PsxMeshFormatRevision.NeversoftV3, shell!.FormatRevision));
    }

    /// <summary>
    ///     A hierarchy is well formed when at least one object is its own parent
    ///     (the root marker the header reader turns into -1) and no parent chain
    ///     cycles. Cheap, and it separates the two readings completely: the swap
    ///     produced cycles on 24 of THPS1's 28 shells.
    /// </summary>
    private static bool IsWellFormedHierarchy(PsxMeshFile shell)
    {
        var parents = shell.Objects.Select(static o => o.ParentIndex).ToArray();
        if (!parents.Contains(-1))
            return false;

        for (var start = 0; start < parents.Length; start++)
        {
            var cursor = start;
            for (var step = 0; step <= parents.Length; step++)
            {
                if (cursor < 0)
                    break;
                if (cursor >= parents.Length)
                    return false;
                cursor = parents[cursor];
                if (step == parents.Length)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The defect the byte order was hiding. Every shell that carries a
    ///     hierarchy must produce a rooted, acyclic one — measured 4/28 under the
    ///     old word swap and 28/28 read big-endian, on THPS1 alone.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(Roms))]
    public void Hierarchy_IsWellFormed_ForEveryShell(string build, string rom)
    {
        var shells = CarveShells(paths, build, rom);
        var checkedShells = 0;
        var malformed = 0;

        foreach (var data in shells)
        {
            var shell = PsxN64ShellFile.Parse(data);
            if (shell is not { HasHierarchy: true } || shell.Objects.Count == 0)
                continue;

            checkedShells++;
            if (!IsWellFormedHierarchy(shell))
                malformed++;
        }

        Assert.True(checkedShells > 0, "no shell carried a hierarchy");
        Assert.Equal(0, malformed);
    }

    /// <summary>
    ///     The point of the exercise, stated as an assertion: the mesh index is
    ///     at the PS1 offset (+0x16) once the file is read in its real byte
    ///     order. If this ever fails, the port really did move the field and the
    ///     shared-grammar premise is wrong for the shell.
    /// </summary>
    [CorpusFact]
    public void MeshIndex_IsAtThePs1Offset_WhenReadBigEndian()
    {
        var shells = CarveShells(
            paths,
            "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
            "Tony Hawk's Pro Skater (USA).z64");

        var shell = shells
            .Select(PsxN64ShellFile.Parse)
            .FirstOrDefault(static s => s != null && s.Objects.Any(static o => o.MeshIndex != 0));
        Assert.NotNull(shell);

        var data = shells.First(static d =>
        {
            var parsed = PsxN64ShellFile.Parse(d);
            return parsed != null && parsed.Objects.Any(static o => o.MeshIndex != 0);
        });

        // Read +0x16 straight out of the raw big-endian bytes, with no swap and
        // no reader involved, and it must match what the parse produced.
        for (var i = 0; i < shell!.Objects.Count; i++)
        {
            var position = HeaderFixedSize + i * ObjectRecordSize + 0x16;
            if (position + 2 > data.Length)
                break;
            var raw = (ushort)((data[position] << 8) | data[position + 1]);
            Assert.Equal(raw, shell.Objects[i].MeshIndex);
        }
    }
}
