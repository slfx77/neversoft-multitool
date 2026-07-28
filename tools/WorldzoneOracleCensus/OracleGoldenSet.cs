using System.Text.Json;
using NeversoftMultitool.Core.Formats.GsDump.Oracle;

namespace WorldzoneOracleCensus;

/// <summary>
///     All committed GS-oracle goldens ({tag}.gsoracle.json), indexed by texture
///     checksum. Facts are OBSERVATIONS per capture: a checksum can be drawn
///     under several state buckets, and absence from every capture proves
///     nothing (the dumps only show what those frames drew).
/// </summary>
internal sealed class OracleGoldenSet
{
    private readonly Dictionary<uint, List<CaptureFacts>> _byChecksum;

    private OracleGoldenSet(Dictionary<uint, List<CaptureFacts>> byChecksum, List<string> captureTags)
    {
        _byChecksum = byChecksum;
        CaptureTags = captureTags;
    }

    public IReadOnlyList<string> CaptureTags { get; }

    public int ObservedChecksumCount => _byChecksum.Count;

    public static OracleGoldenSet Load(string goldenDir)
    {
        var byChecksum = new Dictionary<uint, List<CaptureFacts>>();
        var tags = new List<string>();
        foreach (var file in Directory.GetFiles(goldenDir, "*.gsoracle.json").OrderBy(static f => f,
                     StringComparer.OrdinalIgnoreCase))
        {
            var tag = Path.GetFileName(file);
            tag = tag[..tag.IndexOf('.', StringComparison.Ordinal)];
            var report = JsonSerializer.Deserialize(File.ReadAllText(file),
                GsOracleJsonContext.Default.GsOracleReport);
            if (report == null)
                continue;

            tags.Add(tag);
            foreach (var texture in report.Textures)
            {
                if (!byChecksum.TryGetValue(texture.Checksum, out var list))
                {
                    list = [];
                    byChecksum[texture.Checksum] = list;
                }

                list.Add(new CaptureFacts(tag, texture));
            }
        }

        return new OracleGoldenSet(byChecksum, tags);
    }

    public bool IsObserved(uint checksum)
    {
        return checksum != 0 && _byChecksum.ContainsKey(checksum);
    }

    public IReadOnlyList<CaptureFacts> FactsFor(uint checksum)
    {
        return _byChecksum.TryGetValue(checksum, out var list) ? list : [];
    }

    public (long Draws, long Pixels) Totals(uint checksum)
    {
        long draws = 0, pixels = 0;
        foreach (var capture in FactsFor(checksum))
        {
            draws += capture.Facts.TotalDraws;
            pixels += capture.Facts.TotalPixelsWritten;
        }

        return (draws, pixels);
    }

    /// <summary>
    ///     Aggregate the buckets matching one blend register state (ABE on +
    ///     exact A/B/C/D) across every capture — the evidence that THIS pass of
    ///     the texture, not just the texture, was drawn.
    /// </summary>
    public BlendStateEvidence ScoreBlendState(uint checksum, uint a, uint b, uint c, uint d)
    {
        long stateDraws = 0, statePixels = 0, anyDraws = 0, anyPixels = 0;
        var stateCaptures = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var capture in FactsFor(checksum))
        {
            foreach (var bucket in capture.Facts.StateBuckets)
            {
                anyDraws += bucket.Draws;
                anyPixels += bucket.PixelsWritten;
                if (!bucket.AlphaBlendEnabled ||
                    bucket.AlphaA != a || bucket.AlphaB != b || bucket.AlphaC != c || bucket.AlphaD != d)
                {
                    continue;
                }

                stateDraws += bucket.Draws;
                statePixels += bucket.PixelsWritten;
                if (bucket.PixelsWritten > 0)
                    stateCaptures.Add(capture.Tag);
            }
        }

        return new BlendStateEvidence(stateDraws, statePixels, anyDraws, anyPixels, [.. stateCaptures]);
    }

    /// <summary>
    ///     Frame-global first draw index of a checksum in one capture, preferring
    ///     buckets that match the leaf's A/B/C/D blend state and falling back to
    ///     all buckets when none match. Null when the capture never drew it.
    /// </summary>
    public long? FirstDrawIndex(string tag, uint checksum, uint a, uint b, uint c, uint d)
    {
        long? matched = null;
        long? any = null;
        foreach (var capture in FactsFor(checksum))
        {
            if (!string.Equals(capture.Tag, tag, StringComparison.Ordinal))
                continue;

            foreach (var bucket in capture.Facts.StateBuckets)
            {
                any = any is { } a0 ? Math.Min(a0, bucket.FirstDrawIndex) : bucket.FirstDrawIndex;
                if (bucket.AlphaA != a || bucket.AlphaB != b || bucket.AlphaC != c || bucket.AlphaD != d)
                    continue;

                matched = matched is { } m0 ? Math.Min(m0, bucket.FirstDrawIndex) : bucket.FirstDrawIndex;
            }
        }

        return matched ?? any;
    }

    internal readonly record struct CaptureFacts(string Tag, GsOracleTextureFacts Facts);

    internal readonly record struct BlendStateEvidence(
        long StateDraws,
        long StatePixels,
        long AnyDraws,
        long AnyPixels,
        IReadOnlyList<string> StateCaptures);
}
