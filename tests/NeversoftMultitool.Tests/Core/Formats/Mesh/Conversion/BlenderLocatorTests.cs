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
        PreflightRegistryWriteOrSkip(subKey);
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
                TryDeleteRegistrySubKey(subKey);
        }
    }

    [Fact]
    public void UserSettings_BlenderPath_RoundTripsThroughRegistry()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Settings are registry-backed (Windows only)");

        var subKey = $@"Software\NsMultitool_Test_{Guid.NewGuid():N}";
        PreflightRegistryWriteOrSkip(subKey);
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
                TryDeleteRegistrySubKey(subKey);
        }
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(-0.25, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.25, 1.0)]
    public void NormalizePlayerVolume_MapsNaNAndClampsOtherValues(
        double value,
        double expected)
    {
        var normalized = UserSettings.NormalizePlayerVolume(value);

        Assert.True(double.IsFinite(normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void UserSettings_PlayerVolume_NormalizesNaNToDefault()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Settings are registry-backed (Windows only)");

        var subKey = $@"Software\NsMultitool_Test_{Guid.NewGuid():N}";
        PreflightRegistryWriteOrSkip(subKey);
        UserSettings.OverrideSubKeyForTesting(subKey);
        var changedFired = 0;
        Action onChanged = () => changedFired++;
        UserSettings.Changed += onChanged;
        try
        {
            UserSettings.PlayerVolume = 0.25;
            Assert.Equal(0.25, UserSettings.PlayerVolume);
            Assert.Equal(1, changedFired);

            UserSettings.PlayerVolume = double.NaN;

            var normalized = UserSettings.PlayerVolume;
            Assert.True(double.IsFinite(normalized));
            Assert.Equal(1.0, normalized);
            Assert.Equal(2, changedFired);
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                Assert.Equal("1", key?.GetValue("PlayerVolume") as string);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                key.SetValue("PlayerVolume", "NaN");
            }

            var loaded = UserSettings.PlayerVolume;
            Assert.True(double.IsFinite(loaded));
            Assert.Equal(1.0, loaded);
            Assert.Equal(2, changedFired);
        }
        finally
        {
            UserSettings.Changed -= onChanged;
            UserSettings.OverrideSubKeyForTesting(null);
            if (OperatingSystem.IsWindows())
                TryDeleteRegistrySubKey(subKey);
        }
    }

    private static void PreflightRegistryWriteOrSkip(string subKey)
    {
        try
        {
            using (Registry.CurrentUser.CreateSubKey(subKey))
            {
            }

            Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteRegistrySubKey(subKey);
            Assert.Skip(
                "HKCU registry writes are unavailable in this sandbox or test environment.");
        }
    }

    private static void TryDeleteRegistrySubKey(string subKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
        }
        catch (UnauthorizedAccessException)
        {
            // Registry-denied cleanup must not mask a skipped test or assertion failure.
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
