using Microsoft.Win32;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Settings;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class BlenderLocatorTests
{
    [Fact]
    public void Resolve_ExplicitFile_ReturnsThatFile()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = Path.Combine(dir, "blender.exe");
            File.WriteAllBytes(exe, [0]);

            Assert.Equal(exe, BlenderLocator.Resolve(exe, out var reason));
            Assert.Equal("", reason);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resolve_ExplicitInstallDirectory_FindsExecutableInside()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = Path.Combine(dir, "blender.exe");
            File.WriteAllBytes(exe, [0]);

            Assert.Equal(exe, BlenderLocator.Resolve(dir, out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resolve_ExplicitMissingPath_FailsNamingThatPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}", "blender.exe");

        Assert.Null(BlenderLocator.Resolve(missing, out var reason));
        Assert.Contains(missing, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SavedSettingMissing_FailsNamingThatPath()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Settings are registry-backed (Windows only)");

        var subKey = $@"Software\NsMultitool_Test_{Guid.NewGuid():N}";
        UserSettings.OverrideSubKeyForTesting(subKey);
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}", "blender.exe");
            UserSettings.BlenderPath = missing;

            // A stale pinned path must fail with a pointer at the stale setting,
            // not silently fall through to some other Blender.
            Assert.Null(BlenderLocator.Resolve(null, out var reason));
            Assert.Contains(missing, reason, StringComparison.Ordinal);
        }
        finally
        {
            UserSettings.OverrideSubKeyForTesting(null);
            if (OperatingSystem.IsWindows())
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
        }
    }

    [Fact]
    public void UserSettings_BlenderPath_RoundTripsThroughRegistry()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Settings are registry-backed (Windows only)");

        var subKey = $@"Software\NsMultitool_Test_{Guid.NewGuid():N}";
        UserSettings.OverrideSubKeyForTesting(subKey);
        var changedFired = 0;
        Action onChanged = () => changedFired++;
        UserSettings.Changed += onChanged;
        try
        {
            Assert.Null(UserSettings.BlenderPath);

            UserSettings.BlenderPath = @"C:\Tools\Blender\blender.exe";
            Assert.Equal(@"C:\Tools\Blender\blender.exe", UserSettings.BlenderPath);
            Assert.Equal(1, changedFired);

            UserSettings.BlenderPath = null;
            Assert.Null(UserSettings.BlenderPath);
            Assert.Equal(2, changedFired);
        }
        finally
        {
            UserSettings.Changed -= onChanged;
            UserSettings.OverrideSubKeyForTesting(null);
            if (OperatingSystem.IsWindows())
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), $"NsMultitool_Test_Blender_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}