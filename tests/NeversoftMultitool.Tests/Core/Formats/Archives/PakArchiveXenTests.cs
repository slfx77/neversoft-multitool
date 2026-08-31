using System.Buffers.Binary;
using System.IO.Compression;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.ArchiveFs;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Xbox 360 .pak.xen support: big-endian THAW-generation tables wrapped in
///     whole-file compression — headerless raw deflate on Project 8 / Proving
///     Ground, headerless Okumura LZSS on THAW X360 — with the Proving Ground
///     QbKey(".last") terminator and LE-style header-relative offset semantics
///     (never the GC companion-residency flag). Measured 2026-08-25 on all three
///     X360 corpus builds.
/// </summary>
public class PakArchiveXenTests(TestPaths paths)
{
    private const string P8Build = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string PgBuild = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";
    private const string ThawBuild = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";

    private const uint QbExtensionKey = 0xA7F505C4; // QbKey(".qb")
    private const uint LastSentinel = 0xB524565F; // QbKey("last")
    private const uint DotLastSentinel = 0x2CB3EF3B; // QbKey(".last")

    // ─── Synthetic wrapper + sentinel coverage ───────────────────────────

    [Fact]
    public void DeflateWrappedBigEndianTable_IsDetectedAndListed()
    {
        var raw = BuildBigEndianPak(LastSentinel, entryFlags: 0);
        var wrapped = DeflateWrap(raw);

        Assert.True(PakArchive.IsPakArchive(wrapped));

        var entries = PakArchive.GetFileList(wrapped, hasPab: false, sourceName: "test.pak.xen");
        var entry = Assert.Single(entries);
        Assert.EndsWith(".qb.xen", entry.Name, StringComparison.Ordinal);
        Assert.Equal(16, entry.Size);
    }

