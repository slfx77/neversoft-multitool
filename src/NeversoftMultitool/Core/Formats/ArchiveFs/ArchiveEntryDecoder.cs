using System.IO.Compression;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Shared per-entry decode step for the archive filesystems. The
///     <see cref="ArchiveEntry.IsCompressed" /> flag is OVERLOADED per type —
///     PAK repurposes it as "archive has a .pab companion" and THUG-era WAD as
///     the NO_WAD external-reference flag — so decompression must be scoped to
///     the types that really compress.
/// </summary>
internal static class ArchiveEntryDecoder
{
    /// <summary>
    ///     Bytes to fetch from the container for one entry. CompressedSize is
    ///     honored only for types that truly store compressed payloads.
    /// </summary>
    public static long GetStoredSize(ArchiveAssetType type, ArchiveEntry entry)
    {
        return type switch
        {
            ArchiveAssetType.CompressedPre or ArchiveAssetType.Pkr when entry.IsCompressed => entry.CompressedSize,
            // QTex ZIP stores the raw slice length in CompressedSize for STORE
            // and DEFLATE alike.
            ArchiveAssetType.Zip => entry.CompressedSize,
            _ => entry.Size
        };
    }

    /// <summary>
    ///     Type-scoped decode: CompressedPre → LZSS, PKR → zlib, ZIP → deflate.
    ///     Everything else — including PAK ("has pab") and WAD (NO_WAD) — is a
    ///     straight copy of the stored bytes.
    /// </summary>
    public static byte[] Decode(ArchiveAssetType type, ArchiveEntry entry, byte[] stored)
    {
        switch (type)
        {
            case ArchiveAssetType.CompressedPre when entry.IsCompressed:
                return LzssDecoder.Decode(stored, checked((int)entry.Size));

            case ArchiveAssetType.Pkr when entry.IsCompressed:
            {
                using var input = new MemoryStream(stored, false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                var output = new byte[entry.Size];
                try
                {
                    zlib.ReadExactly(output);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException(
                        $"Decompressed PKR entry is shorter than its declared size of {entry.Size} bytes.", ex);
                }

                if (zlib.ReadByte() != -1)
                    throw new InvalidDataException(
                        $"Decompressed PKR entry exceeds its declared size of {entry.Size} bytes.");
                return output;
            }

            case ArchiveAssetType.Zip when entry.IsCompressed:
            {
                using var input = new MemoryStream(stored, false);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                var output = new byte[entry.Size];
                try
                {
                    deflate.ReadExactly(output);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException(
                        $"Decompressed ZIP entry is shorter than its declared size of {entry.Size} bytes.", ex);
                }

                if (deflate.ReadByte() != -1)
                    throw new InvalidDataException(
                        $"Decompressed ZIP entry exceeds its declared size of {entry.Size} bytes.");
                return output;
            }

            default:
                return stored;
        }
    }
}
