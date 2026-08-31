using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using System.Buffers.Binary;
using System.Text;
using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

internal static class SampleGeneratorDiscOperations
{
    private const uint GameCubeDiscMagic = 0xC2339F3D;
    private const int GameCubeBootBinSize = 0x440;
    private const int GameCubeBi2Offset = GameCubeBootBinSize;
    private const int GameCubeBi2Size = 0x2000;
    private const int GameCubeApploaderOffset = 0x2440;
    private const int GameCubeApploaderHeaderSize = 0x20;
    private const int GameCubeDolOffsetOffset = 0x420;
    private const int GameCubeFstOffsetOffset = 0x424;
    private const int GameCubeFstSizeOffset = 0x428;

    /// <summary>
    /// Extracts one or more disc images into a destination directory, preserving the on-disc
    /// directory tree. Multi-disc games get one subdirectory per image.
    /// </summary>
    internal static int ExtractBuildFromDiscImages(IReadOnlyList<string> imagePaths, string destDir)
    {
        if (imagePaths.Count == 0) return 0;

        destDir = SampleGeneratorPathSafety.ResolveConfiguredOutputDirectory(destDir);
        SampleGeneratorBuildOperations.DeleteTree(destDir);
        Directory.CreateDirectory(destDir);

        var count = 0;
        if (imagePaths.Count == 1)
        {
            count += ExtractFilesFromDiscImage(imagePaths[0], destDir);
        }
        else
        {
            foreach (var imagePath in imagePaths)
            {
                var subdir = SampleGeneratorPathSafety.ResolveDestinationPath(
                    destDir,
                    Path.GetFileNameWithoutExtension(imagePath));
                count += ExtractFilesFromDiscImage(imagePath, subdir);
            }
        }

        return count;
    }

