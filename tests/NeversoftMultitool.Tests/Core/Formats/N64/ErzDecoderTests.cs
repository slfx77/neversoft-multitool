using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

/// <summary>
///     Validates the C# ERZ transcription against golden outputs produced by
///     <c>tools/diagnostics/erz_emu_decode.py</c>, which decodes the SAME
///     blocks by executing the ROM's own MIPS decompressor under emulation —
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

    [Fact]
    public void Decode_RejectsV1UntilItsCoreIsTranscribed()
    {
        var block = new byte[32];
        block[0] = (byte)'E';
        block[1] = (byte)'R';
        block[2] = (byte)'Z';
        block[3] = 1;

        Assert.True(ErzDecoder.IsErz(block));
        Assert.Throws<NotSupportedException>(() => ErzDecoder.Decode(block));
    }
}
