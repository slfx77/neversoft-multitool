using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeTextureTests
{
    [Fact]
    public void ProbeTexture_PsxFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".psx", [0x03, 0x00, 0x02, 0x00]);
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
}
