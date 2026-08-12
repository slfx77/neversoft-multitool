using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class GlbRendererTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void RenderToFile_BareOutputName_WritesToCurrentDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"nmt-glb-renderer-{Guid.NewGuid():N}");
        var outputName = $"nmt-glb-renderer-{Guid.NewGuid():N}.png";
        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), outputName);

        try
        {
            Directory.CreateDirectory(temp);
            var input = Path.Combine(temp, "empty.glb");
            File.WriteAllBytes(input, BuildEmptySceneGlb());

            GlbRenderer.RenderToFile(input, outputName, longEdge: 8);

            Assert.True(File.Exists(outputPath));
            Assert.Equal(PngSignature, File.ReadAllBytes(outputPath)[..PngSignature.Length]);
        }
        finally
        {
            File.Delete(outputPath);
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static byte[] BuildEmptySceneGlb()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{}]}");
        var paddedJsonLength = (json.Length + 3) & ~3;
        var data = new byte[12 + 8 + paddedJsonLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x4E4F534A); // JSON
        json.AsSpan().CopyTo(data.AsSpan(20));
        data.AsSpan(20 + json.Length, paddedJsonLength - json.Length).Fill(0x20);
        return data;
    }
}
