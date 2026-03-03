using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeTests
{
    // --- Texture probing ---

    [Fact]
    public void ProbeTexture_PsxFile_Supported()
    {
        var tempFile = CreateTempFile(".psx", [0x03, 0x00, 0x02, 0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSX Texture", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_Ps2TexV3_Supported()
    {
        var tempFile = CreateTempFile(".tex", BitConverter.GetBytes(3u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("PS2 TEX", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_Ps2TexV5_Supported()
    {
        var tempFile = CreateTempFile(".tex.ps2", BitConverter.GetBytes(5u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_Ps2ImgV2_Supported()
    {
        var tempFile = CreateTempFile(".img", BitConverter.GetBytes(2u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PS2 IMG (v2)", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_RwTxd_Supported()
    {
        var tempFile = CreateTempFile(".tex", BitConverter.GetBytes(0x0016u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RenderWare TXD", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_ThawTexPs2_Unsupported()
    {
        // THAW .tex.ps2 files have version 256 (0x100) — QB script data
        var tempFile = CreateTempFile(".tex.ps2", BitConverter.GetBytes(256u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("script data", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_TdxFile_Unsupported()
    {
        var tempFile = CreateTempFile(".tdx", [0x00]);
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("TDX", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_XboxTexV1_Supported()
    {
        var tempFile = CreateTempFile(".tex.xbx", BitConverter.GetBytes(1u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Xbox TEX", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeTexture_XboxImgV2_Supported()
    {
        var tempFile = CreateTempFile(".img.wpc", BitConverter.GetBytes(2u));
        try
        {
            var result = FormatProbe.ProbeTexture(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Xbox IMG", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    // --- Mesh probing ---

    [Fact]
    public void ProbeMesh_DdmFile_Supported()
    {
        var tempFile = CreateTempFile(".ddm", [0x00]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("DDM Mesh", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_Ps2SceneThps4_Supported()
    {
        // Version triple (3,4,1) = THPS4
        var data = new byte[12];
        BitConverter.GetBytes(3u).CopyTo(data, 0);
        BitConverter.GetBytes(4u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("THPS4", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_Ps2SceneThug_Supported()
    {
        // Version triple (5,6,1) = THUG
        var data = new byte[12];
        BitConverter.GetBytes(5u).CopyTo(data, 0);
        BitConverter.GetBytes(6u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".mdl.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("THUG", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_Thug2PreCompiled_PartiallySupported()
    {
        // matVersion = 1 means pre-compiled VIF/DMA
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(100u).CopyTo(data, 4);
        BitConverter.GetBytes(200u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.PartiallySupported, result.Support);
            Assert.Contains("iskin", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_ThawSkinPs2_Unsupported()
    {
        // THAW version triples are garbage values
        var data = new byte[12];
        BitConverter.GetBytes(65536u).CopyTo(data, 0);
        BitConverter.GetBytes(2496u).CopyTo(data, 4);
        BitConverter.GetBytes(1792u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("THAW", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_XboxScene_Supported()
    {
        // Version triple (1,1,1)
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".skin.xbx", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Xbox", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_PcScene_Supported()
    {
        // Version triple (1,1,1)
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = CreateTempFile(".mdl.wpc", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Xbox", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_ColFileV9_Supported()
    {
        var tempFile = CreateTempFile(".col.xbx", BitConverter.GetBytes(9));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("COL", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_BspValidMagic_Supported()
    {
        // RW_WORLD = 0x000B
        var tempFile = CreateTempFile(".bsp", BitConverter.GetBytes(0x000Bu));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RW BSP Level", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_BspInvalidMagic_Unsupported()
    {
        var tempFile = CreateTempFile(".bsp", [0xFF, 0xFF, 0xFF, 0xFF]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_RwDff_Supported()
    {
        // RW_CLUMP = 0x0010
        var tempFile = CreateTempFile(".skn", BitConverter.GetBytes(0x0010u));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RW DFF Mesh", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeMesh_Ps2Geom_Supported()
    {
        var tempFile = CreateTempFile(".geom.ps2", [0x00]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PS2 GEOM", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    // --- Archive probing ---

    [Fact]
    public void ProbeArchive_WadFile_Supported()
    {
        var tempFile = CreateTempFile(".wad", [0x00]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeArchive_CompressedPre_Supported()
    {
        // Compressed PRE v2: offset 4 = 0xABCD0002
        var data = new byte[8];
        BitConverter.GetBytes(0xABCD0002u).CopyTo(data, 4);
        var tempFile = CreateTempFile(".pre", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Compressed", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeArchive_PlainPre_Supported()
    {
        var tempFile = CreateTempFile(".pre", new byte[8]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PRE Archive", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeArchive_BonV1_Supported()
    {
        var data = new byte[8];
        data[0] = (byte)'B'; data[1] = (byte)'o'; data[2] = (byte)'n'; data[3] = 0;
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        var tempFile = CreateTempFile(".bon", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("BON", result.FormatName);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeArchive_BonUnsupportedVersion_Unsupported()
    {
        var data = new byte[8];
        data[0] = (byte)'B'; data[1] = (byte)'o'; data[2] = (byte)'n'; data[3] = 0;
        BitConverter.GetBytes(99u).CopyTo(data, 4);
        var tempFile = CreateTempFile(".bon", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("99", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeArchive_PakFile_Unsupported()
    {
        var tempFile = CreateTempFile(".pak", [0x00]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("opaque", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    // --- Audio probing ---

    [Fact]
    public void ProbeAudio_AdxFile_Supported()
    {
        // ADX: 0x80 0x00 magic, encoding=3 at offset 4
        var data = new byte[8];
        data[0] = 0x80; data[1] = 0x00; data[4] = 3;
        var tempFile = CreateTempFile(".adx", data);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeAudio_AdxUnsupportedEncoding_Unsupported()
    {
        var data = new byte[8];
        data[0] = 0x80; data[1] = 0x00; data[4] = 7; // encoding type 7
        var tempFile = CreateTempFile(".adx", data);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("encoding", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeAudio_VagFile_Supported()
    {
        var tempFile = CreateTempFile(".vag", [0x00]);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeAudio_XaFile_Supported()
    {
        var tempFile = CreateTempFile(".xa", [0x00]);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    // --- Video probing ---

    [Fact]
    public void ProbeVideo_SfdFile_Supported()
    {
        var tempFile = CreateTempFile(".sfd", [0x00]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeVideo_StrInvalidSize_Unsupported()
    {
        // STR files must be multiples of 2336 bytes
        var tempFile = CreateTempFile(".str", new byte[100]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("2336", result.UnsupportedReason!);
        }
        finally { File.Delete(tempFile); }
    }

    [Fact]
    public void ProbeVideo_StrValidSize_Supported()
    {
        // 2336 bytes = valid STR sector
        var tempFile = CreateTempFile(".str", new byte[2336]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally { File.Delete(tempFile); }
    }

    // --- PartitionFiles helper ---

    [Fact]
    public void PartitionFiles_SeparatesSupportedAndUnsupported()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Probe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            // Create supported file (version 3 = PS2 TEX)
            var supportedFile = Path.Combine(tempDir, "good.tex");
            File.WriteAllBytes(supportedFile, BitConverter.GetBytes(3u));

            // Create unsupported file (garbage version)
            var unsupportedFile = Path.Combine(tempDir, "bad.tex");
            File.WriteAllBytes(unsupportedFile, BitConverter.GetBytes(999u));

            var files = new[] { supportedFile, unsupportedFile };
            var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeTexture);

            Assert.Single(supported);
            Assert.Single(unsupported);
            Assert.Contains("good.tex", Path.GetFileName(supported[0]));
            Assert.Equal("bad.tex", unsupported[0].FileName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // --- Helper ---

    private static string CreateTempFile(string extension, byte[] content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Probe");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, $"test_{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(filePath, content);
        return filePath;
    }
}
