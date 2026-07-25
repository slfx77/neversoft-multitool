namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Uniform 2048-byte-logical-sector view over a disc image, regardless of
///     the container's physical sector size (2048 plain ISO, 2352 raw CD with
///     sync/header, 2336 Mode2-without-sync). Raw-capable sources additionally
///     expose the Mode2 sector tail (subheader + data) so Form2 XA/STR streams
///     can be extracted losslessly.
/// </summary>
public interface IDiscSectorSource : IDisposable
{
    /// <summary>Total addressable logical sectors (highest mapped LBA + 1).</summary>
    long SectorCount { get; }

    /// <summary>True when 2352-byte raw sectors (with subheaders) are available.</summary>
    bool HasRawSectors { get; }

    /// <summary>
    ///     Reads the 2048-byte user data of the given LBA. Returns the Mode2
    ///     XA submode byte (bit 5 = Form2), or 0 for Mode1/plain sectors.
    /// </summary>
    byte ReadSector(long lba, Span<byte> buffer);

    /// <summary>
    ///     Reads the sector's Mode2 tail — subheader + data + EDC, i.e. raw
    ///     bytes 16..2352 (2336 bytes) — and returns the submode byte
    ///     (bit 5 set = Form2). Only valid when <see cref="HasRawSectors" />.
    /// </summary>
    byte ReadSectorTail(long lba, Span<byte> buffer);
}
