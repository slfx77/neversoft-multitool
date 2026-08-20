using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Nds;

/// <summary>
///     Pins the NDS Nitro-filesystem reader: a synthetic ROM round-trips through
///     <see cref="NdsRomArchive.GetFileList" /> and the rejection gates, and the
///     three shipped Tony Hawk DS carts enumerate their real Nitro trees plus the
///     <c>_system/</c> loader components with byte-exact reads.
/// </summary>
public sealed class NdsRomArchiveTests(TestPaths paths)
{
    private const string Sk8landBuild = "Tony Hawk's American Sk8land (2005-11-15, DS - Final)";
    private const string Sk8landRom = "Tony Hawk's American Sk8land (USA).nds";
    private const string DhjBuild = "Tony Hawk's Downhill Jam (2006-10-24, DS - Final)";
    private const string DhjRom = "Tony Hawk's Downhill Jam (USA).nds";
    private const string PgBuild = "Tony Hawk's Proving Ground (2007-10-15, DS - Final)";
    private const string PgRom = "Tony Hawk's Proving Ground (USA).nds";

    [Fact]
    public void GetFileList_SyntheticRom_ResolvesNitroTreeAndSystemFiles()
    {
        var rom = BuildSyntheticRom();

        Assert.True(NdsRomArchive.IsNdsRom(WriteTemp(rom, out var path)));
        try
        {
            var entries = NdsRomArchive.GetFileList(path);

            var root = Assert.Single(entries, e => e.Name == "root.txt");
            Assert.Equal("", root.Directory);
            Assert.Equal(RootTextBytes.Length, root.Size);

            var child = Assert.Single(entries, e => e.Name == "child.bin");
            Assert.Equal("sub", child.Directory);
            Assert.Equal(ChildBinBytes.Length, child.Size);

            // System components are always present.
            Assert.Contains(entries, e => e is { Directory: "_system", Name: "header.bin" });
            Assert.Contains(entries, e => e is { Directory: "_system", Name: "arm9.bin" });

            // Byte-exact reads through the disk-backed filesystem.
            using var fs = ArchiveFileSystem.TryOpen(path);
            Assert.NotNull(fs);
            Assert.Equal(ArchiveAssetType.Nds, fs!.Type);
            Assert.Equal(RootTextBytes, fs.ReadEntry(fs.FindByPath("root.txt")!));
            Assert.Equal(ChildBinBytes, fs.ReadEntry(fs.FindByPath("sub/child.bin")!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsNdsRom_RejectsCorruptLogoCrc()
    {
        var rom = BuildSyntheticRom();
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15C), 0x1234); // not the commercial 0xCF56
        Assert.False(NdsRomArchive.IsNdsRom(WriteTemp(rom, out var path)));
        File.Delete(path);
    }

    [Fact]
    public void IsNdsRom_RejectsCorruptHeaderCrc()
    {
        var rom = BuildSyntheticRom();
        rom[0x20] ^= 0xFF; // mutate a header field without fixing the header CRC
        Assert.False(NdsRomArchive.IsNdsRom(WriteTemp(rom, out var path)));
        File.Delete(path);
    }

    [Fact]
    public void IsNdsRom_RejectsOutOfBoundsFat()
    {
        var rom = BuildSyntheticRom();
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x48), (uint)rom.Length + 0x1000); // fatOff past EOF
        FixHeaderCrc(rom);
        Assert.False(NdsRomArchive.IsNdsRom(WriteTemp(rom, out var path)));
        File.Delete(path);
    }

    [Fact]
    public void IsNdsRom_RejectsTruncatedFile()
    {
        var rom = BuildSyntheticRom()[..0x100];
        Assert.False(NdsRomArchive.IsNdsRom(WriteTemp(rom, out var path)));
        File.Delete(path);
    }

    [CorpusTheory]
    [InlineData(Sk8landBuild, Sk8landRom, "vvobj/generated/gob/main.gfc", "vvobj/generated/gob/main.gob",
        546088, "211db7be6608626f1df9d33dab9de8aad0bcf7ab1f57e46ed886edea38c89a5a",
        "0ce488576b568f935c5e60f12dfcf7269327a5b2593f6cec2cfdab1c296eadaf")]
    [InlineData(DhjBuild, DhjRom, "vvobj/generated/gob/main.gfc", "vvobj/generated/gob/main.gob",
        297640, "55630e58c9d7ba5ad8d1037ba0fe6b105721d3b94b2547f3e646274ab8a00d78",
        "2df8292eb8aab191b3223a72c8218b9f50ad428ed5f4e62de66e43eeeed98a9f")]
    [InlineData(PgBuild, PgRom, "gob/mainUS.gfc", "gob/mainUS.gob",
        288316, "b915eefd61bb47b12f834396d15c911864f3b07b2322823494ee9c0f841e7705",
        "5d072eb60bd4bb346a599eb54336008e20b1194621127d09daf0b305d5d07568")]
    public void RealCart_EnumeratesNitroTree_AndReadsEntriesByteExact(
        string build, string rom, string gfcPath, string gobPath, int gfcSize, string headerSha, string gfcSha)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        Assert.True(NdsRomArchive.IsNdsRom(romPath!));

