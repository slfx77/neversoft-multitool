namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Provisions the Wii common key needed to decrypt a retail Wii disc's
///     partition. The key is a copyrighted console secret and is deliberately
///     NOT stored in this repository — it is resolved at runtime from, in order:
///     the <c>NEVERSOFT_WII_COMMON_KEY</c> environment variable (32 hex chars),
///     or a 16-byte <c>wii_common_key.bin</c> under
///     <c>%APPDATA%\NeversoftMultitool\</c>. When neither is present the Wii
///     path declines with <see cref="ProvisioningHint" /> instead of failing
///     opaquely.
/// </summary>
public static class WiiCommonKey
{
    public const string EnvironmentVariable = "NEVERSOFT_WII_COMMON_KEY";

    public const string ProvisioningHint =
        "Wii common key not provisioned. Set the NEVERSOFT_WII_COMMON_KEY environment variable " +
        "to 32 hex characters, or place a 16-byte wii_common_key.bin in " +
        "%APPDATA%\\NeversoftMultitool\\.";

    /// <summary>Returns the 16-byte common key, or null when it is not provisioned.</summary>
    public static byte[]? TryResolve()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var hex = env.Trim();
            if (hex.Length == 32 && TryParseHex(hex, out var key))
                return key;
        }

        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NeversoftMultitool", "wii_common_key.bin");
            if (File.Exists(file))
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length == 16)
                    return bytes;
            }
        }
        catch (IOException)
        {
            // fall through to null
        }

        return null;
    }

    private static bool TryParseHex(string hex, out byte[] key)
    {
        key = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out key[i]))
                return false;
        }

        return true;
    }
}
