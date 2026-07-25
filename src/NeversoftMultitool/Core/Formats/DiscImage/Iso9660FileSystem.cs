using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     ISO9660 reader (PS1/PS2/Dreamcast/PC discs). Prefers the Joliet
///     supplementary descriptor when present (PC discs with long names);
///     otherwise uses the primary descriptor with ";1" version suffixes
///     stripped. GD-ROM images pass their high-density session LBA as
///     <c>sessionLba</c> — descriptor and extent LBAs there are absolute,
///     which the track-mapped sector source resolves.
/// </summary>
public static class Iso9660FileSystem
{
    private const int SectorSize = 2048;
    private const int MaxDepth = 32;

    public static bool HasVolumeDescriptor(IDiscSectorSource source, long sessionLba = 0)
    {
        try
        {
            Span<byte> sector = stackalloc byte[SectorSize];
            source.ReadSector(sessionLba + 16, sector);
            return sector[0] == 1 && sector.Slice(1, 5).SequenceEqual("CD001"u8);
        }
        catch
        {
            return false;
        }
    }

    public static List<DiscFileEntry> ReadFileList(IDiscSectorSource source, long sessionLba = 0)
    {
        var sector = new byte[SectorSize];
        byte[]? primaryRoot = null;
        byte[]? jolietRoot = null;

        for (var lba = sessionLba + 16; lba < sessionLba + 32; lba++)
        {
            source.ReadSector(lba, sector);
            if (!sector.AsSpan(1, 5).SequenceEqual("CD001"u8))
                break;

            switch (sector[0])
            {
                case 1:
                    primaryRoot ??= sector.AsSpan(156, 34).ToArray();
                    break;

                case 2 when IsJoliet(sector):
                    jolietRoot ??= sector.AsSpan(156, 34).ToArray();
                    break;
            }

            if (sector[0] == 255)
                break;
        }

        var root = jolietRoot ?? primaryRoot
            ?? throw new InvalidDataException("No ISO9660 volume descriptor found.");
        var joliet = jolietRoot != null;

        var entries = new List<DiscFileEntry>();
        var rootLba = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(2));
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(10));
        WalkDirectory(source, rootLba, rootSize, "", joliet, entries, 0);
        return entries;
    }

    private static bool IsJoliet(ReadOnlySpan<byte> descriptor)
    {
        // Escape sequences %/@ %/C %/E at offset 88 mark UCS-2 levels 1-3.
        var esc = descriptor.Slice(88, 3);
        return esc[0] == 0x25 && esc[1] == 0x2F && esc[2] is 0x40 or 0x43 or 0x45;
    }

    private static void WalkDirectory(
        IDiscSectorSource source,
        long extentLba,
        long extentSize,
        string directory,
        bool joliet,
        List<DiscFileEntry> entries,
        int depth)
    {
        if (depth > MaxDepth)
            return;

        var sectors = (extentSize + SectorSize - 1) / SectorSize;
        var data = new byte[sectors * SectorSize];
        for (long i = 0; i < sectors; i++)
            source.ReadSector(extentLba + i, data.AsSpan((int)(i * SectorSize), SectorSize));

        var offset = 0;
        while (offset < extentSize)
        {
            var recordLength = data[offset];
            if (recordLength == 0)
            {
                // Records never span sectors — skip to the next boundary.
                offset = (offset / SectorSize + 1) * SectorSize;
                continue;
            }

            if (offset + recordLength > data.Length)
                break;

            var record = data.AsSpan(offset, recordLength);
            offset += recordLength;

            var nameLength = record[32];
            if (nameLength == 0 || 33 + nameLength > recordLength)
                continue;

            // "\0" = self, "\1" = parent.
            if (nameLength == 1 && record[33] <= 1)
                continue;

            var name = DecodeName(record.Slice(33, nameLength), joliet);
            if (name.Length == 0)
                continue;

            var childLba = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
            var childSize = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
            var isDirectory = (record[25] & 0x02) != 0;

            var entry = new DiscFileEntry(directory, name, childLba, childSize, isDirectory);
            if (isDirectory)
            {
                WalkDirectory(source, childLba, childSize, entry.FullPath, joliet, entries, depth + 1);
            }
            else
            {
                entries.Add(entry);
            }
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> raw, bool joliet)
    {
        var name = joliet
            ? Encoding.BigEndianUnicode.GetString(raw)
            : Encoding.ASCII.GetString(raw);

        // Strip the ISO version suffix (";1") and a trailing dot from
        // extensionless primary names ("README." → "README").
        var semi = name.IndexOf(';');
        if (semi >= 0)
            name = name[..semi];
        return name.TrimEnd('.');
    }
}
