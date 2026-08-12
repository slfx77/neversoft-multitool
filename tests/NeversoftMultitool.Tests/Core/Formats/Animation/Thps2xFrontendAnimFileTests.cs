using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class Thps2XFrontendAnimFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)";

    [Fact]
    public void Parse_NestedForest_PreservesRawFieldsAndConsumesExactBytes()
    {
        var child = Node("child", "screen", rawUnknown32: 0x01020304);
        var root = Node(
            "root",
            "screen",
            rawUnknown32: 0xDEADBEEF,
            keys:
            [
                new SyntheticKey(
                    Enumerable.Range(1, 9).Select(static value => (float)value).ToArray(),
                    0xBEEF,
                    -2.5f)
            ],
            children: [child]);
        var secondRoot = Node("second", "other");
        var data = BuildFile(3.5f, root, secondRoot);

        var animation = Thps2XFrontendAnimFile.Parse(data);

        Assert.Equal(Thps2XFrontendAnimFile.SupportedVersion, animation.Version);
        Assert.Equal(3.5f, animation.Duration);
        Assert.Equal(data.Length, animation.SerializedSize);
        Assert.Equal(2, animation.Roots.Length);
        Assert.Equal(3, animation.NodeCount);
        Assert.Equal(1, animation.KeyCount);

        var parsedRoot = animation.Roots[0];
        Assert.Equal("root", parsedRoot.Name);
        Assert.Equal("screen", parsedRoot.ClosingName);
        Assert.Equal(0xDEADBEEFu, parsedRoot.RawUnknown32);
        Assert.Equal(Enumerable.Range(1, 12).Select(static value => (float)value), parsedRoot.BaseValues);
        var parsedKey = Assert.Single(parsedRoot.Keys);
        Assert.Equal(0xBEEF, parsedKey.RawUnknown16);
        Assert.Equal(-2.5f, parsedKey.TrailingValue);
        Assert.Equal(Enumerable.Range(1, 9).Select(static value => (float)value), parsedKey.Values);
        Assert.Equal("child", Assert.Single(parsedRoot.Children).Name);
        Assert.Empty(animation.Roots[1].Children);
    }

    [Fact]
    public void Parse_AnyTruncationOfValidNestedFile_IsRejected()
    {
        var data = BuildFile(1f,
            Node("root", "close", children: [Node("child", "close")]));

        for (var length = 0; length < data.Length; length++)
        {
            var truncated = data.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => Thps2XFrontendAnimFile.Parse(truncated));
        }
    }

    [Fact]
    public void Parse_TrailingBytesAreRejected()
    {
        var data = BuildFile(1f, Node("root", "close"));
        Array.Resize(ref data, data.Length + 1);

        var ex = Assert.Throws<InvalidDataException>(() => Thps2XFrontendAnimFile.Parse(data));
        Assert.Contains("file length", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NonFiniteDurationIsRejected()
    {
        var data = BuildFile(1f, Node("root", "close"));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x7FC00000);

        var ex = Assert.Throws<InvalidDataException>(() => Thps2XFrontendAnimFile.Parse(data));
        Assert.Contains("invalid duration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_KeyCountOverrunIsRejectedBeforeAllocation()
    {
        var data = BuildFile(1f, Node("root", "close"));
        const int keyCountOffset = Thps2XFrontendAnimFile.HeaderSize + 1 + 2 + 4 + 48;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(keyCountOffset), uint.MaxValue);

        var ex = Assert.Throws<InvalidDataException>(() => Thps2XFrontendAnimFile.Parse(data));
        Assert.Contains("key count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OversizedStringIsRejectedBeforeReadingPayload()
    {
        var data = BuildFile(1f, Node("root", "close"));
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(Thps2XFrontendAnimFile.HeaderSize + 1),
            Thps2XFrontendAnimFile.MaxStringBytes + 1);

        var ex = Assert.Throws<InvalidDataException>(() => Thps2XFrontendAnimFile.Parse(data));
        Assert.Contains("safety limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExcessiveNestingIsRejected()
    {
        var node = Node("leaf", "close");
        for (var i = 0; i < Thps2XFrontendAnimFile.MaxDepth; i++)
            node = Node($"node{i}", "close", children: [node]);

        var ex = Assert.Throws<InvalidDataException>(
            () => Thps2XFrontendAnimFile.Parse(BuildFile(1f, node)));
        Assert.Contains("nesting exceeds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExactlySixtyFourLevelsIsAccepted()
    {
        var node = Node("leaf", "close");
        for (var i = 1; i < Thps2XFrontendAnimFile.MaxDepth; i++)
            node = Node($"node{i}", "close", children: [node]);

        var animation = Thps2XFrontendAnimFile.Parse(BuildFile(1f, node));

        Assert.Equal(Thps2XFrontendAnimFile.MaxDepth, animation.NodeCount);
    }

    [Fact]
    public void JsonExporter_IsDeterministicAndKeepsUncertainValuesRaw()
    {
        var data = BuildFile(2f,
            Node("root", "screen", rawUnknown32: 0xDEADBEEF,
                keys: [new SyntheticKey(new float[9], 0xBEEF, 1.25f)]));
        var animation = Thps2XFrontendAnimFile.Parse(data);

        var first = Thps2XFrontendAnimJsonExporter.Serialize("TitleBG.ANIM", animation);
        var second = Thps2XFrontendAnimJsonExporter.Serialize("TitleBG.ANIM", animation);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(Thps2XFrontendAnimJsonExporter.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal(Thps2XFrontendAnimJsonExporter.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("TitleBG.ANIM", root.GetProperty("source").GetString());
        Assert.Equal(data.Length, root.GetProperty("serializedSize").GetInt32());
        var node = root.GetProperty("roots")[0];
        Assert.Equal(0xDEADBEEFu, node.GetProperty("rawUnknown32").GetUInt32());
        var key = node.GetProperty("keys")[0];
        Assert.Equal(0xBEEF, key.GetProperty("rawUnknown16").GetUInt16());
        Assert.Equal(1.25f, key.GetProperty("trailingValue").GetSingle());
    }

    [Fact]
    public void CommandOutputPath_DuplicateBasenamesInSubdirectoriesRemainDistinct()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "NsMultitool_Thps2xAnim_" + Guid.NewGuid().ToString("N"));
        var inputRoot = Path.Combine(tempRoot, "input");
        var outputRoot = Path.Combine(tempRoot, "output");
        Directory.CreateDirectory(inputRoot);
        try
        {
            var first = Thps2XFrontendAnimCommand.GetOutputPath(
                inputRoot, Path.Combine(inputRoot, "left", "shared.ANIM"), outputRoot);
            var second = Thps2XFrontendAnimCommand.GetOutputPath(
                inputRoot, Path.Combine(inputRoot, "right", "shared.ANIM"), outputRoot);

            Assert.NotEqual(first, second);
            Assert.Equal(Path.Combine(outputRoot, "left", "shared.anim.json"), first);
            Assert.Equal(Path.Combine(outputRoot, "right", "shared.anim.json"), second);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CommandOutputPath_SingleFileIsFlatAndOutsideRootIsRejected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "NsMultitool_Thps2xAnim_" + Guid.NewGuid().ToString("N"));
        var inputRoot = Path.Combine(tempRoot, "input");
        var outputRoot = Path.Combine(tempRoot, "output");
        Directory.CreateDirectory(inputRoot);
        try
        {
            var singleFile = Path.Combine(tempRoot, "TitleBG.ANIM");
            Assert.Equal(
                Path.Combine(outputRoot, "TitleBG.anim.json"),
                Thps2XFrontendAnimCommand.GetOutputPath(singleFile, singleFile, outputRoot));

            var outside = Path.Combine(tempRoot, "outside", "escape.ANIM");
            var ex = Assert.Throws<InvalidOperationException>(
                () => Thps2XFrontendAnimCommand.GetOutputPath(inputRoot, outside, outputRoot));
            Assert.Contains("outside input root", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Parse_TitleBg_IdentifiesFourFrontendNodes()
    {
        var path = GetFrontendFile("TitleBG.ANIM");
        Assert.SkipWhen(path == null, "THPS2X frontend corpus is not available");

        var data = File.ReadAllBytes(path!);
        var animation = Thps2XFrontendAnimFile.Parse(data);

        Assert.Equal(346, data.Length);
        Assert.Equal(3.3333333f, animation.Duration);
        Assert.Equal(4, animation.Roots.Length);
        Assert.Equal(4, animation.NodeCount);
        Assert.Equal(0, animation.KeyCount);
        Assert.Equal(
            ["Title BG", "loading 00", "loading 01", "loading 02"],
            animation.Roots.Select(static node => node.Name).ToArray());
        Assert.All(animation.Roots,
            static node => Assert.Equal("Title Screen", node.ClosingName));
    }

    [Fact]
    public void Parse_CareerScroll_UsesOneRootWithThirteenNestedUiNodes()
    {
        var path = GetFrontendFile("CareerSelectFileMenuLeftScroll.ANIM");
        Assert.SkipWhen(path == null, "THPS2X frontend corpus is not available");

        var animation = Thps2XFrontendAnimFile.Parse(File.ReadAllBytes(path!));

        var root = Assert.Single(animation.Roots);
        Assert.Equal("career name 01", root.Name);
        Assert.Equal("Career Select a File 1", root.ClosingName);
        Assert.Equal(13, root.Children.Length);
        Assert.Equal(14, animation.NodeCount);
        Assert.Equal(7, animation.KeyCount);
        Assert.All(root.Children,
            static node => Assert.Equal("Career Select a File 1", node.ClosingName));
    }

    [CorpusFact]
    public void Parse_FullFrontendCorpus_ConsumesEveryFileAndPinsCensus()
    {
        var frontend = GetFrontendDirectory();
        Assert.SkipWhen(frontend == null, "THPS2X frontend corpus is not available");

        var files = Directory.EnumerateFiles(frontend!, "*", SearchOption.TopDirectoryOnly)
            .Where(static file => Path.GetExtension(file)
                .Equals(".anim", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(193, files.Length);

        var roots = 0;
        var nodes = 0;
        var keys = 0;
        var maximumDepth = 0;
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            Assert.True(Thps2XFrontendAnimFile.IsThps2XFrontendAnim(data), file);
            var animation = Thps2XFrontendAnimFile.Parse(data);
            Assert.Equal(data.Length, animation.SerializedSize);
            Assert.Equal(Thps2XFrontendAnimFile.SupportedVersion, animation.Version);

            roots += animation.Roots.Length;
            nodes += animation.NodeCount;
            keys += animation.KeyCount;
            foreach (var root in animation.Roots)
                Visit(root, 0, ref maximumDepth);
        }

        Assert.Equal(921, roots);
        Assert.Equal(1_148, nodes);
        Assert.Equal(4_581, keys);
        Assert.Equal(1, maximumDepth);
    }

    private string? GetFrontendFile(string fileName)
    {
        var directory = GetFrontendDirectory();
        if (directory == null) return null;
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) ? path : null;
    }

    private string? GetFrontendDirectory()
    {
        if (!paths.HasSampleBuilds) return null;
        var directory = Path.Combine(paths.SampleBuildsDir!, BuildName, "frontend");
        return Directory.Exists(directory) ? directory : null;
    }

    private static void Visit(Thps2XFrontendAnimNode node, int depth, ref int maximumDepth)
    {
        maximumDepth = Math.Max(maximumDepth, depth);
        Assert.Equal(12, node.BaseValues.Length);
        foreach (var key in node.Keys)
            Assert.Equal(9, key.Values.Length);
        foreach (var child in node.Children)
            Visit(child, depth + 1, ref maximumDepth);
    }

    private static SyntheticNode Node(
        string name,
        string closingName,
        uint rawUnknown32 = 0,
        SyntheticKey[]? keys = null,
        SyntheticNode[]? children = null)
    {
        return new SyntheticNode(
            name,
            Enumerable.Range(1, 12).Select(static value => (float)value).ToArray(),
            rawUnknown32,
            keys ?? [],
            children ?? [],
            closingName);
    }

    private static byte[] BuildFile(float duration, params SyntheticNode[] roots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("Anm\0"u8);
        writer.Write(Thps2XFrontendAnimFile.SupportedVersion);
        writer.Write(duration);
        writer.Write((uint)roots.Length);
        foreach (var root in roots)
            WriteNode(writer, root);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteNode(BinaryWriter writer, SyntheticNode node)
    {
        writer.Write(Thps2XFrontendAnimFile.NodeMarker);
        WriteString(writer, node.Name);
        Assert.Equal(12, node.BaseValues.Length);
        foreach (var value in node.BaseValues)
            writer.Write(value);
        writer.Write((uint)node.Keys.Length);
        writer.Write(node.RawUnknown32);
        foreach (var key in node.Keys)
        {
            Assert.Equal(9, key.Values.Length);
            foreach (var value in key.Values)
                writer.Write(value);
            writer.Write(key.RawUnknown16);
            writer.Write(key.TrailingValue);
        }

        foreach (var child in node.Children)
            WriteNode(writer, child);
        WriteString(writer, node.ClosingName);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private sealed record SyntheticNode(
        string Name,
        float[] BaseValues,
        uint RawUnknown32,
        SyntheticKey[] Keys,
        SyntheticNode[] Children,
        string ClosingName);

    private sealed record SyntheticKey(float[] Values, ushort RawUnknown16, float TrailingValue);
}
