using System.Collections.Frozen;

namespace NeversoftMultitool.Core;

public static partial class QbKey
{
    private static readonly FrozenDictionary<uint, string> KnownNames = LoadKnownNames();

    private static FrozenDictionary<uint, string> LoadKnownNames()
    {
        var dict = new Dictionary<uint, string>();

        using var stream = typeof(QbKey).Assembly.GetManifestResourceStream("QbKeyNames.txt");
        if (stream == null)
            return dict.ToFrozenDictionary();

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // Format: name=0xHASH
            var eq = line.IndexOf('=');
            if (eq < 1 || eq >= line.Length - 1)
                continue;

            var name = line[..eq];
            var hashStr = line[(eq + 1)..];
            if (uint.TryParse(hashStr.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hash))
                dict.TryAdd(hash, name);
        }

        return dict.ToFrozenDictionary();
    }
}
