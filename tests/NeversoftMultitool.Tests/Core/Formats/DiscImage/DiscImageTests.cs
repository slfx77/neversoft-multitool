using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class DiscImageTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "NeversoftMultitoolTests", Guid.NewGuid().ToString("N"));

    public DiscImageTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    // ─── Cue sheet parsing ────────────────────────────────────────────────

    [Fact]
    public void CueSheet_ParsesMultiFileTracksWithTypesAndIndexes()
    {
        string[] lines =
        [
            "FILE \"Game (Track 1).bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00",
            "FILE \"Game (Track 2).bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    INDEX 00 00:00:00",
            "    INDEX 01 00:02:00"
        ];

        var cue = CueSheet.Parse(lines, _tempDir);

        Assert.Equal(2, cue.Tracks.Count);
        Assert.Equal(1, cue.Tracks[0].Number);
        Assert.Equal("MODE2/2352", cue.Tracks[0].Type);
        Assert.Equal(2352, cue.Tracks[0].SectorSize);
        Assert.False(cue.Tracks[0].IsAudio);
        Assert.Equal(0, cue.Tracks[0].Index01Frames);

        Assert.Equal(2, cue.Tracks[1].Number);
        Assert.True(cue.Tracks[1].IsAudio);
        Assert.Equal(150, cue.Tracks[1].Index01Frames); // 2 s pregap = 150 frames
    }

    [Fact]
    public void GdiSheet_ParsesTracksAndDataSession()
    {
        string[] lines =
        [
            "3",
            "1 0 4 2352 track01.bin 0",
            "2 756 0 2352 track02.raw 0",
            "3 45000 4 2352 track03.bin 0"
        ];

        var gdi = GdiSheet.Parse(lines, _tempDir);

        Assert.Equal(3, gdi.Tracks.Count);
        Assert.True(gdi.Tracks[0].IsData);
        Assert.False(gdi.Tracks[1].IsData);
        Assert.Equal(45000, gdi.DataSessionLba);
    }

    // ─── Synthetic ISO9660 round-trips ────────────────────────────────────

    [Fact]
    public void PlainIso_ListsAndExtractsNestedFiles()
    {
        var isoPath = Path.Combine(_tempDir, "test.iso");
        File.WriteAllBytes(isoPath, BuildIso9660Sectors());

        Assert.True(DiscImageArchive.IsDiscImage(isoPath));

        var entries = DiscImageArchive.GetFileList(isoPath);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "HELLO.TXT" && e.Directory == "" && e.Size == 13);
        Assert.Contains(entries, e => e.Name == "NESTED.BIN" && e.Directory == "DATA" && e.Size == 5);

        var outDir = Path.Combine(_tempDir, "out_iso");
        DiscImageArchive.ExtractFiles(
            isoPath,
            outDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hello, disc!\n", File.ReadAllText(Path.Combine(outDir, "HELLO.TXT")));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(outDir, "DATA", "NESTED.BIN")));
    }

    [Fact]
    public void RawBinCue_ExtractsMode2Form1Data()
    {
        var binPath = Path.Combine(_tempDir, "game.bin");
        File.WriteAllBytes(binPath, WrapAsRawMode2(BuildIso9660Sectors(), []));

        var cuePath = Path.Combine(_tempDir, "game.cue");
        File.WriteAllLines(cuePath,
        [
            "FILE \"game.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00"
        ]);

        Assert.True(DiscImageArchive.IsDiscImage(cuePath));

        var outDir = Path.Combine(_tempDir, "out_cue");
        DiscImageArchive.ExtractFiles(
            cuePath,
            outDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hello, disc!\n", File.ReadAllText(Path.Combine(outDir, "HELLO.TXT")));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(outDir, "DATA", "NESTED.BIN")));
    }

    /// <summary>
    ///     Single-file cue with CD-DA: each audio track's extent must end where
    ///     the NEXT track in the same image begins. The pre-2026-08-04 code ran
    ///     every audio track to end-of-image, so track02.wav contained tracks
    ///     2..N, track03.wav 3..N, and so on. Multi-file Redump layouts (one
    ///     file per track) were always correct and are covered by
    ///     <see cref="CueSheet_ParsesMultiFileTracksWithTypesAndIndexes" />.
    /// </summary>
    [Fact]
    public void SingleFileCue_AudioTracksExtractTheirOwnRangeOnly()
    {
        var dataSectors = WrapAsRawMode2(BuildIso9660Sectors(), []);
        var dataSectorCount = dataSectors.Length / 2352;

        // Two audio tracks with distinct fill bytes, appended to the data track
        // in ONE shared .bin.
        const int track2Sectors = 5;
        const int track3Sectors = 3;
        var track2 = new byte[track2Sectors * 2352];
        Array.Fill(track2, (byte)0xA2);
        var track3 = new byte[track3Sectors * 2352];
        Array.Fill(track3, (byte)0xA3);

        var binPath = Path.Combine(_tempDir, "cdda.bin");
        using (var bin = File.Create(binPath))
        {
            bin.Write(dataSectors);
            bin.Write(track2);
            bin.Write(track3);
        }

        static string Msf(int frames)
        {
            return $"{frames / (60 * 75):D2}:{frames / 75 % 60:D2}:{frames % 75:D2}";
        }

        var cuePath = Path.Combine(_tempDir, "cdda.cue");
        File.WriteAllLines(cuePath,
        [
            "FILE \"cdda.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO",
            $"    INDEX 01 {Msf(dataSectorCount)}",
            "  TRACK 03 AUDIO",
            $"    INDEX 01 {Msf(dataSectorCount + track2Sectors)}"
        ]);

        var outDir = Path.Combine(_tempDir, "out_cdda");
        DiscImageArchive.ExtractFiles(
            cuePath,
            outDir,
            cancellationToken: TestContext.Current.CancellationToken);

        var wav2 = File.ReadAllBytes(Path.Combine(outDir, "cdda", "track02.wav"));
        var wav3 = File.ReadAllBytes(Path.Combine(outDir, "cdda", "track03.wav"));

        // 44-byte WAV header + exactly this track's sectors, no bleed-through.
        Assert.Equal(44 + track2Sectors * 2352, wav2.Length);
        Assert.Equal(44 + track3Sectors * 2352, wav3.Length);
        Assert.All(wav2.Skip(44), static value => Assert.Equal(0xA2, value));
        Assert.All(wav3.Skip(44), static value => Assert.Equal(0xA3, value));
    }

    [Fact]
    public void RawBinCue_Form2FileExtractsAsXaSectorStream()
    {
        // Mark HELLO.TXT's data sector (LBA 22) as Form2 — the extractor must
        // emit the whole file as 2336-byte sector units (subheader + payload)
        // so STR/XA consumers keep their interleaved audio sectors.
        var binPath = Path.Combine(_tempDir, "xa.bin");
        File.WriteAllBytes(binPath, WrapAsRawMode2(BuildIso9660Sectors(), [22]));

        var cuePath = Path.Combine(_tempDir, "xa.cue");
        File.WriteAllLines(cuePath,
        [
            "FILE \"xa.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00"
        ]);

        var outDir = Path.Combine(_tempDir, "out_xa");
        DiscImageArchive.ExtractFiles(
            cuePath,
            outDir,
            cancellationToken: TestContext.Current.CancellationToken);

        var xaFile = File.ReadAllBytes(Path.Combine(outDir, "HELLO.TXT"));
        Assert.Equal(2336, xaFile.Length);
        Assert.Equal(0x20, xaFile[2]); // submode byte inside the preserved subheader
        Assert.Equal("Hello, disc!\n"u8.ToArray(), xaFile.AsSpan(8, 13).ToArray());

        // The Form1-only file is unaffected.
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(outDir, "DATA", "NESTED.BIN")));
    }

    // ─── Synthetic image builders ─────────────────────────────────────────

    /// <summary>
    ///     Minimal ISO9660 volume: PVD(16), terminator(17), root dir(20),
    ///     "DATA" subdir(21), HELLO.TXT data(22), NESTED.BIN data(23).
    /// </summary>
    private static byte[] BuildIso9660Sectors()
    {
        var image = new byte[24 * 2048];

        // PVD
        var pvd = image.AsSpan(16 * 2048);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        WriteDirRecord(pvd[156..], 20, 2048, true, "\0");

        // Terminator
        var term = image.AsSpan(17 * 2048);
        term[0] = 255;
        "CD001"u8.CopyTo(term[1..]);

        // Root directory
        var root = image.AsSpan(20 * 2048);
        var offset = WriteDirRecord(root, 20, 2048, true, "\0");
        offset += WriteDirRecord(root[offset..], 20, 2048, true, "");
        offset += WriteDirRecord(root[offset..], 21, 2048, true, "DATA");
        WriteDirRecord(root[offset..], 22, 13, false, "HELLO.TXT;1");

        // DATA directory
        var dataDir = image.AsSpan(21 * 2048);
        offset = WriteDirRecord(dataDir, 21, 2048, true, "\0");
        offset += WriteDirRecord(dataDir[offset..], 20, 2048, true, "");
        WriteDirRecord(dataDir[offset..], 23, 5, false, "NESTED.BIN;1");

        // File payloads
        "Hello, disc!\n"u8.CopyTo(image.AsSpan(22 * 2048));
        new byte[] { 1, 2, 3, 4, 5 }.CopyTo(image.AsSpan(23 * 2048));

        return image;
    }

    private static int WriteDirRecord(Span<byte> target, uint extentLba, uint size, bool isDirectory, string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var length = 33 + nameBytes.Length;
        if (length % 2 != 0) length++;

        target[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(target[2..], extentLba);
        BinaryPrimitives.WriteUInt32BigEndian(target[6..], extentLba);
        BinaryPrimitives.WriteUInt32LittleEndian(target[10..], size);
        BinaryPrimitives.WriteUInt32BigEndian(target[14..], size);
        target[25] = isDirectory ? (byte)0x02 : (byte)0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(target[28..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(target[30..], 1);
        target[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(target[33..]);
        return length;
    }

    /// <summary>Wraps 2048-byte logical sectors as raw 2352-byte Mode2 sectors.</summary>
    private static byte[] WrapAsRawMode2(byte[] logical, HashSet<int> form2Sectors)
    {
        var sectorCount = logical.Length / 2048;
        var raw = new byte[sectorCount * 2352];

        for (var i = 0; i < sectorCount; i++)
        {
            var sector = raw.AsSpan(i * 2352);
            // Sync pattern
            sector[1..11].Fill(0xFF);
            sector[15] = 2; // Mode 2
            var submode = form2Sectors.Contains(i) ? (byte)0x20 : (byte)0x08;
            sector[18] = submode;
            sector[22] = submode; // duplicated subheader copy
            logical.AsSpan(i * 2048, 2048).CopyTo(sector[24..]);
        }

        return raw;
    }
}