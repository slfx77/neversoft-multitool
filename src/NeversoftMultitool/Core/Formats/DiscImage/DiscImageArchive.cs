using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Disc image reader with the standard archive contract: plain .iso
///     (ISO9660 for PS1/PS2/DC/PC, XDVDFS for Xbox, GCM for GameCube),
///     .cue+.bin (PS1/PC, single- or multi-file), .img+.ccd (CloneCD), and
///     .gdi (Dreamcast). Data files extract through the filesystem; files
///     containing Mode2 Form2 sectors (PS1 STR/XA streams) extract as
///     2336-byte-sector streams so the existing STR/XA tooling can consume
///     them; CD audio tracks extract as WAV.
/// </summary>
public static class DiscImageArchive
{
    private const int SectorSize = 2048;
    private const int XaSectorSize = 2336;
    private const int RawSectorSize = 2352;

    private static readonly byte[] SyncPattern =
        [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>
    ///     Extension + content gate. `.img` needs a same-stem `.ccd` sibling
    ///     (bare .img files elsewhere in the corpus are PS2 IOP modules) and
    ///     `.bin` needs a raw-sector sync pattern (bare .bin files are
    ///     Dreamcast executables) — both are also covered by opening the
    ///     .cue/.gdi that references them.
    /// </summary>
    public static bool IsDiscImage(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".iso" => SniffIso(path),
                ".cue" => TryParseCue(path) != null,
                ".gdi" => TryParseGdi(path) != null,
                ".img" => File.Exists(Path.ChangeExtension(path, ".ccd")) && HasSyncPattern(path),
                ".bin" => HasSyncPattern(path),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    public static List<ArchiveEntry> GetFileList(string path)
    {
        using var disc = OpenedDisc.Open(path);
        var entries = disc.Files
            .Select(file => new ArchiveEntry
            {
                Name = file.Name,
                Directory = file.Directory,
                Size = file.Size
            })
            .ToList();

        foreach (var (number, region) in disc.AudioTracks)
        {
            entries.Add(new ArchiveEntry
            {
                Name = $"track{number:D2}.wav",
                Directory = "cdda",
                Size = region.SectorCountValue * RawSectorSize + 44
            });
        }

        return entries;
    }

    public static void ExtractFiles(
        string path,
        string outputDir,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var disc = OpenedDisc.Open(path);
        var total = disc.Files.Count + disc.AudioTracks.Count;
        var current = 0;
        var outputRoot = Path.GetFullPath(outputDir);
        var filePaths = new string[disc.Files.Count];
        for (var i = 0; i < disc.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filePaths[i] = ArchiveExtractionPath.GetContainedPath(
                outputRoot, disc.Files[i].FullPath, "Disc file");
        }

        var audioPaths = new string[disc.AudioTracks.Count];
        for (var i = 0; i < disc.AudioTracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = disc.AudioTracks[i].Number;
            audioPaths[i] = ArchiveExtractionPath.GetContainedPath(
                outputRoot, $"cdda/track{number:D2}.wav", "Disc audio track");
        }

        for (var i = 0; i < disc.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;

            var file = disc.Files[i];
            var outPath = filePaths[i];
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            disc.ExtractFile(file, outPath, cancellationToken);
            onProgress?.Invoke(current, total);
        }

        for (var i = 0; i < disc.AudioTracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;

            var (_, region) = disc.AudioTracks[i];
            var outPath = audioPaths[i];
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            ExtractAudioTrack(region, outPath, cancellationToken);
            onProgress?.Invoke(current, total);
        }
    }

    // ─── Detection helpers ────────────────────────────────────────────────

    private static bool SniffIso(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (GcmFileSystem.IsGcm(stream))
            return true;

        using var source = new IsoSectorSource(stream);
        return Iso9660FileSystem.HasVolumeDescriptor(source) ||
               XdvdfsFileSystem.TryFindBase(source, out _);
    }

    private static CueSheet? TryParseCue(string path)
    {
        try
        {
            var cue = CueSheet.Parse(path);
            return cue.Tracks.Any(t => !t.IsAudio) &&
                   cue.Tracks.All(t => File.Exists(t.FilePath))
                ? cue
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static GdiSheet? TryParseGdi(string path)
    {
        try
        {
            var gdi = GdiSheet.Parse(path);
            return gdi.Tracks.Any(t => t.IsData) &&
                   gdi.Tracks.All(t => File.Exists(t.FilePath))
                ? gdi
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasSyncPattern(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < RawSectorSize)
            return false;

        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);
        return head.SequenceEqual(SyncPattern);
    }

    /// <summary>CD-DA: raw 2352-byte sectors are 44.1 kHz 16-bit LE stereo PCM.</summary>
    private static void ExtractAudioTrack(
        DiscTrackRegion region,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(region.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Position = region.FileByteOffset;

        var dataBytes = region.SectorCountValue * region.PhysicalSectorSize;
        using var output = File.Create(outputPath);
        WriteWavHeader(output, dataBytes);

        var buffer = new byte[64 * 1024];
        var remaining = dataBytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, remaining);
            input.ReadExactly(buffer.AsSpan(0, chunk));
            output.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private static void WriteWavHeader(Stream output, long dataBytes)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2); // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        output.Write(header);
    }

    // ─── Opened-disc state ────────────────────────────────────────────────

    private sealed class OpenedDisc : IDisposable
    {
        private Stream? _gcmStream;

        private DiscKind _kind;
        private IDiscSectorSource? _source;

        public List<DiscFileEntry> Files { get; private set; } = [];
        public List<(int Number, DiscTrackRegion Region)> AudioTracks { get; } = [];

        public void Dispose()
        {
            _source?.Dispose();
            _gcmStream?.Dispose();
        }

        public static OpenedDisc Open(string path)
        {
            var disc = new OpenedDisc();
            var ext = Path.GetExtension(path).ToLowerInvariant();

            switch (ext)
            {
                case ".iso":
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (GcmFileSystem.IsGcm(stream))
                    {
                        disc._kind = DiscKind.Gcm;
                        disc._gcmStream = stream;
                        disc.Files = GcmFileSystem.ReadFileList(stream);
                        return disc;
                    }

                    disc._source = new IsoSectorSource(stream);
                    break;
                }

                case ".cue":
                {
                    var cue = CueSheet.Parse(path);
                    disc._source = new RawSectorSource(cue.BuildRegions());
                    disc.CollectAudioTracksFromCue(cue);
                    break;
                }

                case ".gdi":
                {
                    var gdi = GdiSheet.Parse(path);
                    disc._source = new RawSectorSource(gdi.BuildRegions());
                    disc.CollectAudioTracks(((RawSectorSource)disc._source).Tracks);
                    disc._kind = DiscKind.Iso9660;
                    disc.Files = Iso9660FileSystem.ReadFileList(disc._source, gdi.DataSessionLba);
                    return disc;
                }

                case ".img" or ".bin":
                {
                    var length = new FileInfo(path).Length;
                    disc._source = new RawSectorSource(
                    [
                        new DiscTrackRegion(0, length / RawSectorSize, path, 0, RawSectorSize, false)
                    ]);
                    break;
                }

                default:
                    throw new NotSupportedException($"Not a supported disc image: {path}");
            }

            // Identify the filesystem on the sector source. XDVDFS probes
            // FIRST: redump Xbox dumps also carry a small ISO9660 video
            // partition at sector 16, which would otherwise shadow the game
            // partition.
            if (XdvdfsFileSystem.TryFindBase(disc._source!, out var baseSector))
            {
                disc._kind = DiscKind.Xdvdfs;
                disc.Files = XdvdfsFileSystem.ReadFileList(disc._source!, baseSector);
            }
            else if (Iso9660FileSystem.HasVolumeDescriptor(disc._source!))
            {
                disc._kind = DiscKind.Iso9660;
                disc.Files = Iso9660FileSystem.ReadFileList(disc._source!);
            }
            else
            {
                disc.Dispose();
                throw new InvalidDataException("No recognized filesystem (ISO9660/XDVDFS/GCM) on disc image.");
            }

            return disc;
        }

        private void CollectAudioTracksFromCue(CueSheet cue)
        {
            for (var index = 0; index < cue.Tracks.Count; index++)
            {
                var track = cue.Tracks[index];
                if (!track.IsAudio || track.FileLength <= 0)
                    continue;

                // Skip the in-file pregap; INDEX 01 marks where audio begins.
                var startByte = track.Index01Frames * track.SectorSize;

                // A track's extent ends where the NEXT track in the SAME image
                // file begins, not at end-of-image. Single-file cues (one .bin
                // shared by every track) previously ran every audio track to
                // EOF, so track02.wav contained tracks 2..N, track03.wav 3..N,
                // and so on (fixed 2026-08-04). Multi-file Redump layouts have
                // one file per track, where "next track in the same file" never
                // exists and end-of-file remains correct.
                var endByte = track.FileLength;
                for (var next = index + 1; next < cue.Tracks.Count; next++)
                {
                    if (!string.Equals(cue.Tracks[next].FilePath, track.FilePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    endByte = cue.Tracks[next].Index01Frames * cue.Tracks[next].SectorSize;
                    break;
                }

                var sectors = (endByte - startByte) / track.SectorSize;
                if (sectors <= 0)
                    continue;

                AudioTracks.Add((track.Number,
                    new DiscTrackRegion(0, sectors, track.FilePath, startByte, track.SectorSize, true)));
            }
        }

        private void CollectAudioTracks(IReadOnlyList<DiscTrackRegion> regions)
        {
            var number = 0;
            foreach (var region in regions)
            {
                number++;
                if (region.IsAudio)
                    AudioTracks.Add((number, region));
            }
        }

        public void ExtractFile(DiscFileEntry file, string outputPath, CancellationToken cancellationToken)
        {
            if (_kind == DiscKind.Gcm)
            {
                ExtractGcmFile(file, outputPath, cancellationToken);
                return;
            }

            var source = _source!;
            var sectors = (file.Size + SectorSize - 1) / SectorSize;
            var buffer = new byte[SectorSize];

            using var output = File.Create(outputPath);
            long written = 0;
            for (long i = 0; i < sectors; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var submode = source.ReadSector(file.ExtentLba + i, buffer);
                if ((submode & 0x20) != 0 && source.HasRawSectors)
                {
                    // Form2 sector (XA audio / STR stream) — restart the file
                    // as a 2336-byte-sector stream to preserve subheaders and
                    // Form2 payloads for the STR/XA decoders.
                    output.SetLength(0);
                    output.Position = 0;
                    ExtractXaStream(source, file, output, sectors, cancellationToken);
                    return;
                }

                var chunk = (int)Math.Min(SectorSize, file.Size - written);
                output.Write(buffer, 0, chunk);
                written += chunk;
            }
        }

        private static void ExtractXaStream(
            IDiscSectorSource source,
            DiscFileEntry file,
            Stream output,
            long sectors,
            CancellationToken cancellationToken)
        {
            var tail = new byte[XaSectorSize];
            for (long i = 0; i < sectors; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.ReadSectorTail(file.ExtentLba + i, tail);
                output.Write(tail, 0, XaSectorSize);
            }
        }

        private void ExtractGcmFile(DiscFileEntry file, string outputPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = _gcmStream!;
            stream.Position = file.ExtentLba; // raw byte offset for GCM
            using var output = File.Create(outputPath);

            var remaining = file.Size;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(buffer.Length, remaining);
                stream.ReadExactly(buffer.AsSpan(0, chunk));
                output.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }

        private enum DiscKind
        {
            Iso9660,
            Xdvdfs,
            Gcm
        }
    }
}
