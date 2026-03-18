namespace NeversoftMultitool.Tests.Core;

internal static class FormatProbeTestHelper
{
    public static string CreateTempFile(string extension, byte[] content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Probe");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, $"test_{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    public static string CreateTempDirectory(string prefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}
