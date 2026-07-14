namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record CueTrack(
    int Number,
    string Type,
    string FilePath,
    long FileLength,
    int SectorSize,
    long Index01Frames)
{
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
    public long SectorCount => FileLength / SectorSize;
}

/// <summary>
///     Minimal cue sheet parser: FILE/TRACK/INDEX statements, one or more
///     BINARY files (Redump-style one-file-per-track and single-file layouts).
/// </summary>
public sealed class CueSheet
{
    private CueSheet(IReadOnlyList<CueTrack> tracks)
    {
        Tracks = tracks;
    }

    public IReadOnlyList<CueTrack> Tracks { get; }

    public static CueSheet Parse(string cuePath)
    {
        return Parse(File.ReadAllLines(cuePath), Path.GetDirectoryName(cuePath) ?? "");
    }

    public static CueSheet Parse(IReadOnlyList<string> lines, string baseDirectory)
    {
        var tracks = new List<CueTrack>();
        string? currentFile = null;
        long currentFileLength = 0;
        int? trackNumber = null;
        string? trackType = null;
        long index01 = 0;
        var haveIndex01 = false;

        void FlushTrack()
        {
            if (trackNumber == null || trackType == null || currentFile == null)
                return;

            tracks.Add(new CueTrack(
                trackNumber.Value,
                trackType,
                currentFile,
                currentFileLength,
                SectorSizeFor(trackType),
                haveIndex01 ? index01 : 0));
            trackNumber = null;
            trackType = null;
            index01 = 0;
            haveIndex01 = false;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                FlushTrack();
                var name = ExtractQuoted(line) ?? line[5..].Trim().Split(' ')[0];
                currentFile = Path.Combine(baseDirectory, name);
                currentFileLength = File.Exists(currentFile) ? new FileInfo(currentFile).Length : 0;
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                FlushTrack();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], out var number))
                {
                    trackNumber = number;
                    trackType = parts[2];
                }
            }
            else if (line.StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "01")
                {
                    index01 = ParseMsfFrames(parts[2]);
                    haveIndex01 = true;
                }
            }
        }

        FlushTrack();

        if (tracks.Count == 0)
            throw new InvalidDataException("Cue sheet contains no tracks.");

        return new CueSheet(tracks);
    }

    /// <summary>
    ///     Track regions for the sector source. Files stack sequentially in
    ///     LBA space (Redump layout); the first data track starts at LBA 0.
    /// </summary>
    public List<DiscTrackRegion> BuildRegions()
    {
        var regions = new List<DiscTrackRegion>();
        long lba = 0;
        foreach (var track in Tracks)
        {
            if (track.FileLength <= 0)
                continue;

            regions.Add(new DiscTrackRegion(
                lba,
                track.SectorCount,
                track.FilePath,
                0,
                track.SectorSize,
                track.IsAudio));
            lba += track.SectorCount;
        }

        return regions;
    }

    private static int SectorSizeFor(string trackType)
    {
        return trackType.ToUpperInvariant() switch
        {
            "MODE1/2048" => 2048,
            "MODE2/2336" => 2336,
            _ => 2352 // MODE1/2352, MODE2/2352, AUDIO, CDG…
        };
    }

    private static string? ExtractQuoted(string line)
    {
        var first = line.IndexOf('"');
        if (first < 0) return null;
        var second = line.IndexOf('"', first + 1);
        return second < 0 ? null : line.Substring(first + 1, second - first - 1);
    }

    private static long ParseMsfFrames(string msf)
    {
        var parts = msf.Split(':');
        if (parts.Length != 3) return 0;
        if (!int.TryParse(parts[0], out var m) ||
            !int.TryParse(parts[1], out var s) ||
            !int.TryParse(parts[2], out var f))
            return 0;

        return (m * 60L + s) * 75 + f;
    }
}
