using System.Buffers.Binary;
using System.Text;

namespace SampleGenerator;

/// <summary>
///     In-tree extractor for original Xbox ISO ("XISO" / XGD1) images.
///     Replaces the external extract-xiso binary with a managed reader that doesn't OOM
///     on big ISOs and removes the configurable absolute path. Format reference:
///     https://github.com/XboxDev/extract-xiso/blob/master/extract-xiso.c
/// </summary>
internal static class SampleGeneratorXboxIsoOperations
{
    private const long XisoHeaderOffset = 0x10000;
    private const int XisoSectorSize = 2048;
    private const int XisoEntryHeaderSize = 14;
    private const ushort XisoSubtreeSentinel = 0xFFFF;
    private const byte XisoAttributeDir = 0x10;
    private const byte XisoPaddingByte = 0xFF;

    private static readonly byte[] XisoMagic = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    /// <summary>
    ///     Returns true if the file's magic at offset 0x10000 matches the standard XISO marker.
    /// </summary>
    internal static bool IsXboxIso(string isoPath)
    {
        try
        {
            using var fs = File.OpenRead(isoPath);
            if (fs.Length < XisoHeaderOffset + XisoMagic.Length) return false;
            fs.Position = XisoHeaderOffset;
            Span<byte> buf = stackalloc byte[20];
            fs.ReadExactly(buf);
            return buf.SequenceEqual(XisoMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Extracts every file in an XISO into destDir, mirroring the on-disc directory tree.
    ///     Returns the number of files written.
    /// </summary>
    internal static int ExtractFiles(string isoPath, string destDir)
    {
        destDir = SampleGeneratorPathSafety.ResolveConfiguredOutputDirectory(destDir);
        Directory.CreateDirectory(destDir);

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        // Read root directory metadata from the XISO header.
        fs.Position = XisoHeaderOffset + XisoMagic.Length;
        Span<byte> rootInfo = stackalloc byte[8];
        fs.ReadExactly(rootInfo);
        var rootSector = BinaryPrimitives.ReadUInt32LittleEndian(rootInfo[..4]);
        var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(rootInfo[4..8]);

        var dirQueue = new Queue<(long DirAbsOffset, uint DirSize, string DirRelPath)>();
        dirQueue.Enqueue(((long)rootSector * XisoSectorSize, rootSize, string.Empty));

        var fileCount = 0;
        var entryHeader = new byte[XisoEntryHeaderSize];
        var nameBuf = new byte[255];
        var copyBuf = new byte[65536];

        while (dirQueue.Count > 0)
        {
            var (dirAbs, dirSize, dirRel) = dirQueue.Dequeue();
            var destDirPath = string.IsNullOrEmpty(dirRel)
                ? destDir
                : SampleGeneratorPathSafety.ResolveDestinationPath(destDir, dirRel);
            Directory.CreateDirectory(destDirPath);

            // Walk the AVL tree of directory entries via a pre-order stack.
            // Order doesn't matter for extraction — we just need every node visited once.
            var nodeStack = new Stack<long>();
            nodeStack.Push(0);
            var visited = new HashSet<long>();

            while (nodeStack.Count > 0)
            {
                var entryOff = nodeStack.Pop();
                if (entryOff < 0 || entryOff + XisoEntryHeaderSize > dirSize) continue;
                if (!visited.Add(entryOff)) continue;

                fs.Position = dirAbs + entryOff;
                fs.ReadExactly(entryHeader);

                var leftOff = BinaryPrimitives.ReadUInt16LittleEndian(entryHeader.AsSpan(0, 2));
                var rightOff = BinaryPrimitives.ReadUInt16LittleEndian(entryHeader.AsSpan(2, 2));
                var startSector = BinaryPrimitives.ReadUInt32LittleEndian(entryHeader.AsSpan(4, 4));
                var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(entryHeader.AsSpan(8, 4));
                var attributes = entryHeader[12];
                var nameLen = entryHeader[13];

                // Padding and end-of-tree markers: nameLen==0xFF means uninitialized space.
                if (nameLen == XisoPaddingByte || nameLen == 0) continue;

                if (leftOff != XisoSubtreeSentinel)
                    nodeStack.Push((long)leftOff * 4);
                if (rightOff != XisoSubtreeSentinel)
                    nodeStack.Push((long)rightOff * 4);

                if (entryOff + XisoEntryHeaderSize + nameLen > dirSize) continue;
                fs.ReadExactly(nameBuf, 0, nameLen);
                var name = SampleGeneratorPathSafety.SanitizeDiscPathSegment(
                    Encoding.Latin1.GetString(nameBuf, 0, nameLen));
                if (name.Length == 0) continue;

                var entryRel = string.IsNullOrEmpty(dirRel) ? name : Path.Combine(dirRel, name);

                if ((attributes & XisoAttributeDir) != 0)
                {
                    if (entrySize > 0)
                    {
                        var subDirAbs = (long)startSector * XisoSectorSize;
                        if (subDirAbs >= 0 && subDirAbs + entrySize <= fs.Length)
                            dirQueue.Enqueue((subDirAbs, entrySize, entryRel));
                    }
                    Directory.CreateDirectory(
                        SampleGeneratorPathSafety.ResolveDestinationPath(destDir, entryRel));
                }
                else
                {
                    var fileAbs = (long)startSector * XisoSectorSize;
                    if (entrySize > 0 && (fileAbs < 0 || fileAbs + entrySize > fs.Length))
                        continue;

                    var destFile = SampleGeneratorPathSafety.ResolveDestinationPath(destDir, entryRel);
                    var destFileDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destFileDir))
                        Directory.CreateDirectory(destFileDir);

                    WriteFileFromIso(fs, fileAbs, entrySize, destFile, copyBuf);
                    fileCount++;
                }
            }
        }

        return fileCount;
    }

    private static void WriteFileFromIso(FileStream fs, long absOffset, uint length, string destFile, byte[] buffer)
    {
        using var output = File.Create(destFile);
        if (length == 0) return;

        fs.Position = absOffset;
        var remaining = (long)length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = fs.Read(buffer, 0, toRead);
            if (read <= 0)
                throw new EndOfStreamException(
                    $"Unexpected EOF while extracting {Path.GetFileName(destFile)} from XISO.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

}
