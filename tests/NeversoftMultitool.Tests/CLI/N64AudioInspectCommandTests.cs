using NeversoftMultitool.CLI;
using NeversoftMultitool.Tests.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64AudioInspectCommandTests
{
    [Fact]
    public void Execute_CanonicalOutputPathsCannotOverwritePointerOrWaveSources()
    {
        using var temp = new TempDirectory();
        var (pointerData, waveData) = N64SoundToolsBankTests.BuildPair(pointerTail: 6, waveTail: 1);
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        var wavePath = Path.Combine(temp.Path, "waves.wbk");
        File.WriteAllBytes(pointerPath, pointerData);
        File.WriteAllBytes(wavePath, waveData);

        var pointerAlias = Path.Combine(temp.Path, ".", "bank.ptr.n64");
        var waveAlias = Path.Combine(temp.Path, "nested", "..", "waves.wbk");
        Assert.Equal(1, N64AudioInspectCommand.Execute(
            pointerPath, wavePath, pointerAlias, TestContext.Current.CancellationToken));
        Assert.Equal(1, N64AudioInspectCommand.Execute(
            pointerPath, wavePath, waveAlias, TestContext.Current.CancellationToken));

        Assert.Equal(pointerData, File.ReadAllBytes(pointerPath));
        Assert.Equal(waveData, File.ReadAllBytes(wavePath));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesBeforeInputOrOutputAccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].ptr.n64");
        var output = Path.Combine(temp.Path, "nested", "cancelled.json");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => N64AudioInspectCommand.Execute(
            input,
            wavePath: null,
            jsonPath: output,
            cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        Assert.False(File.Exists(output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-audio-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
