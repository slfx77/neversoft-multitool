using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Reads a retail Wii disc image (unscrubbed <c>.iso</c>). The disc header is
///     plaintext (Wii magic <c>0x5D1C9EA3</c> at 0x18); the partition table lives
///     at <c>0x40000</c> as four group entries <c>{u32 count, u32 tableOffset&gt;&gt;2}</c>,
///     each pointing at partition-info records <c>{u32 partitionOffset&gt;&gt;2, u32 type}</c>
///     (type 0 = DATA, the game partition). Every 20.12-style Wii offset in these
///     structures is stored as <c>value &gt;&gt; 2</c>.
///
///     A partition begins with a 0x2A4-byte ticket; its title key is the 16
///     encrypted bytes at <c>ticket+0x1BF</c>, decrypted AES-128-CBC with the
///     <see cref="WiiCommonKey">common key</see> and an IV of the 8-byte title id
///     (<c>ticket+0x1DC</c>) padded to 16. The TMD-info block at
///     <c>ticket+0x2A4</c> gives <c>{tmdSize, tmdOff, certSize, certOff, h3Off,
///     dataOff, dataSize}</c> (offsets word-shifted, relative to the partition
///     start), and the encrypted cluster region is
///     <c>[partitionOffset + dataOff, dataSize)</c>. <see cref="WiiPartitionStream" />
///     turns that region into a plaintext GameCube-shaped image.
/// </summary>
public static class WiiDisc
{
    private const uint WiiMagic = 0x5D1C9EA3;
    private const long PartitionTableOffset = 0x40000;
    private const int TicketTitleKeyOffset = 0x1BF;
    private const int TicketTitleIdOffset = 0x1DC;
    private const int TmdInfoOffset = 0x2A4;

    public static bool IsWii(Stream stream)
    {
        if (stream.Length < 0x50000)
            return false;
        Span<byte> magic = stackalloc byte[4];
        stream.Position = 0x18;
        stream.ReadExactly(magic);
        return BinaryPrimitives.ReadUInt32BigEndian(magic) == WiiMagic;
    }

    /// <summary>
    ///     Opens the DATA partition as a decrypted stream, or returns null with a
    ///     reason: not a Wii disc, no DATA partition, or the common key is not
    ///     provisioned (<see cref="WiiCommonKey.ProvisioningHint" />).
    /// </summary>
    public static WiiPartitionStream? TryOpenDataPartition(Stream disc, out string? error)
    {
        error = null;
        if (!IsWii(disc))
        {
            error = "Not a Wii disc image.";
            return null;
        }

        var commonKey = WiiCommonKey.TryResolve();
        if (commonKey == null)
        {
            error = WiiCommonKey.ProvisioningHint;
            return null;
        }

        var partitionOffset = FindDataPartition(disc);
        if (partitionOffset < 0)
        {
            error = "No DATA partition found on the Wii disc.";
            return null;
        }

        // Ticket → title key.
        var ticket = new byte[TmdInfoOffset + 0x1C];
        disc.Position = partitionOffset;
        disc.ReadExactly(ticket, 0, ticket.Length);

        var titleKey = DecryptTitleKey(ticket, commonKey);

        // TMD-info block: word-shifted offsets/sizes relative to the partition.
        var dataOffsetWords = BinaryPrimitives.ReadUInt32BigEndian(ticket.AsSpan(TmdInfoOffset + 0x14));
        var dataSizeWords = BinaryPrimitives.ReadUInt32BigEndian(ticket.AsSpan(TmdInfoOffset + 0x18));
        var dataOffset = partitionOffset + ((long)dataOffsetWords << 2);
        var dataSize = (long)dataSizeWords << 2;

        if (dataOffset <= 0 || dataSize <= 0 || dataOffset + dataSize > disc.Length)
        {
            error = "Wii DATA partition data region is out of bounds.";
            return null;
        }

        // The returned stream owns the disc handle: disposing it closes the file.
        return new WiiPartitionStream(disc, dataOffset, dataSize, titleKey, ownsDisc: true);
    }

    private static long FindDataPartition(Stream disc)
    {
        Span<byte> table = stackalloc byte[8 * 4];
        disc.Position = PartitionTableOffset;
        disc.ReadExactly(table);

        for (var group = 0; group < 4; group++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(table[(group * 8)..]);
            var infoOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(table[(group * 8 + 4)..]) << 2;
            if (count == 0 || infoOffset <= 0 || count > 16)
                continue;

            var info = new byte[count * 8];
            disc.Position = infoOffset;
            disc.ReadExactly(info, 0, info.Length);
            for (var i = 0; i < count; i++)
            {
                var partitionOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(i * 8)) << 2;
                var type = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(i * 8 + 4));
                if (type == 0 && partitionOffset > 0 && partitionOffset < disc.Length)
                    return partitionOffset;
            }
        }

        return -1;
    }

    private static byte[] DecryptTitleKey(byte[] ticket, byte[] commonKey)
    {
        var encrypted = new byte[16];
        Array.Copy(ticket, TicketTitleKeyOffset, encrypted, 0, 16);

        // IV = 8-byte title id followed by 8 zero bytes.
        var iv = new byte[16];
        Array.Copy(ticket, TicketTitleIdOffset, iv, 0, 8);

        using var aes = Aes.Create();
        aes.Key = commonKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(commonKey, iv);
        var titleKey = new byte[16];
        decryptor.TransformBlock(encrypted, 0, 16, titleKey, 0);
        return titleKey;
    }
}
