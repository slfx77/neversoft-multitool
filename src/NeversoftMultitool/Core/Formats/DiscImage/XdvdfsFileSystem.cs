using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Xbox DVD filesystem (XDVDFS). The volume descriptor sits 32 sectors
///     past the partition base; redump-style full XGD dumps place the game
///     partition at a fixed offset past the video partition, so detection
///     probes the known bases. Directory tables are binary trees of
///     dword-offset entries.
/// </summary>
public static class XdvdfsFileSystem
{
    private const int SectorSize = 2048;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    /// <summary>
    ///     Known game-partition base sectors: XISO-only rips, XGD2 redump
    ///     (0xFD90000 — measured on the Proving Ground X360 full dump; the
    ///     video partition occupies the front of the image), XGD1, and two
    ///     historical layer-split candidates kept for older rips. Detection
    ///     is magic-gated, so extra candidates cannot misfire.
    /// </summary>
    private static readonly long[] CandidateBaseSectors =
    [
        0,
        0xFD90000 / SectorSize,
        0x18300000 / SectorSize,
        0x1FB20000 / SectorSize,
        0x2EE80000 / SectorSize
    ];

    public static bool TryFindBase(IDiscSectorSource source, out long baseSector)
    {
        Span<byte> sector = stackalloc byte[SectorSize];
        foreach (var candidate in CandidateBaseSectors)
        {
            if (candidate + 32 >= source.SectorCount)
                continue;

            try
            {
                source.ReadSector(candidate + 32, sector);
            }
            catch
            {
                continue;
            }

            if (sector[..Magic.Length].SequenceEqual(Magic))
            {
                baseSector = candidate;
                return true;
            }
        }

        baseSector = 0;
        return false;
    }

    public static List<DiscFileEntry> ReadFileList(IDiscSectorSource source, long baseSector)
    {
        var sector = new byte[SectorSize];
        source.ReadSector(baseSector + 32, sector);
        if (!sector.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("XDVDFS volume descriptor not found.");

        var rootSector = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(20));
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(24));

        var entries = new List<DiscFileEntry>();
        WalkDirectory(source, baseSector, rootSector, rootSize, "", entries, 0);
        return entries;
    }

    private static void WalkDirectory(
        IDiscSectorSource source,
        long baseSector,
        long tableSector,
        long tableSize,
        string directory,
        List<DiscFileEntry> entries,
        int depth)
    {
        if (depth > 32 || tableSize == 0)
            return;

        var sectors = (tableSize + SectorSize - 1) / SectorSize;
        var data = new byte[sectors * SectorSize];
        for (long i = 0; i < sectors; i++)
            source.ReadSector(baseSector + tableSector + i, data.AsSpan((int)(i * SectorSize), SectorSize));

        var pending = new Stack<int>();
        pending.Push(0);
        var visited = new HashSet<int>();

        while (pending.Count > 0)
        {
            var entryOffset = pending.Pop();
            var byteOffset = entryOffset * 4;
            if (entryOffset == 0xFFFF || !visited.Add(entryOffset))
                continue;
            if ((long)byteOffset + 14 > tableSize || byteOffset + 14 > data.Length)
                continue;

            var left = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(byteOffset));
            var right = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(byteOffset + 2));
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(byteOffset + 4));
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(byteOffset + 8));
            var attributes = data[byteOffset + 12];
            var nameLength = data[byteOffset + 13];

            // 0xFFFF-filled padding marks the end of a sector's entries.
            if (left == 0xFFFF && right == 0xFFFF && startSector == 0xFFFFFFFF)
                continue;
            if ((long)byteOffset + 14 + nameLength > tableSize ||
                byteOffset + 14 + nameLength > data.Length)
            {
                continue;
            }

            if (left != 0 && left != 0xFFFF)
                pending.Push(left);
            if (right != 0 && right != 0xFFFF)
                pending.Push(right);

            var name = Encoding.Latin1.GetString(data.AsSpan(byteOffset + 14, nameLength));
            var isDirectory = (attributes & 0x10) != 0;

            if (!isDirectory && fileSize != 0)
            {
                var fileSectors = ((long)fileSize + SectorSize - 1) / SectorSize;
                if (baseSector < 0 || baseSector > source.SectorCount)
                    continue;

                var availableSectors = source.SectorCount - baseSector;
                if (fileSectors > availableSectors ||
                    (long)startSector > availableSectors - fileSectors)
                {
                    continue;
                }
            }

            var entry = new DiscFileEntry(directory, name, baseSector + startSector, fileSize, isDirectory);

            if (isDirectory)
                WalkDirectory(source, baseSector, startSector, fileSize, entry.FullPath, entries, depth + 1);
            else
                entries.Add(entry);
        }
    }
}
