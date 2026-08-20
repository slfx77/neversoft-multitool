using System.Buffers.Binary;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     Pins the Wii encrypted-partition reader. The synthetic tests build a
///     one-cluster DATA partition encrypted under a TEST key (no real console
///     secret, no disc image), proving the partition-table walk, title-key
///     decryption, and cluster AES round-trip. The corpus test validates the
///     whole path against the two real Wii discs and skips when the common key
///     is not provisioned.
/// </summary>
public sealed class WiiDiscTests(TestPaths paths)
{
    private const string DhjBuild = "Tony Hawk's Downhill Jam (2006-11-19, Wii - Final)";

    [Fact]
    public void WiiPartitionStream_DecryptsClusterRoundTrip_UnderTestKey()
    {
        var commonKey = RandomBytes(16, seed: 1);
        var titleKey = RandomBytes(16, seed: 2);
        var titleId = RandomBytes(8, seed: 3);

        // A single cluster of plaintext: 0x7C00 recognisable bytes.
        var plaintext = new byte[0x7C00];
        for (var i = 0; i < plaintext.Length; i++)
            plaintext[i] = (byte)(i * 7 + 3);

        var disc = BuildSyntheticWiiDisc(commonKey, titleKey, titleId, plaintext, out var path);
        try
        {
            Environment.SetEnvironmentVariable(WiiCommonKey.EnvironmentVariable, Convert.ToHexStringLower(commonKey));

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.True(WiiDisc.IsWii(stream));

            var partition = WiiDisc.TryOpenDataPartition(stream, out var error);
            Assert.Null(error);
            Assert.NotNull(partition);

            var read = new byte[plaintext.Length];
            partition!.Position = 0;
            partition.ReadExactly(read);
            Assert.Equal(plaintext, read);

            // A random-access seek into the middle returns the right bytes.
            partition.Position = 0x1234;
            var slice = new byte[16];
            partition.ReadExactly(slice);
            Assert.Equal(plaintext.AsSpan(0x1234, 16).ToArray(), slice);
            partition.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WiiCommonKey.EnvironmentVariable, null);
            GC.KeepAlive(disc);
            File.Delete(path);
        }
    }

    [Fact]
    public void TryOpenDataPartition_MissingKey_DeclinesWithHint()
    {
        var disc = BuildSyntheticWiiDisc(RandomBytes(16, 1), RandomBytes(16, 2), RandomBytes(8, 3),
            new byte[0x7C00], out var path);
        try
        {
            Environment.SetEnvironmentVariable(WiiCommonKey.EnvironmentVariable, null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.True(WiiDisc.IsWii(stream));
            var partition = WiiDisc.TryOpenDataPartition(stream, out var error);
            Assert.Null(partition);
            Assert.Equal(WiiCommonKey.ProvisioningHint, error);
        }
        finally
        {
            GC.KeepAlive(disc);
            File.Delete(path);
        }
    }

    [CorpusFact]
    public void RealDisc_ExtractedByTheWiiReader_HasByteExactContent()
    {
        // BuildMode.Iso extracts the disc into the corpus (no ISO is kept), so
        // this validates the reader END TO END through the staged output: the
        // generator's Wii branch runs WiiDisc + WiiPartitionStream, and a broken
        // partition walk or AES step would corrupt these files. Needs no common
        // key at test time — the files are already decrypted on disk.
        var font = paths.FindSampleFile(DhjBuild, "small.fnt.ngc");
        Assert.SkipWhen(font == null, "Downhill Jam Wii build not staged");

        // Byte-exact against DolphinTool's own extraction of the same file.
        Assert.Equal(
            "4fadb2daa2217c7822620a923e78946f22cf7ff28a485a0c35cdbaef55f820c6",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(font!))));

        Assert.NotNull(paths.FindSampleFile(DhjBuild, "standardkeyQ.bin"));
        // The DATA partition is dominated by the Neversoft big-endian .ngc lineage.
        Assert.True(paths.FindSampleFiles(DhjBuild, "*.ngc").Count() > 2000,
            "expected the extracted Wii build to be dominated by .ngc assets");
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
            bytes[i] = (byte)(seed * 131 + i * 17);
        return bytes;
    }

    /// <summary>
    ///     Minimal Wii disc: header magic, a one-partition table at 0x40000, a
    ///     ticket carrying the common-key-encrypted title key, a TMD-info block,
    ///     and one AES-CBC cluster (0x400 hash + 0x7C00 encrypted data). Enough
    ///     for the reader to find and decrypt the DATA partition.
    /// </summary>
    private static byte[] BuildSyntheticWiiDisc(
        byte[] commonKey, byte[] titleKey, byte[] titleId, byte[] plaintext, out string path)
    {
        const long partitionOffset = 0x50000;
        const int dataOffsetInPartition = 0x8000; // one cluster past the header
        var image = new byte[partitionOffset + dataOffsetInPartition + 0x8000];

        // Disc header: Wii magic at 0x18.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18), 0x5D1C9EA3);

        // Partition table @0x40000: group 0 = {count 1, infoOffset>>2}.
        const long infoOffset = 0x40020;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x40000), 1);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x40004), (uint)(infoOffset >> 2));
        // Partition info: {partitionOffset>>2, type 0 = DATA}.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)infoOffset), (uint)(partitionOffset >> 2));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan((int)infoOffset + 4), 0);

        // Ticket: title id @0x1DC, common-key-encrypted title key @0x1BF.
        Array.Copy(titleId, 0, image, (int)partitionOffset + 0x1DC, 8);
        var iv = new byte[16];
        Array.Copy(titleId, iv, 8);
        using (var aes = Aes.Create())
        {
            aes.Key = commonKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor(commonKey, iv);
            var encKey = new byte[16];
            enc.TransformBlock(titleKey, 0, 16, encKey, 0);
            Array.Copy(encKey, 0, image, (int)partitionOffset + 0x1BF, 16);
        }

        // TMD-info block @ ticket+0x2A4: dataOffset>>2 @+0x14, dataSize>>2 @+0x18.
        var tmd = (int)partitionOffset + 0x2A4;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(tmd + 0x14), (uint)(dataOffsetInPartition >> 2));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(tmd + 0x18), 0x8000 >> 2);

        // One cluster at partitionOffset + dataOffset: 0x400 hash + 0x7C00 data.
        var clusterStart = (int)partitionOffset + dataOffsetInPartition;
        var clusterIv = RandomBytes(16, seed: 9);
        Array.Copy(clusterIv, 0, image, clusterStart + 0x3D0, 16); // IV lives in the hash block
        using (var aes = Aes.Create())
        {
            aes.Key = titleKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor(titleKey, clusterIv);
            var encData = new byte[0x7C00];
            enc.TransformBlock(plaintext, 0, 0x7C00, encData, 0);
            Array.Copy(encData, 0, image, clusterStart + 0x400, 0x7C00);
        }

        path = Path.Combine(Path.GetTempPath(), $"nmt-wii-{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, image);
        return image;
    }
}
