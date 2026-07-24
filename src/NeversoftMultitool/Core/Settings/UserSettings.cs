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

    private static readonly object Sync = new();
    private static string _subKeyPath = DefaultSubKeyPath;

    /// <summary>Raised after any setting is written (on the writing thread).</summary>
    public static event Action? Changed;

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
                    key.DeleteValue(BlenderPathValueName, throwOnMissingValue: false);
                else
                    key.SetValue(BlenderPathValueName, value.Trim());
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Test hook: redirect to a scratch subkey (null restores the default).</summary>
    internal static void OverrideSubKeyForTesting(string? subKeyPath)
    {
        lock (Sync)
        {
            _subKeyPath = subKeyPath ?? DefaultSubKeyPath;
        }
    }
}
