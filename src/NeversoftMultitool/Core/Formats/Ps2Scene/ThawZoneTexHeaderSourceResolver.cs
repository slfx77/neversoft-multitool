using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexHeaderSourceResolver
{
    public static Dictionary<(uint Tbp, uint Cbp), uint> BuildChecksumMapFromHeaders(
        IEnumerable<ReadOnlyMemory<byte>> fileData)
    {
        var map = new Dictionary<(uint Tbp, uint Cbp), uint>();
        foreach (var data in fileData)
        {
            foreach (var entry in ParseHeaderEntries(data.Span))
            {
                var tbp = (uint)(entry.Tex0 & 0x3FFF);
                var cbp = (uint)((entry.Tex0 >> 37) & 0x3FFF);
                map.TryAdd((tbp, cbp), entry.Checksum);
            }
        }

        return map;
    }

    public static Dictionary<ulong, ZoneTexHeaderSourceEntry> BuildHeaderSourceEntryMapByTex0FromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        var map = new Dictionary<ulong, ZoneTexHeaderSourceEntry>();
        var sourceIndex = 0;

        foreach (var headers in headerLists)
        {
            foreach (var entry in headers)
                map.TryAdd(entry.Tex0, new ZoneTexHeaderSourceEntry(entry, sourceIndex));

            sourceIndex++;
        }

        return map;
    }

    public static Dictionary<(uint Tbp, uint Cbp), List<ZoneTexHeaderSourceEntry>>
        BuildHeaderSourceEntryGroupsFromHeaderLists(
            IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        var map = new Dictionary<(uint Tbp, uint Cbp), List<ZoneTexHeaderSourceEntry>>();
        var sourceIndex = 0;

        foreach (var headers in headerLists)
        {
            foreach (var entry in headers)
            {
                var key = ((uint)(entry.Tex0 & 0x3FFF), (uint)((entry.Tex0 >> 37) & 0x3FFF));
                if (!map.TryGetValue(key, out var entries))
                {
                    entries = [];
                    map[key] = entries;
                }

                entries.Add(new ZoneTexHeaderSourceEntry(entry, sourceIndex));
            }

            sourceIndex++;
        }

        return map;
    }

    public static bool TryResolveHeaderSourceEntry(
        ulong tex0,
        ulong tex1,
        IReadOnlyDictionary<ulong, ZoneTexHeaderSourceEntry> exactMap,
        IReadOnlyDictionary<(uint Tbp, uint Cbp), List<ZoneTexHeaderSourceEntry>> candidateGroups,
        out ZoneTexHeaderSourceEntry resolved)
    {
        if (exactMap.TryGetValue(tex0, out resolved))
            return true;

        var key = ((uint)(tex0 & 0x3FFF), (uint)((tex0 >> 37) & 0x3FFF));
        if (!candidateGroups.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            resolved = default;
            return false;
        }

        resolved = candidates
            .OrderByDescending(candidate => ScoreHeaderCandidate(tex0, tex1, candidate.Entry))
            .ThenBy(candidate => candidate.SourceIndex)
            .ThenBy(candidate => candidate.Entry.UploadOffset)
            .ThenBy(candidate => candidate.Entry.DataOffset)
            .First();
        return true;
    }

    public static Dictionary<(uint Tbp, uint Cbp), int> BuildSourceIndexMapFromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        var map = new Dictionary<(uint Tbp, uint Cbp), int>();
        var sourceIndex = 0;

        foreach (var headers in headerLists)
        {
            foreach (var tex0 in headers.Select(static entry => entry.Tex0))
            {
                var tbp = (uint)(tex0 & 0x3FFF);
                var cbp = (uint)((tex0 >> 37) & 0x3FFF);
                map.TryAdd((tbp, cbp), sourceIndex);
            }

            sourceIndex++;
        }

        return map;
    }

    public static Dictionary<(uint Tbp, uint Cbp), ZoneTexHeaderEntry> BuildHeaderEntryMapFromHeaderLists(
        IEnumerable<IReadOnlyList<ZoneTexHeaderEntry>> headerLists)
    {
        var map = new Dictionary<(uint Tbp, uint Cbp), ZoneTexHeaderEntry>();

        foreach (var headers in headerLists)
        {
            foreach (var entry in headers)
            {
                var tbp = (uint)(entry.Tex0 & 0x3FFF);
                var cbp = (uint)((entry.Tex0 >> 37) & 0x3FFF);
                map.TryAdd((tbp, cbp), entry);
            }
        }

        return map;
    }

    internal static int ScoreHeaderCandidate(ulong tex0, ulong tex1, ZoneTexHeaderEntry entry)
    {
        var score = 0;

        if (entry.Tex0 == tex0)
            score += 10_000;

        if (GetTex0BufferWidth(entry.Tex0) == GetTex0BufferWidth(tex0))
            score += 1_000;
        if (GetTex0Psm(entry.Tex0) == GetTex0Psm(tex0))
            score += 1_000;
        if (GetTex0Width(entry.Tex0) == GetTex0Width(tex0))
            score += 2_000;
        if (GetTex0Height(entry.Tex0) == GetTex0Height(tex0))
            score += 2_000;
        if (GetTex0Cpsm(entry.Tex0) == GetTex0Cpsm(tex0))
            score += 500;

        var requestedMipCount = GetTex1Mxl(tex1);
        if (requestedMipCount > 0)
        {
            if (entry.MipLevelCount == requestedMipCount)
            {
                score += 200;
            }
            else
            {
                score -= Math.Abs((int)entry.MipLevelCount - requestedMipCount) * 10;
            }
        }
        else if (entry.MipLevelCount == 0)
        {
            score += 100;
        }

        return score;
    }

    internal static uint GetTex0BufferWidth(ulong tex0)
    {
        return (uint)((tex0 >> 14) & 0x3F);
    }

    internal static uint GetTex0Psm(ulong tex0)
    {
        return (uint)((tex0 >> 20) & 0x3F);
    }

    internal static int GetTex0Width(ulong tex0)
    {
        return 1 << (int)((tex0 >> 26) & 0xF);
    }

    internal static int GetTex0Height(ulong tex0)
    {
        return 1 << (int)((tex0 >> 30) & 0xF);
    }

    internal static uint GetTex0Cbp(ulong tex0)
    {
        return (uint)((tex0 >> 37) & 0x3FFF);
    }

    internal static uint GetTex0Cpsm(ulong tex0)
    {
        return (uint)((tex0 >> 51) & 0xF);
    }

    internal static int GetTex1Mxl(ulong tex1)
    {
        return (int)((tex1 >> 2) & 0x7);
    }

}
