using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

/// <summary>
///     Validates the C# ERZ transcription against golden outputs produced by
///     decoding the same blocks with the ROM's own MIPS decompressor under emulation —
///     bit-exact ground truth by construction (2026-08-05). The four blocks
///     cover the observed stream features: text-heavy data (skater
///     definitions), two MIPS code overlays, and a table-heavy block.
/// </summary>
public sealed class ErzDecoderTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    [Theory]
    [InlineData(0x13BB8, 65_536, "42e9d73b36a74ff1481aee6eea20ab651f065b15983153e0cdc6baea39ad217f")]
    [InlineData(0x1AB84, 65_536, "cd6f62a4486e23cfe89507bbffccc52481b53e14697ffa59aa8c4f366435f68f")]
    [InlineData(0x229C0, 65_536, "16d2a189283ab50667cd75f7acfdcf66c6997885962b0a7325ff8ef77531000f")]
    [InlineData(0x2AC8C, 65_536, "8030e8e200e79f291aa3e083cc893af3724cbb1170c923c19adfbebc1e73d592")]
    public void DecodeV2_MatchesTheEmulatedRomDecompressor(
        int romOffset,
        int expectedLength,
        string expectedSha256)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        var compressedSize = ErzDecoder.GetCompressedSize(rom.AsSpan(romOffset));
        var block = rom[romOffset..(romOffset + ErzDecoder.HeaderSize + compressedSize)];

        Assert.True(ErzDecoder.IsErz(block));
        Assert.Equal(2, ErzDecoder.GetVersion(block));

        var decoded = ErzDecoder.Decode(block);

        Assert.Equal(expectedLength, decoded.Length);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(decoded)));
    }

    /// <summary>
    ///     v1 goldens (2026-08-05): the THPS2 asset-directory block and THPS1's
    ///     first boot block, both emulator-decoded. v1 is the Deflate-like
    ///     canonical-code scheme used by the THPS1/2/3 asset corpora.
    /// </summary>
    [Theory]
    [InlineData(Thps2N64Build, RomName, 0x79AAA, 16_384,
        "bb3c5d8a6509aeaabe64eeb0c9c9e815c5374c505321ac51acff32dc7a94095f")]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 0x13A24, 65_536,
        "8ad3bff88e7be31a2c059aea0a478b86984dc932b41563a411fe5b8792e41e98")]
    public void DecodeV1_MatchesTheEmulatedRomDecompressor(
        string buildName,
        string romName,
        int romOffset,
        int expectedLength,
        string expectedSha256)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        var compressedSize = ErzDecoder.GetCompressedSize(rom.AsSpan(romOffset));
        var block = rom[romOffset..(romOffset + ErzDecoder.HeaderSize + compressedSize)];

        Assert.True(ErzDecoder.IsErz(block));
        Assert.Equal(1, ErzDecoder.GetVersion(block));

        var decoded = ErzDecoder.Decode(block);

        Assert.Equal(expectedLength, decoded.Length);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(decoded)));
    }
}
