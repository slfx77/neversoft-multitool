namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record GdiTrack(
    int Number,
    long StartLba,
    bool IsData,
    int SectorSize,
    string FilePath)
{
    public long SectorCount => File.Exists(FilePath) ? new FileInfo(FilePath).Length / SectorSize : 0;
}

/// <summary>
///     Dreamcast GDI descriptor: a track count line followed by
///     "trackNo startLba type sectorSize filename offset" lines. The
///     high-density data area starts at LBA 45000, and the ISO9660 volume
///     descriptors there reference absolute LBAs — the track-mapped sector
///     source resolves them naturally.
/// </summary>
public sealed class GdiSheet
{
    private GdiSheet(IReadOnlyList<GdiTrack> tracks)
    {
        Tracks = tracks;
    }

    public IReadOnlyList<GdiTrack> Tracks { get; }

    /// <summary>LBA of the high-density data session (usually 45000).</summary>
    public long DataSessionLba =>
        Tracks.Where(t => t.IsData && t.StartLba >= 45000)
            .Select(t => t.StartLba)
            .DefaultIfEmpty(Tracks.First(t => t.IsData).StartLba)
            .First();

    public static GdiSheet Parse(string gdiPath)
    {
        return Parse(File.ReadAllLines(gdiPath), Path.GetDirectoryName(gdiPath) ?? "");
    }

    public static GdiSheet Parse(IReadOnlyList<string> lines, string baseDirectory)
    {
        var tracks = new List<GdiTrack>();
        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Filenames may be quoted (and may contain spaces when quoted).
            string fileName;
            string[] head;
            var quoteStart = line.IndexOf('"');
            if (quoteStart >= 0)
            {
                var quoteEnd = line.IndexOf('"', quoteStart + 1);
                if (quoteEnd < 0) continue;
                fileName = line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                head = line[..quoteStart].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
            else
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;
                fileName = parts[4];
                head = parts;
            }

            if (head.Length < 4) continue;
            if (!int.TryParse(head[0], out var number)) continue;
            if (!long.TryParse(head[1], out var startLba)) continue;
            if (!int.TryParse(head[2], out var type)) continue;
            if (!int.TryParse(head[3], out var sectorSize)) continue;

            tracks.Add(new GdiTrack(
                number,
                startLba,
                type == 4,
                sectorSize,
                Path.Combine(baseDirectory, fileName)));
        }

        if (tracks.Count == 0)
            throw new InvalidDataException("GDI sheet contains no tracks.");

        return new GdiSheet(tracks);
    }

    public List<DiscTrackRegion> BuildRegions()
    {
        return Tracks
            .Where(t => t.SectorCount > 0)
            .Select(t => new DiscTrackRegion(t.StartLba, t.SectorCount, t.FilePath, 0, t.SectorSize, !t.IsData))
            .ToList();
    }
}
