using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psx;

public class PsxTextureReviewLookupTests
{
    private const uint TargetHash = 0x12345678;

    [Fact]
    public void TryExtractFromAllFiles_ConflictingSameBasenamePreviews_AreRejected()
    {
        var root = CreateFixtureRoot();
        try
        {
            WriteLibrary(root, "BuildA", "shared.psx", TargetHash, 0x001F);
            WriteLibrary(root, "BuildB", "shared.psx", TargetHash, 0x7C00);
            WriteLibrary(root, "BuildC", "shared.psx", TargetHash, 0x7C00);
            var diagnostics = new List<string>();

            var result = PsxTextureReviewLookup.TryExtractFromAllFiles(
                root,
                ["shared.psx"],
                TargetHash,
                diagnostics);

            Assert.Null(result);
            var diagnostic = Assert.Single(
                diagnostics,
                item => item.Contains("conflicting previews", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("in 3 PSX files", diagnostic, StringComparison.Ordinal);
            Assert.Contains("BuildA", diagnostic, StringComparison.Ordinal);
            Assert.Contains("BuildB", diagnostic, StringComparison.Ordinal);
            Assert.Contains("BuildC", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryExtractFromAllFiles_IdenticalSameBasenamePreviews_AreCoalesced()
    {
        var root = CreateFixtureRoot();
        try
        {
            WriteLibrary(root, "BuildA", "shared.psx", TargetHash, 0x001F);
            WriteLibrary(root, "BuildB", "shared.psx", TargetHash, 0x001F);
            var diagnostics = new List<string>();

            var result = PsxTextureReviewLookup.TryExtractFromAllFiles(
                root,
                ["shared.psx"],
                TargetHash,
                diagnostics);

            Assert.NotNull(result);
            Assert.Equal(
                new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 },
                result.Value.Rgba);
            Assert.DoesNotContain(
                diagnostics,
                item => item.Contains("conflicting previews", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryExtractFromAllFiles_OnlyOneMatchingHash_PreservesLegacyLookup()
    {
        var root = CreateFixtureRoot();
        try
        {
            WriteLibrary(root, "BuildA", "shared.psx", TargetHash, 0x001F);
            WriteLibrary(root, "BuildB", "shared.psx", TargetHash + 1, 0x7C00);
            var diagnostics = new List<string>();

            var result = PsxTextureReviewLookup.TryExtractFromAllFiles(
                root,
                ["shared.psx"],
                TargetHash,
                diagnostics);

            Assert.NotNull(result);
            Assert.Equal((2, 1), (result.Value.Width, result.Value.Height));
            Assert.Equal(
                new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 },
                result.Value.Rgba);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"NeversoftMultitool_HashReview_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteLibrary(
        string root,
        string build,
        string fileName,
        uint hash,
        ushort color)
    {
        var directory = Path.Combine(root, build);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), BuildLibrary(hash, color));
    }

    private static byte[] BuildLibrary(uint hash, ushort color)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(new byte[] { 0x04, 0x00, 0x02, 0x00 });
        writer.Write(16u); // tagged-chunk list
        writer.Write(0u); // object count
        writer.Write(0u); // mesh count
        writer.Write(uint.MaxValue); // end of tagged chunks
        writer.Write(1u); // texture-name count
        writer.Write(hash);
        writer.Write(1u); // 4-bit palette count
        writer.Write(0x11111111u);
        for (var i = 0; i < 16; i++)
            writer.Write(color);
        writer.Write(0u); // 8-bit palette count
        writer.Write(1u); // physical texture count
        writer.Write(80u); // texture record pointer
        writer.Write(0u); // flags
        writer.Write(16u); // palette size
        writer.Write(0x11111111u);
        writer.Write(0u); // name-table index
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write(0u); // two indexed pixels plus row padding
        return stream.ToArray();
    }
}
