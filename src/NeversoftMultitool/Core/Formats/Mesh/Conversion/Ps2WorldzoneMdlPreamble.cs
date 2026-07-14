using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Level-MDL preamble records: signature scan and preamble extension for
///     worldzone leaf VIF streams.
/// </summary>
internal static class Ps2WorldzoneMdlPreamble
{
    // THAW level-MDL preamble repair. Some entries end in the middle of a
    // 0x50-byte preamble record (z_sm's COL entry starts with the continuation),
    // and those records point back at valid VIF chunks before the root node. The
    // game reads the contiguous PAK bytes; our extracted entry slice must do the
    // same. Lifted out of the now-deleted Ps2WorldzoneConverter as a private
    // adapter helper because PopulatePs2Worldzone is the only caller.
    private const uint MdlPreambleRecordSignature = 0x4B189680;
    private const int MdlPreambleRecordSize = 0x50;
    private const int MdlPreambleRecordSignatureOffset = 0x18;
    private const int MinLevelMdlPreambleRecords = 100;
    private const int MaxLevelMdlPreambleExtensionBytes = 0x4000;

    internal static byte[] ExtendLevelMdlPreambleIfNeeded(
        byte[] pakBytes,
        ArchiveEntry mdlEntry,
        byte[] mdlData)
    {
        var preambleStart = FindMdlPreambleStart(mdlData);
        if (preambleStart < 0)
            return mdlData;

        var fullRecordEnd = preambleStart;
        var existingRecords = 0;
        while (fullRecordEnd + MdlPreambleRecordSize <= mdlData.Length
               && ReadUInt32(mdlData, fullRecordEnd + MdlPreambleRecordSignatureOffset) ==
               MdlPreambleRecordSignature)
        {
            existingRecords++;
            fullRecordEnd += MdlPreambleRecordSize;
        }

        if (existingRecords < MinLevelMdlPreambleRecords)
            return mdlData;

        var trailingBytes = mdlData.Length - fullRecordEnd;
        if (trailingBytes <= 0 || trailingBytes >= MdlPreambleRecordSize)
            return mdlData;

        var logicalBase = checked((int)mdlEntry.Offset);
        var maxLogicalLength = Math.Min(
            pakBytes.Length - logicalBase,
            mdlData.Length + MaxLevelMdlPreambleExtensionBytes);

        var extendedEnd = fullRecordEnd;
        var addedRecords = 0;
        while (extendedEnd + MdlPreambleRecordSize <= maxLogicalLength
               && ReadUInt32(pakBytes, logicalBase + extendedEnd + MdlPreambleRecordSignatureOffset) ==
               MdlPreambleRecordSignature)
        {
            addedRecords++;
            extendedEnd += MdlPreambleRecordSize;
        }

        if (addedRecords == 0 || extendedEnd <= mdlData.Length)
            return mdlData;

        var addedLeafRefsIntoMdl = 0;
        for (var recordOffset = fullRecordEnd;
             recordOffset + MdlPreambleRecordSize <= extendedEnd;
             recordOffset += MdlPreambleRecordSize)
        {
            var flags = ReadUInt32(pakBytes, logicalBase + recordOffset + 0x3C);
            if ((flags & 0x2u) == 0)
                continue;

            var field40 = ReadUInt32(pakBytes, logicalBase + recordOffset + 0x40);
            if (field40 < mdlData.Length)
                addedLeafRefsIntoMdl++;
        }

        if (addedLeafRefsIntoMdl == 0)
            return mdlData;

        var extended = new byte[extendedEnd];
        Array.Copy(pakBytes, logicalBase, extended, 0, extended.Length);
        return extended;
    }

    private static int FindMdlPreambleStart(byte[] data)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            if (ReadUInt32(data, i) != MdlPreambleRecordSignature)
                continue;

            return i >= MdlPreambleRecordSignatureOffset
                ? i - MdlPreambleRecordSignatureOffset
                : -1;
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }
}
