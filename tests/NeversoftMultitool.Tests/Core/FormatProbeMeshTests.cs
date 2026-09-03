using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeMeshTests
{
    [Fact]
    public void ProbeMesh_DdmFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".ddm", [0x00]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("DDM Mesh", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_Ps2SceneThps4_Supported()
    {
        var data = new byte[12];
        BitConverter.GetBytes(3u).CopyTo(data, 0);
        BitConverter.GetBytes(4u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("THPS4", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_Ps2SceneThug_Supported()
    {
        var data = new byte[12];
        BitConverter.GetBytes(5u).CopyTo(data, 0);
        BitConverter.GetBytes(6u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".mdl.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("THUG", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_Thug2PreCompiled_Unsupported()
    {
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(100u).CopyTo(data, 4);
        BitConverter.GetBytes(200u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_ThawSkinPs2_TooSmallForDetection()
    {
        var data = new byte[12];
        BitConverter.GetBytes(65536u).CopyTo(data, 0);
        BitConverter.GetBytes(2496u).CopyTo(data, 4);
        BitConverter.GetBytes(1792u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skin.ps2", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_XboxScene_Supported()
    {
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skin.xbx", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Xbox", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_PcScene_Supported()
    {
        var data = new byte[12];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".mdl.wpc", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Xbox", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_ColFileV9_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".col.xbx", BitConverter.GetBytes(9));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("COL", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_Thps4ColFileV8_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".col.ps2", BitConverter.GetBytes(8));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("COL Collision (v8)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_XenColFileBigEndianV10_Supported()
    {
        var data = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data, 10);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".col.xen", data);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("X360 COL Collision (v10)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_BspValidMagic_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".bsp", BuildRwRoot(0x000B, 0, 12));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RW BSP Level", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_BspInvalidMagic_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".bsp", [0xFF, 0xFF, 0xFF, 0xFF]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_RwDff_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skn", BuildRwRoot(0x0010, 0, 12));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RW DFF Mesh", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_Ps2Geom_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".geom.ps2", [0x00]);
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PS2 GEOM", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".skn", 0x0010u)]
    [InlineData(".bsp", 0x000Bu)]
    public void ProbeMesh_RenderWareRootTruncated_Unsupported(string extension, uint type)
    {
        foreach (var length in new[] { 4, 11 })
        {
            var tempFile = FormatProbeTestHelper.CreateTempFile(extension, BuildRwRoot(type, 0, length));
            try
            {
                var result = FormatProbe.ProbeMesh(tempFile);

                Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ProbeMesh_DffRootPayloadPastFile_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".skn", BuildRwRoot(0x0010, 1, 12));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeMesh_BspRootPayloadPastFile_RemainsSupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".bsp", BuildRwRoot(0x000B, 1, 12));
        try
        {
            var result = FormatProbe.ProbeMesh(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("RW BSP Level", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".skn", 0x0010u, "RW DFF Mesh")]
    [InlineData(".bsp", 0x000Bu, "RW BSP Level")]
    public void ProbeMesh_RenderWareRootPayloadEndingAtOrBeforeFile_Supported(
        string extension,
        uint type,
        string expectedFormat)
    {
        foreach (var length in new[] { 13, 14 })
        {
            var tempFile = FormatProbeTestHelper.CreateTempFile(extension, BuildRwRoot(type, 1, length));
            try
            {
                var result = FormatProbe.ProbeMesh(tempFile);

                Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
                Assert.Equal(expectedFormat, result.FormatName);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }

    private static byte[] BuildRwRoot(uint type, uint payloadSize, int length)
    {
        var data = new byte[length];
        if (length >= 4)
            BitConverter.GetBytes(type).CopyTo(data, 0);
        if (length >= 8)
            BitConverter.GetBytes(payloadSize).CopyTo(data, 4);
        return data;
    }
}
