using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Converts a PSY-Q SEQ song to WAV by rendering it through its VAB bank.
///     The bank is the same-stem <c>.vab</c> sibling — the pairing every
///     Apocalypse song ships with (<c>backwall.seq</c> + <c>backwall.vab</c>).
/// </summary>
public static class SeqExtractor
{
    public static AudioConvertResult ConvertToWav(
        string inputPath, string outputDirectory, string? outputStem = null)
    {
        byte[] seqData;
        try
        {
            seqData = File.ReadAllBytes(inputPath);
        }
        catch (Exception ex)
        {
            return Failure($"Unable to read SEQ: {ex.Message}");
        }

        // Check the magic before looking for a bank. `.seq` is shared with an unrelated
        // Dreamcast "Sequencer File V1.0" container (22 of the corpus's 35), and those have no
        // .vab sibling — probing for one first reported the missing bank as the fault instead
        // of the real answer, which is that the file is not a PSY-Q song at all.
        if (!SeqFile.IsSeq(seqData))
            return NotThisFormat();

        var vabPath = Path.ChangeExtension(inputPath, ".vab");
        if (!File.Exists(vabPath))
        {
            // Preserve the source spelling (VAB vs vab) when probing.
            var upper = Path.ChangeExtension(inputPath, ".VAB");
            if (File.Exists(upper))
                vabPath = upper;
        }

        if (!File.Exists(vabPath))
            return Failure("No same-stem .vab bank beside the SEQ (required for rendering)");

        byte[] vabData;
        try
        {
            vabData = File.ReadAllBytes(vabPath);
        }
        catch (Exception ex)
        {
            return Failure($"Unable to read companion VAB: {ex.Message}");
        }

        return ConvertToWav(
            seqData,
            vabData,
            outputStem ?? Path.GetFileNameWithoutExtension(inputPath),
            outputDirectory);
    }

    public static AudioConvertResult ConvertToWav(
        byte[] seqData, byte[] vabData, string outputStem, string outputDirectory)
    {
        var seq = SeqFile.Parse(seqData);
        if (seq == null)
            return NotThisFormat();

        var vab = VabProgramSet.Parse(vabData);
        if (vab == null)
            return Failure("Companion bank is not a readable VAB (pBAV) file");

        var pcm = SeqSynthesizer.Render(seq, vab);
        if (pcm == null || pcm.Length == 0)
            return Failure("SEQ produced no audio");

        Directory.CreateDirectory(outputDirectory);
        var wavPath = Path.Combine(outputDirectory, outputStem + ".wav");
        WavWriter.WritePcm16(wavPath, SeqSynthesizer.OutputSampleRate, 2, pcm);
        return new AudioConvertResult { Success = true, SamplesWritten = 1 };
    }

    private static AudioConvertResult Failure(string message)
    {
        return new AudioConvertResult { Success = false, ErrorMessage = message };
    }

    private static AudioConvertResult NotThisFormat()
    {
        return new AudioConvertResult
        {
            Success = false,
            Skipped = true,
            ErrorMessage = "Not a PSY-Q SEQ (pQES) song"
        };
    }
}
