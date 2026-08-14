using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool.Tests.CLI;

public sealed class Ps2SceneCommandTests
{
    [Fact]
    public void ExecuteWorldzone_RecognizedPakWithNoExportableMesh_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.pak.ps2");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, CreateEmptyRecognizedWorldzonePak());

        Assert.True(Ps2WorldzoneDetection.IsWorldzonePak(input));

        var result = Ps2SceneCommand.Create()
            .Parse([input, "--worldzone", "--output", output])
            .Invoke();

        Assert.Equal(1, result);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static byte[] CreateEmptyRecognizedWorldzonePak()
    {
        var data = new byte[104];
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(0), Ps2WorldzoneDetection.WorldzoneMdlTypeHash);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 96u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 4u);

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(32), Ps2WorldzoneDetection.WorldzonePlacementTypeHash);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 68u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 4u);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 0xB524565Fu);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nmt-ps2scene-command-{Guid.NewGuid():N}");
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
