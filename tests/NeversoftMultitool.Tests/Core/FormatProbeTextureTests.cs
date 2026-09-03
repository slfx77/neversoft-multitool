using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Texture.Psp;
using NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeTextureTests
{
    [Fact]
    public void ProbeTexture_PvrMixedCaseExtensionWithExactRectangle_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".PvR", BuildRectanglePvr());
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_MissingPvrFile_ReportsHeaderReadFailure()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pvr");

        var result = FormatProbe.ProbeTexture(missingPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal("Unknown", result.FormatName);
        Assert.Equal("Failed to read file header", result.UnsupportedReason);
    }

    [Fact]
    public void ProbeTexture_PvrBadMagicTruncationAndInvalidSizes_Unsupported()
    {
        var invalidMagic = new byte[16];
        "NOPE"u8.CopyTo(invalidMagic);
        var oversizedGbix = new byte[8];
        "GBIX"u8.CopyTo(oversizedGbix);
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedGbix.AsSpan(4), uint.MaxValue);
        byte[][] malformed =
        [
            invalidMagic,
            "PVRT"u8.ToArray(),
            BuildPvrtHeader(7),
            BuildPvrtHeader(16),
            BuildPvrtHeader(uint.MaxValue),
            oversizedGbix
        ];

        foreach (var data in malformed)
        {
            var tempFile = FormatProbeTestHelper.CreateTempFile(".pvr", data);
            try
            {
                var result = FormatProbe.ProbeTexture(tempFile);

                Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
                Assert.Equal("PVR Texture", result.FormatName);
                Assert.Equal(
                    "Not a valid PVR texture (invalid or truncated PVRT/GBIX header)",
                    result.UnsupportedReason);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ProbeTexture_PvrHeaderOnly_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".pvr", BuildPvrtHeader(8));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal(
                "Not a valid PVR texture (invalid or truncated PVRT/GBIX header)",
                result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0x01, 8)]
    [InlineData(0x02, 12)]
    [InlineData(0x03, 2049)]
    [InlineData(0x04, 2050)]
    [InlineData(0x09, 8)]
    [InlineData(0x0D, 8)]
    public void ProbeTexture_PvrSupportedLayoutsWithExactPayload_Supported(
        byte dataType,
        int requiredPayloadSize)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildPvr(dataType, requiredPayloadSize));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0x01, 8)]
    [InlineData(0x02, 12)]
    [InlineData(0x03, 2049)]
    [InlineData(0x04, 2050)]
    [InlineData(0x09, 8)]
    [InlineData(0x0D, 8)]
    public void ProbeTexture_PvrPhysicalPayloadOutsideDeclaredChunk_Unsupported(
        byte dataType,
        int requiredPayloadSize)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr",
            BuildPvr(
                dataType,
                requiredPayloadSize,
                declaredPayloadSize: requiredPayloadSize - 1));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal(
                "Not a valid PVR texture (invalid or truncated PVRT/GBIX header)",
                result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PvrUnsupportedLayout_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildRectanglePvr(dataType: 5));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal("Unsupported PVR texture layout 0x500", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void ProbeTexture_PvrInvalidDimensions_Unsupported(int width, int height)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildRectanglePvr(width: (ushort)width, height: (ushort)height));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal($"Invalid PVR texture dimensions {width}x{height}", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x04)]
    public void ProbeTexture_PvrVqWidthBelowOneBlock_Unsupported(byte dataType)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildPvr(dataType, 0, width: 1));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal("Invalid PVR texture dimensions 1x2", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PvrTwiddledMipOffsetOverflow_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildPvr(0x02, 0, width: ushort.MaxValue));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal("Invalid PVR texture dimensions 65535x2", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PvrUnrepresentableRgbaArea_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildRectanglePvr(width: ushort.MaxValue, height: ushort.MaxValue));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
            Assert.Equal(
                "PVR texture dimensions 65535x65535 exceed the maximum supported RGBA output size",
                result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PvrArbitraryGbixPayloadAndTrailingBytes_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildRectanglePvr(gbixDataSize: 12, trailingBytes: 7));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PvrDirectContainerWithTrailingBytes_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".pvr", BuildRectanglePvr(trailingBytes: 7));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PVR Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x06)]
    public void ProbeTexture_PsxRecognizedMagic_Supported(byte firstMagicByte)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".psx", [firstMagicByte, 0x00, 0x02, 0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSX Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PsxMixedCaseExtension_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".PsX", [0x03, 0x00, 0x02, 0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSX Texture", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PsxInvalidMagic_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".psx", [0x00, 0x00, 0x00, 0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("invalid magic", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ProbeTexture_PsxTruncatedMagic_Unsupported(int byteCount)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".psx", new byte[byteCount]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("File too small", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_MissingPsxFile_ReportsHeaderReadFailure()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.psx");

        var result = FormatProbe.ProbeTexture(missingPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal("Unknown", result.FormatName);
        Assert.Equal("Failed to read file header", result.UnsupportedReason);
    }

    [Fact]
    public void ProbeTexture_Ps2TexV3_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex", BitConverter.GetBytes(3u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("PS2 TEX", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_Ps2TexV5_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.ps2", BitConverter.GetBytes(5u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_Ps2ImgV2_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img", BitConverter.GetBytes(2u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PS2 IMG (v2)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_RwTxd_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex", BitConverter.GetBytes(0x0016u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RenderWare TXD", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_ThawTexPs2_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.ps2", BitConverter.GetBytes(256u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("script data", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_TdxFile_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tdx", [0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("TDX", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_XboxTexV1_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.xbx", BitConverter.GetBytes(1u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Xbox TEX", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_XboxImgV2_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.xbx", BitConverter.GetBytes(2u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Xbox IMG", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_ThawPcImg_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.wpc",
        [
            0x0D, 0xD0, 0xAD, 0xAB,
            0x02, 0x00, 0x14, 0x00
        ]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("THAW PC IMG", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_ThawPcStex_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".stex",
        [
            0x0D, 0xD0, 0xAD, 0xAB,
            0x01, 0x00, 0x01, 0x00
        ]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("THAW PC TEX", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".tex.ngc")]
    [InlineData(".stex.ngc")]
    [InlineData(".tex.stex.ngc")]
    public void ProbeTexture_NgcTex_Supported(string suffix)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(suffix, NgcTexTestBuilder.CreateDictionary());
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("NGC TEX", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("zone.stex.ngc")]
    [InlineData("skater.tex.stex.ngc")]
    [InlineData("legacy.tex.ngc")]
    [InlineData("single.img.ngc")]
    public void IsNgcTextureFileName_RecognizesEveryViewerSuffix(string fileName)
    {
        Assert.True(FormatProbeTexture.IsNgcTextureFileName(fileName));
    }

    [Theory]
    [InlineData(PspImgFile.Project8FinalBuildWord)]
    [InlineData(PspImgFile.Project8Rev1BuildWord)]
    public void ProbeTexture_Project8PspImg_UsesThePspRoute(uint buildWord)
    {
        var header = new byte[36];
        BitConverter.GetBytes(4u).CopyTo(header, 0);
        BitConverter.GetBytes(buildWord).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 28);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 30);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.psp", header);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSP IMG (Project 8)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_Project8PspImg_RejectsMetadataOnlyHeader()
    {
        var header = new byte[32];
        BitConverter.GetBytes(4u).CopyTo(header, 0);
        BitConverter.GetBytes(PspImgFile.Project8FinalBuildWord).CopyTo(header, 4);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.psp", header);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("PSP IMG (Project 8)", result.FormatName);
            Assert.Contains("pixel region", result.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("skater.tex.xen", true)]
    [InlineData("level.STEX.XEN", true)]
    [InlineData("skater.tex.ps3", true)]
    [InlineData("level.stex.ps3", true)]
    [InlineData("01234567.tex.dat", true)]
    [InlineData("single.img.xen", false)]
    [InlineData("single.img.ps3", false)]
    public void IsNextGenTextureFileName_ExcludesTheDistinctImgFormat(
        string fileName,
        bool expected)
    {
        Assert.Equal(expected, FormatProbeTexture.IsNextGenTextureFileName(fileName));
    }

    /// <summary>
    ///     <c>.tex.xen</c> became supported on 2026-08-27 (the FACECAA7 next-gen
    ///     dictionary), so the extension alone is no longer a rejection — but a
    ///     file that does NOT carry the magic still is.
    /// </summary>
    [Fact]
    public void ProbeTexture_XenTexWithoutMagic_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.xen", [0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("FACECAA7", result.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_XenTexDictionary_Supported()
    {
        var header = new byte[0x20];
        header[0] = 0xFA; header[1] = 0xCE; header[2] = 0xCA; header[3] = 0xA7;
        header[4] = 1;      // Xenon
        header[5] = 0x1C;   // header size, echoed at +0x18
        header[0x0B] = 0x20; // empty table/data start
        header[0x0F] = 0x20;
        header[0x10] = 0xFF; header[0x11] = 0xFF; header[0x12] = 0xFF; header[0x13] = 0xFF;
        header[0x1B] = 0x1C;
        header.AsSpan(0x1C).Fill(0xEF);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.xen", header);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_XenTexWithOnlyMagic_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".tex.xen", [0xFA, 0xCE, 0xCA, 0xA7, 1, 0x1C, 0, 0]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("truncated", result.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_PspImgWithOverflowingLogicalDimensions_UnsupportedWithoutThrowing()
    {
        var header = new byte[32];
        BitConverter.GetBytes(4u).CopyTo(header, 0);
        BitConverter.GetBytes(PspImgFile.Project8FinalBuildWord).CopyTo(header, 4);
        BitConverter.GetBytes(12u).CopyTo(header, 8);
        BitConverter.GetBytes(12u).CopyTo(header, 12);
        BitConverter.GetBytes((ushort)32768).CopyTo(header, 28);
        BitConverter.GetBytes((ushort)32768).CopyTo(header, 30);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.psp", header);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("dimensions", result.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_NgcTexUnsupportedFormat_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.ngc", NgcTexTestBuilder.CreateDictionary(0, 0));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("Unsupported NGC texture format", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_NgcTexHeaderWithUndecodablePayload_Unsupported()
    {
        var data = NgcTexTestBuilder.CreateDictionary();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8 + 16), 1);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.ngc", data);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("NGC TEX", result.FormatName);
            Assert.Contains("decode", result.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeTexture_NgcImgBareRecord_RequiresADecodablePayload()
    {
        var data = NgcTexTestBuilder.CreateBareRecord(
            formatA: 0,
            widthLog2: 2,
            heightLog2: 2,
            widthPadding: 0,
            heightPadding: 0,
            dataSize: 32);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".img.ngc", data);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("NGC IMG", result.FormatName);
            Assert.Contains("Unsupported NGC texture format", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static byte[] BuildRectanglePvr(
        byte dataType = 9,
        ushort width = 2,
        ushort height = 2,
        int gbixDataSize = -1,
        int trailingBytes = 0)
    {
        return BuildPvr(dataType, 8, width, height, gbixDataSize, trailingBytes);
    }

    private static byte[] BuildPvr(
        byte dataType,
        int payloadSize,
        ushort width = 2,
        ushort height = 2,
        int gbixDataSize = -1,
        int trailingBytes = 0,
        int? declaredPayloadSize = null)
    {
        var pvrtOffset = gbixDataSize >= 0 ? 8 + gbixDataSize : 0;
        var data = new byte[pvrtOffset + 16 + payloadSize + trailingBytes];
        if (gbixDataSize >= 0)
        {
            "GBIX"u8.CopyTo(data);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)gbixDataSize);
            data.AsSpan(8, gbixDataSize).Fill(0xA5);
        }

        "PVRT"u8.CopyTo(data.AsSpan(pvrtOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(pvrtOffset + 4),
            (uint)(8 + (declaredPayloadSize ?? payloadSize)));
        data[pvrtOffset + 8] = 1; // RGB565
        data[pvrtOffset + 9] = dataType;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pvrtOffset + 12), width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pvrtOffset + 14), height);
        return data;
    }

    private static byte[] BuildPvrtHeader(uint pvrtDataSize)
    {
        var data = new byte[16];
        "PVRT"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), pvrtDataSize);
        data[8] = 1; // RGB565
        data[9] = 9; // rectangle
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 2);
        return data;
    }
}
