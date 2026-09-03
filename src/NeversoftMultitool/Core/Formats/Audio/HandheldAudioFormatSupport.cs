namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Shared routing for the directly decodable Nintendo DS and PSP stream
///     formats. Keeping this policy in Core lets the GUI's batch and preview
///     paths use the same dispatch and keeps the cross-platform test target able
///     to verify it.
/// </summary>
internal static class HandheldAudioFormatSupport
{
    public static readonly string[] Extensions = [".swav", ".strm", ".hwas", ".at3"];

    public static string? DetectFormat(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".swav" => "SWAV",
            ".strm" => "STRM",
            ".hwas" => "HWAS",
            ".at3" => "AT3",
            _ => null
        };
    }

    /// <summary>
    ///     Converts a supported single-stream format, or returns <see langword="null" />
    ///     when <paramref name="audioFormat" /> belongs to another converter family.
    /// </summary>
    public static AudioConvertResult? ConvertToWav(
        string audioFormat,
        byte[] data,
        string outputStem,
        string outputDirectory)
    {
        return audioFormat.ToUpperInvariant() switch
        {
            "SWAV" or "STRM" or "HWAS" =>
                NdsAudioDecoder.ConvertToWav(data, outputStem, outputDirectory),
            "DSP" => WiiDspAudio.ConvertToWav(data, outputStem, outputDirectory),
            "AT3" => At3Decoder.ConvertToWav(data, outputStem, outputDirectory),
            _ => null
        };
    }
}