    internal static IReadOnlyList<string> FindDiscImagePaths(string buildName)
    {
        var mediaDir = Path.Combine(ResearchMedia, buildName);
        if (Directory.Exists(mediaDir))
        {
            var images = Directory.GetFiles(mediaDir, "*.iso", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (images.Length > 0)
            {
                // A build may mix image formats on one shelf: THPS4 PC ships CD1 as a
                // 2048-byte-sector ISO and CD2 as a raw 2352 BIN/CUE. Data-track BINs
                // whose stems don't collide with an ISO join the set instead of being
                // shadowed by the ISO-first probe; the GDI/IMG fallbacks below stay
                // reachable only when no ISO exists, exactly as before (a GDI dump's
                // raw track .bin files must keep resolving to the .gdi manifest).
                var isoStems = images.Select(Path.GetFileNameWithoutExtension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return images
                    .Concat(Directory.GetFiles(mediaDir, "*.bin", SearchOption.TopDirectoryOnly)
                        .Where(path => !isoStems.Contains(Path.GetFileNameWithoutExtension(path)) &&
                                       IsDataTrackBin(path)))
                    .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            // Dreamcast GDI: a manifest pointing at multiple raw track files. The high-density
            // area (LBA >= 45000) holds the ISO 9660 volume — but the PVD lives on one track
            // and file extents may reference another, so we present the whole HD area as a
            // sparse virtual disc via GdiHighDensityStream.
            var gdiFiles = Directory.GetFiles(mediaDir, "*.gdi", SearchOption.TopDirectoryOnly);
            if (gdiFiles.Length > 0) return [gdiFiles[0]];

            var binFiles = Directory.GetFiles(mediaDir, "*.bin", SearchOption.TopDirectoryOnly)
                .Where(IsDataTrackBin)
                .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (binFiles.Length > 0) return binFiles;

            // CloneCD: the .img is a renamed BIN (raw 2352-byte sectors with sync headers).
            var imgFiles = Directory.GetFiles(mediaDir, "*.img", SearchOption.TopDirectoryOnly)
                .Where(IsDataTrackBin)
                .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (imgFiles.Length > 0) return imgFiles;
        }

        return [];
    }

    /// <summary>
    ///     Stream wrapper that presents a Dreamcast GDI as a single raw 2352-byte/sector disc.
    ///     DC writes the ISO 9660 PVD at LBA 45016 (sector 16 of the high-density area at LBA 45000),
    ///     not at the standard LBA 16. References inside the PVD use absolute physical LBAs.
    ///     To make a stock ISO 9660 reader find the volume, we redirect virtual sectors 16..17
    ///     (where it scans for descriptors) to physical LBAs 45016..45017 (where DC put them);
    ///     for every other sector, virtual N == physical N so the PVD's own LBA references
    ///     resolve correctly. Sectors not covered by any data track return zeroed raw sectors.
    /// </summary>
    internal sealed class GdiHighDensityStream : Stream
    {
        private const int RawSectorSize = 2352;
        private const long DcHighDensityStartLba = 45000;

        private readonly List<TrackEntry> _tracks;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        private sealed class TrackEntry
        {
            public required long StartLba { get; init; }
            public required long EndLba { get; init; }
            public required FileStream Stream { get; init; }
        }

        public GdiHighDensityStream(string gdiPath)
        {
            var dir = Path.GetDirectoryName(gdiPath)
                      ?? throw new ArgumentException("GDI path has no parent directory.", nameof(gdiPath));

            _tracks = [];
            long maxLba = DcHighDensityStartLba;

            foreach (var rawLine in File.ReadAllLines(gdiPath).Skip(1))
            {
                var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!long.TryParse(parts[1], out var lba)) continue;
                if (parts[2] != "4") continue;          // skip audio tracks
                if (lba < DcHighDensityStartLba) continue; // skip low-density (LBA 0..44999)

                var trackName = parts[4].Trim('"');
                var trackPath = Path.Combine(dir, trackName);
                if (!File.Exists(trackPath)) continue;

                var fileSize = new FileInfo(trackPath).Length;
                if (fileSize <= 0) continue;
                var sectors = fileSize / RawSectorSize;

                _tracks.Add(new TrackEntry
                {
                    StartLba = lba,
                    EndLba = lba + sectors,
                    Stream = File.OpenRead(trackPath),
                });

                if (lba + sectors > maxLba) maxLba = lba + sectors;
            }

            if (_tracks.Count == 0)
                throw new InvalidDataException(
                    $"GDI manifest '{Path.GetFileName(gdiPath)}' has no usable high-density data tracks.");

            _length = maxLba * RawSectorSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            var sectorBuf = new byte[RawSectorSize];

            while (count > 0 && _position < _length)
            {
                var virtualSector = _position / RawSectorSize;
                var sectorOffset = (int)(_position % RawSectorSize);
                var toRead = (int)Math.Min(count, RawSectorSize - sectorOffset);

                // Redirect ISO 9660 descriptor scan (sectors 16..17) to the DC HD area's PVD/VDST.
                // For every other sector, expose physical LBA == virtual sector so the absolute
                // LBA references inside the PVD resolve straight through.
                var physicalLba = virtualSector switch
                {
                    16 => DcHighDensityStartLba + 16,
                    17 => DcHighDensityStartLba + 17,
                    _ => virtualSector,
                };

                Array.Clear(sectorBuf);

                foreach (var track in _tracks)
                {
                    if (physicalLba >= track.StartLba && physicalLba < track.EndLba)
                    {
                        track.Stream.Position = (physicalLba - track.StartLba) * RawSectorSize;
                        track.Stream.ReadExactly(sectorBuf);
                        break;
                    }
                }

                Buffer.BlockCopy(sectorBuf, sectorOffset, buffer, offset, toRead);
                offset += toRead;
                count -= toRead;
                _position += toRead;
                totalRead += toRead;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => _position
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    foreach (var track in _tracks)
                        track.Stream.Dispose();
                    _tracks.Clear();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    ///     Known nonzero game-partition byte offsets in full Xbox 360 XGD dumps
    ///     (the base-0 XISO case is handled by <see cref="SampleGeneratorXboxIsoOperations" />).
    ///     0xFD90000 is the redump XGD2 layout, measured on the Proving Ground
    ///     X360 dump; the rest mirror the main tool's historical candidates.
    /// </summary>
    private static readonly long[] Xbox360GamePartitionBases =
        [0xFD90000, 0x18300000, 0x1FB20000, 0x2EE80000];

    private static bool IsXbox360XgdIso(string imagePath)
    {
        try
        {
            using var fs = File.OpenRead(imagePath);
            Span<byte> buf = stackalloc byte[20];
            foreach (var partitionBase in Xbox360GamePartitionBases)
            {
                var magicPos = partitionBase + 0x10000;
                if (magicPos + buf.Length > fs.Length) continue;
                fs.Position = magicPos;
                fs.ReadExactly(buf);
                if (buf.SequenceEqual("MICROSOFT*XBOX*MEDIA"u8)) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPs3DiscImage(string imagePath)
    {
        try
        {
            using var fs = File.OpenRead(imagePath);
            if (fs.Length < 0x800 + 12) return false;
            fs.Position = 0x800;
            Span<byte> buf = stackalloc byte[12];
            fs.ReadExactly(buf);
            return buf.SequenceEqual("PlayStation3"u8);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Extracts a PS3 disc image by shelling to 7z. 7z exits with code 2 on
    ///     these images (a header warning for the redump trailer) even though
    ///     every file extracts, so success is judged by comparing the extracted
    ///     file count against the archive listing instead of the exit code.
    /// </summary>
    private static int ExtractPs3Disc(string imagePath, string destDir)
    {
        var expected = ListPs3FileCount(imagePath);
        if (expected == 0)
            throw new InvalidDataException($"7z listed no files inside {Path.GetFileName(imagePath)}.");

        Run7z(["x", imagePath, $"-o{destDir}", "-y"]);

        var extracted = Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories).Count();
        if (extracted < expected)
        {
            throw new InvalidDataException(
                $"PS3 extraction incomplete for {Path.GetFileName(imagePath)}: " +
                $"{extracted} of {expected} listed files.");
        }

        return extracted;
    }

    private static int ListPs3FileCount(string imagePath)
    {
        var output = Run7z(["l", "-ba", imagePath]);
        var count = 0;
        foreach (var line in output.Split('\n'))
        {
            // Listing rows: "2006-10-05 22:28:00 .....  1536  2048  PS3_DISC.SFB";
            // the attribute column carries 'D' for directories.
            if (line.Length < 25) continue;
            var attr = line.Substring(20, 5);
            if (attr.Contains('.') && !attr.Contains('D')) count++;
        }

        return count;
    }

    /// <summary>Runs 7z tolerating nonzero exits (PS3 images warn); returns stdout.</summary>
    private static string Run7z(IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "7z",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start 7z.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        errorTask.GetAwaiter().GetResult();
        return outputTask.GetAwaiter().GetResult();
    }

    private static bool IsRawSectorImage(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            return size > 0 && size % 2352 == 0 && IsDataTrackBin(path);
        }
        catch { return false; }
    }

    private static bool IsDataTrackBin(string binPath)
    {
        try
        {
            using var fs = File.OpenRead(binPath);
            Span<byte> sync = stackalloc byte[12];
            if (fs.Read(sync) < 12) return false;
            return sync[0] == 0x00
                   && sync[1] == 0xFF && sync[2] == 0xFF && sync[3] == 0xFF
                   && sync[10] == 0xFF && sync[11] == 0x00;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Wii encrypted-partition ISOs go through the main tool's reader
    ///     (WiiDisc + WiiPartitionStream); the NEVERSOFT_WII_COMMON_KEY common key
    ///     must be provisioned. Returns false for non-Wii images so the caller
    ///     falls through to the ordinary GC/ISO paths.
    /// </summary>
    private static bool TryExtractWiiDisc(string imagePath, string destDir, out int count)
    {
        count = 0;
        using (var probe = File.OpenRead(imagePath))
        {
            if (!NeversoftMultitool.Core.Formats.DiscImage.WiiDisc.IsWii(probe))
                return false;
        }

        NeversoftMultitool.Core.Formats.DiscImage.DiscImageArchive.ExtractFiles(
            imagePath, destDir, null, default);
        count = Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories).Count();
        return true;
    }

    internal static int ExtractFilesFromDiscImage(string imagePath, string destDir)
    {
        destDir = SampleGeneratorPathSafety.ResolveConfiguredOutputDirectory(destDir);
        Directory.CreateDirectory(destDir);

        // Wii discs are encrypted-partition ISOs the local DiscUtils path cannot
        // read — delegate to the main tool's reader (needs the common key).
        if (TryExtractWiiDisc(imagePath, destDir, out var wiiCount))
            return wiiCount;

        // Xbox ISO (XISO/XGD1): proprietary AVL-tree filesystem at offset 0x10000.
        // Detect by file content (magic), not by path heuristics.
        if (SampleGeneratorXboxIsoOperations.IsXboxIso(imagePath))
            return SampleGeneratorXboxIsoOperations.ExtractFiles(imagePath, destDir);

        // Xbox 360 full XGD dumps front-load a video partition, so the game
        // partition's XDVDFS descriptor sits past one of the known nonzero
        // bases — delegate to the main tool's XDVDFS reader, which probes them.
        if (IsXbox360XgdIso(imagePath))
        {
            NeversoftMultitool.Core.Formats.DiscImage.DiscImageArchive.ExtractFiles(
                imagePath, destDir, null, default);
            return Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories).Count();
        }

        // PS3 Blu-ray images (UDF 2.50 with a metadata partition): DiscUtils'
        // UdfReader rejects them ("Only EFE implemented for Metadata file
        // entry") and the ISO 9660 stub PVD names no files, so extraction
        // shells to 7z, which reads UDF 2.50.
        if (IsPs3DiscImage(imagePath))
            return ExtractPs3Disc(imagePath, destDir);

        // GameCube discs use a proprietary FST, NOT ISO 9660. Detect first and route
        // directly to the GC walker so we don't waste time on a CDReader that will throw.
        using (var gcProbe = File.OpenRead(imagePath))
        {
            if (TryReadGameCubeDiscInfo(gcProbe, out var gcInfo))
            {
                var systemDir = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, SystemDirName);
                var systemCount = ExtractGameCubeSystemFiles(gcProbe, gcInfo, systemDir);
                var fstCount = ExtractGameCubeFilesystem(gcProbe, gcInfo, destDir);
                return systemCount + fstCount;
            }
        }

        var isGdi = imagePath.EndsWith(".gdi", StringComparison.OrdinalIgnoreCase);
        var isBinCue = !isGdi && (imagePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                                  || imagePath.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                                  || IsRawSectorImage(imagePath));

        using Stream rawStream = isGdi
            ? new GdiHighDensityStream(imagePath)
            : File.OpenRead(imagePath);

        Stream isoStream = (isBinCue || isGdi) ? new RawBinToIsoStream(rawStream) : rawStream;
        DiscFileSystem fs;
        try
        {
            fs = OpenIsoFilesystem(isoStream);
        }
        catch
        {
            // GDI / BIN / CUE / IMG that isn't a valid ISO 9660 disc — bail with zero files.
            if (isBinCue || isGdi) return 0;
            throw;
        }

        var imageFiles = fs.GetFiles(@"\", "*.*", SearchOption.AllDirectories);
        var count = 0;

        foreach (var imageFile in imageFiles)
        {
            var cleanPath = imageFile;
            var semiColon = cleanPath.LastIndexOf(';');
            if (semiColon >= 0)
                cleanPath = cleanPath[..semiColon];

            var relativePath = cleanPath.TrimStart('\\', '/');
            var destFile = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, relativePath);
            var destFileDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFileDir))
                Directory.CreateDirectory(destFileDir);

            var ext = Path.GetExtension(cleanPath);
            if (isBinCue && IsXaInterleaved(ext))
            {
                var fileLba = GetFileLba(fs, imageFile);
                var fileSize = GetFileSize(fs, imageFile);
                ExtractRawMode2File(imagePath, fileLba, fileSize, destFile);
            }
            else
            {
                using var srcStream = fs.OpenFile(imageFile, FileMode.Open, FileAccess.Read);
                using var dstStream = File.Create(destFile);
                srcStream.CopyTo(dstStream);
            }

            count++;
        }

        return count;
    }

    /// <summary>XA-interleaved file extensions that need raw Mode 2 Form 2 extraction.</summary>
    private static bool IsXaInterleaved(string ext) =>
        ext.Equals(".STR", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".XA", StringComparison.OrdinalIgnoreCase);

    private static long GetFileLba(DiscFileSystem fs, string imageFile)
    {
        if (fs is CDReader cd)
        {
            var ranges = cd.PathToClusters(imageFile);
            if (ranges.Length > 0)
                return ranges[0].Offset;
        }

        return -1;
    }

    private static long GetFileSize(DiscFileSystem fs, string imageFile)
    {
        using var stream = fs.OpenFile(imageFile, FileMode.Open, FileAccess.Read);
        return stream.Length;
    }

    /// <summary>
    ///     Extracts a file from a raw BIN image preserving Mode 2 Form 2 sector framing.
    ///     Each 2352-byte raw sector becomes 2336 bytes: subheader(8) + user data(2328).
    ///     The sync(12) and header(4) are stripped.
    /// </summary>
    private static void ExtractRawMode2File(string binPath, long startLba, long isoFileSize, string destFile)
    {
        const int rawSectorSize = 2352;
        const int mode2SectorPayload = 2336;

        var numSectors = (isoFileSize + 2047) / 2048;

        using var binStream = File.OpenRead(binPath);
        using var outStream = File.Create(destFile);

        var buffer = new byte[rawSectorSize];
        var sectorOffset = startLba * rawSectorSize;

        for (long s = 0; s < numSectors; s++)
        {
            binStream.Position = sectorOffset + s * rawSectorSize;
            if (binStream.Read(buffer, 0, rawSectorSize) < rawSectorSize)
                break;

            outStream.Write(buffer, 16, mode2SectorPayload);
        }
    }

    /// <summary>
    ///     Stream wrapper that presents a raw 2352-byte BIN image as a Mode 1 ISO stream
    ///     (2048 bytes per sector) so DiscUtils CDReader can parse the ISO 9660 filesystem.
    /// </summary>
    private sealed class RawBinToIsoStream : Stream
    {
        private readonly Stream _binStream;
        private long _position;
        private readonly long _length;

        private const int RawSectorSize = 2352;
        private const int Mode1DataOffset = 16;
        private const int Mode2DataOffset = 24;
        private const int LogicalSectorSize = 2048;

        public RawBinToIsoStream(Stream binStream)
        {
            _binStream = binStream;
            _length = binStream.Length / RawSectorSize * LogicalSectorSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            var rawSector = new byte[RawSectorSize];
            while (count > 0 && _position < _length)
            {
                var sector = _position / LogicalSectorSize;
                var sectorOffset = (int)(_position % LogicalSectorSize);
                var toRead = Math.Min(count, LogicalSectorSize - sectorOffset);

                _binStream.Position = sector * RawSectorSize;
                if (_binStream.Read(rawSector, 0, RawSectorSize) < RawSectorSize) break;

                var mode = rawSector[15];
                var dataOffset = mode == 2 ? Mode2DataOffset : Mode1DataOffset;

                Buffer.BlockCopy(rawSector, dataOffset + sectorOffset, buffer, offset, toRead);

                offset += toRead;
                count -= toRead;
                _position += toRead;
                totalRead += toRead;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => _position
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static DiscFileSystem OpenIsoFilesystem(Stream isoStream)
    {
        try
        {
            var cd = new CDReader(isoStream, true);
            cd.GetFiles(@"\");
            return cd;
        }
        catch
        {
            if (!isoStream.CanSeek) throw;
            isoStream.Position = 0;
            try
            {
                return new UdfReader(isoStream);
            }
            catch
            {
                isoStream.Position = 0;
                return new CDReader(isoStream, false);
            }
        }
    }

    private static int ExtractGameCubeSystemFiles(Stream imageStream, GameCubeDiscInfo info, string destDir)
    {
        Directory.CreateDirectory(destDir);

        var count = 0;
        count += CopyStreamRange(imageStream, 0, GameCubeBootBinSize,
            SampleGeneratorPathSafety.ResolveDestinationPath(destDir, "boot.bin"));
        count += CopyStreamRange(imageStream, GameCubeBi2Offset, GameCubeBi2Size,
            SampleGeneratorPathSafety.ResolveDestinationPath(destDir, "bi2.bin"));
        count += CopyStreamRange(imageStream, GameCubeApploaderOffset, info.ApploaderLength,
            SampleGeneratorPathSafety.ResolveDestinationPath(destDir, "apploader.img"));
        count += CopyStreamRange(imageStream, info.FstOffset, info.FstLength,
            SampleGeneratorPathSafety.ResolveDestinationPath(destDir, "fst.bin"));
        count += CopyStreamRange(imageStream, info.DolOffset, info.DolLength,
            SampleGeneratorPathSafety.ResolveDestinationPath(destDir, "main.dol"));
        imageStream.Position = 0;
        return count;
    }

    /// <summary>
    ///     Walks the GameCube FST and extracts every file in the disc filesystem under destDir,
    ///     preserving the on-disc directory tree. The FST is a flat array of 12-byte entries with
    ///     a string table appended; entry 0 is the root and contains the total entry count.
    /// </summary>
    private static int ExtractGameCubeFilesystem(Stream imageStream, GameCubeDiscInfo info, string destDir)
    {
        if (info.FstLength < 12) return 0;

        var fst = new byte[info.FstLength];
        imageStream.Position = info.FstOffset;
        imageStream.ReadExactly(fst);

        // Root entry: bytes 8-11 hold the total entry count (including root).
        var totalEntries = (int)BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8, 4));
        var stringTableOffset = totalEntries * 12;
        if (totalEntries < 1 || stringTableOffset < 0 || stringTableOffset > info.FstLength) return 0;

        // Directory stack: (endIndex, accumulatedRelativePath). Root spans [1, totalEntries).
        var dirStack = new Stack<(int EndIndex, string Path)>();
        dirStack.Push((totalEntries, string.Empty));

        Directory.CreateDirectory(destDir);

        var count = 0;
        for (var i = 1; i < totalEntries; i++)
        {
            while (dirStack.Count > 0 && dirStack.Peek().EndIndex <= i)
                dirStack.Pop();

            if (dirStack.Count == 0) break;

            var entryOffset = i * 12;
            var type = fst[entryOffset];
            var nameOffset = (fst[entryOffset + 1] << 16) | (fst[entryOffset + 2] << 8) | fst[entryOffset + 3];
            var fileOrParent = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(entryOffset + 4, 4));
            var sizeOrNext = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(entryOffset + 8, 4));

            var name = SampleGeneratorPathSafety.SanitizeDiscPathSegment(
                ReadCString(fst, stringTableOffset + nameOffset));
            if (string.IsNullOrEmpty(name)) continue;

            var parentPath = dirStack.Peek().Path;
            var relativePath = parentPath.Length == 0 ? name : Path.Combine(parentPath, name);

            if (type == 1)
            {
                // Directory: nextEntry = sizeOrNext (index of first entry after this dir's children).
                var nextEntry = (int)sizeOrNext;
                if (nextEntry <= i || nextEntry > totalEntries) continue;
                dirStack.Push((nextEntry, relativePath));
            }
            else
            {
                if (!RangeFits(imageStream.Length, fileOrParent, sizeOrNext)) continue;

                var destPath = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, relativePath);
                var destFileDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destFileDir))
                    Directory.CreateDirectory(destFileDir);

                using var output = File.Create(destPath);
                if (sizeOrNext > 0)
                {
                    imageStream.Position = fileOrParent;
                    CopyStreamBytes(imageStream, output, sizeOrNext);
                }
                count++;
            }
        }

        imageStream.Position = 0;
        return count;
    }

    private static string ReadCString(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length) return string.Empty;
        var end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static void CopyStreamBytes(Stream src, Stream dst, long count)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = src.Read(buffer, 0, toRead);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected EOF while extracting GameCube file.");
            dst.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool TryReadGameCubeDiscInfo(Stream imageStream, out GameCubeDiscInfo info)
    {
        info = default;

        if (!imageStream.CanSeek || imageStream.Length < GameCubeApploaderOffset + GameCubeApploaderHeaderSize)
            return false;

        Span<byte> bootBin = stackalloc byte[GameCubeBootBinSize];
        imageStream.Position = 0;
        imageStream.ReadExactly(bootBin);

        if (ReadBigEndianUInt32(bootBin[0x1C..0x20]) != GameCubeDiscMagic)
            return false;

        var dolOffset = ReadBigEndianUInt32(bootBin[GameCubeDolOffsetOffset..(GameCubeDolOffsetOffset + 4)]);
        var fstOffset = ReadBigEndianUInt32(bootBin[GameCubeFstOffsetOffset..(GameCubeFstOffsetOffset + 4)]);
        var fstLengthValue = ReadBigEndianUInt32(bootBin[GameCubeFstSizeOffset..(GameCubeFstSizeOffset + 4)]);
        if (fstLengthValue > int.MaxValue)
            return false;

        var fstLength = (int)fstLengthValue;
        var apploaderLength = ReadGameCubeApploaderLength(imageStream);
        var dolLength = ReadDolLength(imageStream, dolOffset);

        if (apploaderLength <= 0 || dolLength <= 0 || fstLength <= 0)
            return false;

        if (!RangeFits(imageStream.Length, 0, GameCubeBootBinSize) ||
            !RangeFits(imageStream.Length, GameCubeBi2Offset, GameCubeBi2Size) ||
            !RangeFits(imageStream.Length, GameCubeApploaderOffset, apploaderLength) ||
            !RangeFits(imageStream.Length, fstOffset, fstLength) ||
            !RangeFits(imageStream.Length, dolOffset, dolLength))
            return false;

        var gameId = Encoding.ASCII.GetString(bootBin[..6]);
        var gameNameBytes = bootBin[0x20..0x60];
        var nullIndex = gameNameBytes.IndexOf((byte)0);
        var gameName = Encoding.ASCII.GetString(nullIndex >= 0 ? gameNameBytes[..nullIndex] : gameNameBytes);

        info = new GameCubeDiscInfo(gameId, gameName, dolOffset, dolLength, fstOffset, fstLength, apploaderLength);
        imageStream.Position = 0;
        return true;
    }

    private static int ReadGameCubeApploaderLength(Stream imageStream)
    {
        Span<byte> apploaderHeader = stackalloc byte[GameCubeApploaderHeaderSize];
        imageStream.Position = GameCubeApploaderOffset;
        imageStream.ReadExactly(apploaderHeader);

        var bodySize = ReadBigEndianUInt32(apploaderHeader[0x14..0x18]);
        var trailerSize = ReadBigEndianUInt32(apploaderHeader[0x18..0x1C]);
        var totalLength = (long)GameCubeApploaderHeaderSize + bodySize + trailerSize;
        return totalLength is > 0 and <= int.MaxValue ? (int)totalLength : 0;
    }

    private static int ReadDolLength(Stream imageStream, long dolOffset)
    {
        if (!RangeFits(imageStream.Length, dolOffset, 0x100))
            return 0;

        Span<byte> dolHeader = stackalloc byte[0x100];
        imageStream.Position = dolOffset;
        imageStream.ReadExactly(dolHeader);

        long maxEnd = 0x100;
        for (var i = 0; i < 18; i++)
        {
            var sectionOffset = ReadBigEndianUInt32(dolHeader[(i * 4)..((i * 4) + 4)]);
            var sectionSize = ReadBigEndianUInt32(dolHeader[(0x90 + (i * 4))..(0x94 + (i * 4))]);
            if (sectionSize == 0)
                continue;

            var end = (long)sectionOffset + sectionSize;
            if (end > maxEnd)
                maxEnd = end;
        }

        return maxEnd is > 0 and <= int.MaxValue ? (int)maxEnd : 0;
    }

    private static int CopyStreamRange(Stream imageStream, long offset, int length, string outputPath)
    {
        if (length <= 0 || !RangeFits(imageStream.Length, offset, length))
            return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        imageStream.Position = offset;
        using var output = File.Create(outputPath);

        var remaining = length;
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var chunkSize = Math.Min(buffer.Length, remaining);
            var read = imageStream.Read(buffer, 0, chunkSize);
            if (read <= 0)
                throw new EndOfStreamException($"Unexpected EOF while copying {outputPath}.");

            output.Write(buffer, 0, read);
            remaining -= read;
        }

        return 1;
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);

    private static bool RangeFits(long totalLength, long offset, long length) =>
        offset >= 0 && length >= 0 && offset <= totalLength && length <= totalLength - offset;

    private readonly record struct GameCubeDiscInfo(
        string GameId,
        string GameName,
        long DolOffset,
        int DolLength,
        long FstOffset,
        int FstLength,
        int ApploaderLength);
}
