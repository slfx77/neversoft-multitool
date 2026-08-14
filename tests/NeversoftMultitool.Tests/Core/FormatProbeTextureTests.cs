using System.Buffers.Binary;
using NeversoftMultitool.Core;
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

    [Fact]
    public void ProbeTexture_NgcTex_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.ngc", NgcTexTestBuilder.CreateDictionary());
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

    [Fact]
    public void ProbeTexture_XenTex_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tex.xen", [0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("cross-platform TEX", result.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
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
