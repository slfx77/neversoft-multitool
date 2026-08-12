using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoConverterCancellationTests
{
    [Theory]
    [InlineData("ConvertToMp4")]
    [InlineData("DecodeFrames")]
    [InlineData("DecodeNativeFrames")]
    public void EntryPoint_PreCancelledToken_ThrowsBeforeProbingOrCreatingOutput(string entryPoint)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"nmt-vid1-cancellation-{Guid.NewGuid():N}");
        var inputPath = Path.Combine(tempRoot, "empty.vid");
        var outputDir = Path.Combine(tempRoot, "output");

        try
        {
            Directory.CreateDirectory(tempRoot);
            File.WriteAllBytes(inputPath, []);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
            {
                _ = entryPoint switch
                {
                    "ConvertToMp4" => Vid1VideoConverter.ConvertToMp4(
                        inputPath,
                        outputDir,
                        cancellationToken: cts.Token),
                    "DecodeFrames" => Vid1VideoConverter.DecodeFrames(
                        inputPath,
                        outputDir,
                        cts.Token),
                    "DecodeNativeFrames" => Vid1VideoConverter.DecodeNativeFrames(
                        inputPath,
                        outputDir,
                        cts.Token),
                    _ => throw new InvalidOperationException($"Unexpected entry point '{entryPoint}'.")
                };
            });
            Assert.False(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