    [Fact]
    public void LzssWrappedBigEndianTable_IsDetectedAndListed()
    {
        var raw = BuildBigEndianPak(LastSentinel, entryFlags: 0);
        var wrapped = LzssLiteralWrap(raw);

        Assert.True(PakArchive.IsPakArchive(wrapped));

        var entries = PakArchive.GetFileList(wrapped, hasPab: false, sourceName: "test.pak.xen");
        var entry = Assert.Single(entries);
        Assert.EndsWith(".qb.xen", entry.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void DotLastSentinel_TerminatesTheTable()
    {
        // Proving Ground X360 switched the terminator to QbKey(".last").
        var raw = BuildBigEndianPak(DotLastSentinel, entryFlags: 0);

        Assert.True(PakArchive.IsPakArchive(raw));

        var entries = PakArchive.GetFileList(raw, hasPab: false, sourceName: "test.pak.xen");
        Assert.Single(entries);
    }

    [Fact]
    public void GcResidentBigEndianTable_KeepsNgcSuffixWithoutSourceName()
    {
        // Regression pin: an anonymous big-endian buffer (nested GC pak) still
        // generates .ngc-suffixed names; only .xen sources switch the suffix.
        var raw = BuildBigEndianPak(LastSentinel, entryFlags: 0x80000000);

        var entries = PakArchive.GetFileList(raw);
        var entry = Assert.Single(entries);
        Assert.EndsWith(".qb.ngc", entry.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPakBytes_AreNotMisdetectedThroughTheWrappers()
    {
        // Deterministic pseudo-random bytes: no raw table, no valid inflate to a
        // table, and an LZSS decode that cannot produce a valid table either.
        var data = new byte[4096];
        uint state = 0x12345678;
        for (var i = 0; i < data.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        Assert.False(PakArchive.IsPakArchive(data));
    }

    // ─── Real X360 fixtures ──────────────────────────────────────────────

    [CorpusFact]
    public void P8DbgqPak_DeflateWrapped_ResolvesBothEntriesThroughThePab()
    {
        var pakPath = paths.FindSampleFile(P8Build, "dbgq.pak.xen");
        Assert.SkipWhen(pakPath is null, "P8 X360 dbgq.pak.xen not found");

        Assert.True(PakArchive.IsPakArchive(pakPath!));

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(2, entries.Count);
        Assert.Equal("911201C0.qb.xen", entries[0].Name);
        Assert.Equal(0x468, entries[0].Size);
        Assert.Equal("944E720D.qb.xen", entries[1].Name);
        Assert.Equal(0x2A7BC, entries[1].Size);

        var outputDir = Path.Combine(Path.GetTempPath(), $"xen_pak_test_{Guid.NewGuid():N}");
        try
        {
            PakArchive.ExtractFiles(pakPath!, outputDir);
            var bigQb = Path.Combine(outputDir, "dbgq.pak", "944E720D.qb.xen");
            var data = File.ReadAllBytes(bigQb);
            Assert.Equal(0x2A7BC, data.Length);
            // THAW sectioned-QB header: u32 0 + u32 fileSize, big-endian on X360.
            // The embedded size matching the pak entry proves the deflate wrapper,
            // the byte order, and the header-relative pab resolution all at once.
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(data));
            Assert.Equal(0x2A7BCu, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [CorpusFact]
    public void ThawQbPak_LzssWrapped_ListsFullEntriesWithEmbeddedNames()
    {
        var pakPath = paths.FindSampleFile(ThawBuild, "qb.pak.xen");
        Assert.SkipWhen(pakPath is null, "THAW X360 qb.pak.xen not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(267, entries.Count);
        Assert.All(entries, e => Assert.True(e.Size > 0, $"Entry {e.Name} has zero size"));
        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/aaaaloopchecker.qb.xen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e =>
            e.FullName.Equals("scripts/zone_sizes_xen.qb.xen", StringComparison.OrdinalIgnoreCase));
    }

    [CorpusFact]
    public void ThawDummyPak_LzssDecodesToPlaintext_IsNotAnArchive()
    {
        // cap_assets_sfx.pak.xen LZSS-decodes to a plaintext dummy-file notice,
        // not a table — decode-then-validate must reject it rather than extract.
        var pakPath = paths.FindSampleFile(ThawBuild, "cap_assets_sfx.pak.xen");
        Assert.SkipWhen(pakPath is null, "THAW X360 cap_assets_sfx.pak.xen not found");

        Assert.False(PakArchive.IsPakArchive(pakPath!));
    }

    [CorpusFact]
    public void PgQbPak_DotLastSentinel_ListsAllEntries()
    {
        var pakPath = paths.FindSampleFile(PgBuild, "qb.pak.xen");
        Assert.SkipWhen(pakPath is null, "PG X360 qb.pak.xen not found");

        var entries = PakArchive.GetFileList(pakPath!);
        Assert.Equal(1074, entries.Count);
        Assert.All(entries, e => Assert.True(e.Size > 0, $"Entry {e.Name} has zero size"));
        Assert.All(entries, e => Assert.EndsWith(".xen", e.Name, StringComparison.OrdinalIgnoreCase));
    }

    [CorpusFact]
    public void ArchiveFileSystem_BrowsesCompressedPakThroughMaterializedBuffers()
    {
        var pakPath = paths.FindSampleFile(P8Build, "dbgq.pak.xen");
        Assert.SkipWhen(pakPath is null, "P8 X360 dbgq.pak.xen not found");

        using var fs = ArchiveFileSystem.TryOpen(pakPath!);
        Assert.NotNull(fs);
        Assert.Equal(2, fs!.Entries.Count);

        var big = fs.Entries.Single(e => e.Name == "944E720D.qb.xen");
        var data = fs.ReadEntry(big);
        Assert.Equal(0x2A7BC, data.Length);
        Assert.Equal(0x2A7BCu, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    ///     One compact big-endian entry (header-relative offset to 16 payload
    ///     bytes at +64), a sentinel entry, then the payload.
    /// </summary>
    private static byte[] BuildBigEndianPak(uint sentinel, uint entryFlags)
    {
        var data = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x00), QbExtensionKey);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x04), 64); // header-relative
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x08), 16); // size
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x14), 0x1234ABCD); // name key
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x1C), entryFlags);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x20), sentinel);
        "XENPAYLOAD123456"u8.CopyTo(data.AsSpan(64));
        return data;
    }

    private static byte[] DeflateWrap(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return output.ToArray();
    }

    /// <summary>All-literal Okumura LZSS stream: flag 0xFF before each 8 literals.</summary>
    private static byte[] LzssLiteralWrap(byte[] raw)
    {
        using var output = new MemoryStream();
        for (var i = 0; i < raw.Length; i += 8)
        {
            output.WriteByte(0xFF);
            output.Write(raw, i, Math.Min(8, raw.Length - i));
        }

        return output.ToArray();
    }
}