        using var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        Assert.Equal(ArchiveAssetType.Nds, fs!.Type);

        // The Vicarious Visions container pair is the point of the whole DS effort.
        var gfc = fs.FindByPath(gfcPath);
        var gob = fs.FindByPath(gobPath);
        Assert.NotNull(gfc);
        Assert.NotNull(gob);
        Assert.Equal(gfcSize, gfc!.Size);

        // Every entry is readable at its declared size, both Nitro and _system.
        foreach (var entry in fs.Entries)
            Assert.Equal(entry.Size, fs.ReadEntry(entry).Length);

        var header = fs.ReadEntry(fs.FindByPath("_system/header.bin")!);
        Assert.Equal(512, header.Length);
        Assert.Equal(headerSha, Sha(header));
        Assert.Equal(gfcSha, Sha(fs.ReadEntry(gfc)));
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string WriteTemp(byte[] rom, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"nmt-nds-{Guid.NewGuid():N}.nds");
        File.WriteAllBytes(path, rom);
        return path;
    }

    // ---- synthetic minimal NDS ROM -----------------------------------------

    private static readonly byte[] RootTextBytes = Encoding.ASCII.GetBytes("root file contents");
    private static readonly byte[] ChildBinBytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];

    private static byte[] BuildSyntheticRom()
    {
        var rom = new byte[0x10000];

        // header identity
        Encoding.ASCII.GetBytes("SYNTH").CopyTo(rom.AsSpan(0x00));
        Encoding.ASCII.GetBytes("ZZZE").CopyTo(rom.AsSpan(0x0C));

        // ARM9 {off, entry, ram, size}; ARM7 at 0x30. Point them at padding so
        // the _system reads land in-bounds.
        WriteBlock(rom, 0x20, 0x8000, 0x100);
        WriteBlock(rom, 0x30, 0x8200, 0x80);

        // Layout: FAT at 0x400 (2 slots), FNT at 0x420, file data at 0x500+.
        const int fatOff = 0x400;
        const int fntOff = 0x420;
        const int rootDataOff = 0x600;
        var childDataOff = rootDataOff + RootTextBytes.Length + 8;

        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x40), fntOff);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x44), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x48), fatOff);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x4C), 16); // 2 slots
        // no overlays

        // FAT: file 0 = root.txt, file 1 = child.bin
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fatOff), (uint)rootDataOff);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fatOff + 4), (uint)(rootDataOff + RootTextBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fatOff + 8), (uint)childDataOff);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fatOff + 12), (uint)(childDataOff + ChildBinBytes.Length));

        RootTextBytes.CopyTo(rom.AsSpan(rootDataOff));
        ChildBinBytes.CopyTo(rom.AsSpan(childDataOff));

        // FNT main table: 2 dirs. sub-tables follow at +0x10.
        const int sub0 = 0x10; // root sub-table, relative to fntOff
        var sub1 = sub0 + RootSubTableLength();
        // dir 0 (root): {subOff, firstFileId=0, dirCount=2}
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fntOff + 0), sub0);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(fntOff + 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(fntOff + 6), 2);
        // dir 1 (sub): {subOff, firstFileId=1, parent=0xF000}
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fntOff + 8), (uint)sub1);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(fntOff + 12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(fntOff + 14), 0xF000);

        var p = fntOff + sub0;
        WriteFileEntry(rom, ref p, "root.txt");
        WriteDirEntry(rom, ref p, "sub", 0xF001);
        rom[p++] = 0x00;

        p = fntOff + sub1;
        WriteFileEntry(rom, ref p, "child.bin");
        rom[p] = 0x00;

        // Logo CRC magic + header CRC.
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15C), 0xCF56);
        FixHeaderCrc(rom);
        return rom;
    }

    private static int RootSubTableLength() =>
        1 + "root.txt".Length + 1 + "sub".Length + 2 + 1; // file, dir(+childId), terminator

    private static void WriteFileEntry(byte[] rom, ref int p, string name)
    {
        rom[p++] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(rom.AsSpan(p));
        p += name.Length;
    }

    private static void WriteDirEntry(byte[] rom, ref int p, string name, ushort childId)
    {
        rom[p++] = (byte)(0x80 | name.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(rom.AsSpan(p));
        p += name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(p), childId);
        p += 2;
    }

    private static void WriteBlock(byte[] rom, int fieldOffset, uint romOff, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fieldOffset), romOff);      // rom offset
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(fieldOffset + 0xC), size);  // size
    }

    private static void FixHeaderCrc(byte[] rom)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15E), Crc16(rom.AsSpan(0, 0x15E)));
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var carry = crc & 1;
                crc >>= 1;
                if (carry != 0)
                    crc ^= 0xA001;
            }
        }

        return (ushort)crc;
    }
}
