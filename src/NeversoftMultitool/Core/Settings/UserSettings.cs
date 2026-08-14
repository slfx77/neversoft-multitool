using System.Globalization;
using Microsoft.Win32;

namespace NeversoftMultitool.Core.Settings;

/// <summary>
///     Persisted user preferences shared by the GUI and CLI, stored in the
///     registry under HKCU\Software\NeversoftMultitool (same scheme as the
///     Bethesda Multitool this GUI is modeled on). Windows-only: on other
///     platforms reads return null and writes are ignored — the GUI is
///     Windows-only and the CLI takes explicit paths instead.
/// </summary>
public static class UserSettings
{
    private const string DefaultSubKeyPath = @"Software\NeversoftMultitool";
    private const string BlenderPathValueName = "BlenderPath";
    private const string PlayerVolumeValueName = "PlayerVolume";

    private static readonly object Sync = new();
    private static string _subKeyPath = DefaultSubKeyPath;

    /// <summary>Where the settings live, for user-facing messages.</summary>
    public static string StorageDescription => $@"HKEY_CURRENT_USER\{_subKeyPath}";

    /// <summary>
    ///     User-pinned Blender executable for .blend export, or null to
    ///     auto-detect. Write failures propagate so the caller can surface them.
    /// </summary>
    public static string? BlenderPath
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            lock (Sync)
            {
                using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
                var value = key?.GetValue(BlenderPathValueName) as string;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            lock (Sync)
            {
                using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath);
                if (string.IsNullOrWhiteSpace(value))
                    key.DeleteValue(BlenderPathValueName, false);
                else
                    key.SetValue(BlenderPathValueName, value.Trim());
            }

            Changed?.Invoke();
        }
    }

    /// <summary>
    ///     Shared preview-player volume for the audio and video tabs, 0..1
    ///     (default 1.0). Stored as an invariant-culture string.
    /// </summary>
    public static double PlayerVolume
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 1.0;
            lock (Sync)
            {
                using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
                var raw = key?.GetValue(PlayerVolumeValueName) as string;
                return raw != null
                       && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? NormalizePlayerVolume(value)
                    : 1.0;
            }
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            var clamped = NormalizePlayerVolume(value);
            lock (Sync)
            {
                // Skip no-op writes: Changed listeners re-probe Blender and
                // rebuild UI state, so a same-value write is never free.
                if (Math.Abs(PlayerVolume - clamped) < 0.0005) return;
                using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath);
                key.SetValue(
                    PlayerVolumeValueName,
                    clamped.ToString("R", CultureInfo.InvariantCulture));
            }

            Changed?.Invoke();
        }
    }

    internal static double NormalizePlayerVolume(double value)
    {
        return double.IsNaN(value) ? 1.0 : Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Raised after any setting is written (on the writing thread).</summary>
    public static event Action? Changed;

    /// <summary>Test hook: redirect to a scratch subkey (null restores the default).</summary>
    internal static void OverrideSubKeyForTesting(string? subKeyPath)
    {
        lock (Sync)
        {
            _subKeyPath = subKeyPath ?? DefaultSubKeyPath;
        }
    }
}
