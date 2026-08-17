using System.Buffers.Binary;
using System.CommandLine;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Rendering;
using SixLabors.ImageSharp;

namespace NeversoftMultitool.Tests.CLI;

/// <summary>
///     Drives the real System.CommandLine parser, because a copied pose is text that
///     has to survive a round trip through a shell.
/// </summary>
public sealed class GlbRenderCameraOptionTests
{
    [Fact]
    public void NegativeCoordinatePose_ParsesAndReachesTheRenderer()
    {
        // Negative coordinates are the common case for a level, and they are the shape
        // an argument parser is most likely to mistake for another option.
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.glb");
        var output = Path.Combine(temp.Path, "out");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        var pose = new ViewPose(
            new System.Numerics.Vector3(-24942.67f, 237.78f, -7054.67f),
            180f, -5.13f, 45f, 61, 37);

        var exitCode = Invoke([input, "-o", output, .. pose.ToArguments().Split(' ')]);

        Assert.Equal(0, exitCode);
        using var image = Image.Load(Path.Combine(output, "empty.png"));
        Assert.Equal(61, image.Width);
        Assert.Equal(37, image.Height);
    }

    [Fact]
    public void SpaceSeparatedNegativeEye_AlsoParses()
    {
        // Measured, not assumed: this parser binds a leading-'-' value correctly, so a
        // hand-typed pose works too. We still emit '=' — see ViewPose.ToArguments.
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.glb");
        var output = Path.Combine(temp.Path, "out");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        var exitCode = Invoke(
        [
            input, "-o", output,
            "--camera-eye", "-24942.67,237.78,-7054.67",
            "--camera-yaw", "-180",
            "--camera-size", "64x48"
        ]);

        Assert.Equal(0, exitCode);
        using var image = Image.Load(Path.Combine(output, "empty.png"));
        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
    }

    [Fact]
    public void WithoutACamera_TheAutoFramedPathIsUnchanged()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.glb");
        var output = Path.Combine(temp.Path, "out");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        Assert.Equal(0, Invoke([input, "-o", output, "-s", "48"]));

        using var image = Image.Load(Path.Combine(output, "empty.png"));
        Assert.Equal(48, image.Width);
        Assert.Equal(48, image.Height);
    }

    [Fact]
    public void CameraOptionWithoutAnEye_FailsInsteadOfRenderingTheDefaultView()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.glb");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        Assert.Equal(1, Invoke([input, "-o", Path.Combine(temp.Path, "out"), "--camera-yaw=90"]));
    }

    [Fact]
    public void PresetWithACamera_IsRejectedAsContradictory()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty.glb");
        File.WriteAllBytes(input, BuildEmptySceneGlb());

        Assert.Equal(1, Invoke(
        [
            input, "-o", Path.Combine(temp.Path, "out"),
            "--preset", "object-review", "--camera-eye=0,0,0"
        ]));
    }

    [Fact]
    public void ProbeWithoutACamera_IsRejected()
    {
        Assert.False(GlbRenderCommand.TryCreateProbeRequest(
            probe: true, probeAt: null, pose: null, out var request, out var error));
        Assert.Null(request);
        Assert.Contains("--camera-eye", error);
    }

    [Fact]
    public void ProbeDefaultsToTheCentreOfTheFrame()
    {
        var pose = new ViewPose(System.Numerics.Vector3.Zero, 0f, 0f, 45f, 800, 600);

        Assert.True(GlbRenderCommand.TryCreateProbeRequest(
            probe: true, probeAt: null, pose, out var request, out var error));

        Assert.Null(error);
        Assert.Equal(new ProbeRequest(400, 300), request);
    }

    [Theory]
    [InlineData("725,450", 725, 450)]
    [InlineData(" 0 , 0 ", 0, 0)]
    public void ProbeAt_ReadsExplicitPixels(string text, int x, int y)
    {
        var pose = new ViewPose(System.Numerics.Vector3.Zero, 0f, 0f, 45f, 800, 600);

        Assert.True(GlbRenderCommand.TryCreateProbeRequest(
            probe: false, probeAt: text, pose, out var request, out _));

        Assert.Equal(new ProbeRequest(x, y), request);
    }

    [Theory]
    [InlineData("725")]
    [InlineData("725,450,1")]
    [InlineData("800,300")]
    [InlineData("-1,300")]
    public void ProbeAt_OutsideTheFrameOrMalformed_IsRejected(string text)
    {
        var pose = new ViewPose(System.Numerics.Vector3.Zero, 0f, 0f, 45f, 800, 600);

        Assert.False(GlbRenderCommand.TryCreateProbeRequest(
            probe: false, probeAt: text, pose, out _, out var error));
        Assert.NotNull(error);
    }

    private static int Invoke(string[] args)
    {
        var root = new RootCommand();
        root.Subcommands.Add(GlbRenderCommand.Create());
        return root.Parse(["glb-render", .. args]).Invoke();
    }

    private static byte[] BuildEmptySceneGlb()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{}]}");
        var paddedJsonLength = (json.Length + 3) & ~3;
        var data = new byte[12 + 8 + paddedJsonLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x46546C67); // glTF
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x4E4F534A); // JSON
        json.AsSpan().CopyTo(data.AsSpan(20));
        data.AsSpan(20 + json.Length, paddedJsonLength - json.Length).Fill(0x20);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-glb-camera-{Guid.NewGuid():N}");
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
