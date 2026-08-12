using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

/// <summary>
///     THAW-generation sectioned QB files (PS2/PC little-endian "old" info encoding,
///     GC big-endian "new" encoding) parsed through the shared QbFile pipeline.
///     Fixtures are extracted from pristine PAK archives at test time so the tests
///     do not depend on pre-extracted Sample payloads.
/// </summary>
public class QbSectionParserTests(TestPaths paths)
{
    private static readonly byte[] SectionedQbSignature =
    [
        0x1C, 0x08, 0x02, 0x04, 0x10, 0x04, 0x08, 0x0C, 0x0C, 0x08,
        0x02, 0x04, 0x14, 0x02, 0x04, 0x0C, 0x10, 0x10, 0x0C, 0x00
    ];

    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    private static string ExtractPak(string pakPath, string tag)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"NsMultitool_Test_{tag}_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);
        return tempDir;
    }

    [Fact]
    public void Parse_OldStructQbKeyStringQs_UsesStructItemMeaning()
    {
        var qb = QbFile.Parse(BuildOldStruct(0x001A0400), "synthetic.qb.ps2");

        var value = Assert.Single(qb.Tokens, static token => token.Offset == 64);
        Assert.Equal(QbTokenType.Name, value.Type);
        Assert.Equal(0x33333333u, value.NameChecksum);
    }

    [Fact]
    public void Parse_OldStructQbKey_ControlRemainsName()
    {
        var qb = QbFile.Parse(BuildOldStruct(0x00001B00), "synthetic.qb.ps2");

        var value = Assert.Single(qb.Tokens, static token => token.Offset == 64);
        Assert.Equal(QbTokenType.Name, value.Type);
        Assert.Equal(0x33333333u, value.NameChecksum);
    }

    [Fact]
    public void Parse_OldTopLevelStringPointer_RetainsSectionMeaning()
    {
        var data = CreateOldSectionedQb(48);
        WriteUInt32(data, 28, 0x001A0400); // SectionStringPointer
        WriteUInt32(data, 32, 0x11111111); // section key
        WriteUInt32(data, 40, 0x33333333); // language-string pointer

        var qb = QbFile.Parse(data, "synthetic.qb.ps2");

        var value = Assert.Single(qb.Tokens, static token => token.Offset == 40);
        Assert.Equal(QbTokenType.HexInteger, value.Type);
        Assert.Equal(0x33333333u, value.HexValue);
    }

    private static byte[] BuildOldStruct(uint itemKind)
    {
        var data = CreateOldSectionedQb(72);
        WriteUInt32(data, 28, 0x000A0400); // SectionStruct
        WriteUInt32(data, 32, 0x11111111); // section key
        WriteUInt32(data, 40, 48); // section payload
        WriteUInt32(data, 48, 0x00010000); // StructHeader
        WriteUInt32(data, 52, 56); // first item
        WriteUInt32(data, 56, itemKind);
        WriteUInt32(data, 60, 0x22222222); // item key
        WriteUInt32(data, 64, 0x33333333); // item value
        return data; // next-item pointer at 68 remains zero
    }

    private static byte[] CreateOldSectionedQb(int size)
    {
        var data = new byte[size];
        WriteUInt32(data, 4, (uint)size);
        SectionedQbSignature.CopyTo(data, 8);
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    [Fact]
    public void Parse_CamPakInfo_Ps2AndGc_AgreeOnStructure()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Pak = paths.FindSampleFile(ThawPs2Build, "bh_11_main.pak.ps2");
        var gcPak = paths.FindSampleFile(ThawGcBuild, "bh_11_main.apk.ngc");
        Assert.SkipWhen(ps2Pak is null || gcPak is null, "bh_11_main archives not found");

        var ps2Dir = ExtractPak(ps2Pak!, "QbPs2");
        var gcDir = ExtractPak(gcPak!, "QbGc");
        try
        {
            var ps2File = Directory.GetFiles(ps2Dir, "bh_11_cam_pak_info.qb.ps2", SearchOption.AllDirectories).Single();
            var gcFile = Directory.GetFiles(gcDir, "bh_11_cam_pak_info.qb.ngc", SearchOption.AllDirectories).Single();

            var ps2 = QbFile.Parse(ps2File);
            var gc = QbFile.Parse(gcFile);

            // Same single array-of-structs global under the same key on both platforms
            // (QbKey("bh_11_cam_paks"); resolves via the THAW dbg.pak dictionary).
            Assert.Equal(1, ps2.GlobalCount);
            Assert.Equal(1, gc.GlobalCount);
            Assert.Equal(0x8A75C579u, ps2.Items.Single().NameChecksum);
            Assert.Equal(0x8A75C579u, gc.Items.Single().NameChecksum);

            var ps2Text = QbDecompiler.Decompile(ps2);
            var gcText = QbDecompiler.Decompile(gc);
            Assert.Contains(@"cutscenes\\bh_11\\ps2\\bh_11_cam0\\bh_11_cam0.pak", ps2Text);
            Assert.Contains(@"cutscenes\\bh_11\\ngc\\bh_11_cam0\\bh_11_cam0.pak", gcText);

            // The struct item keys and integer values match across endianness/encodings.
            Assert.Contains("length = 8", ps2Text);
            Assert.Contains("length = 8", gcText);
        }
        finally
        {
            Directory.Delete(ps2Dir, true);
            Directory.Delete(gcDir, true);
        }
    }

    [Fact]
    public void Parse_GcCutsceneScripts_DecompileWithLzssAndInlineStructs()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var gcPak = paths.FindSampleFile(ThawGcBuild, "bh_11_main.apk.ngc");
        Assert.SkipWhen(gcPak is null, "bh_11_main.apk.ngc not found");

        var gcDir = ExtractPak(gcPak!, "QbGcScripts");
        try
        {
            var file = Directory.GetFiles(gcDir, "bh_11_main_scripts.qb.ngc", SearchOption.AllDirectories).Single();
            var qb = QbFile.Parse(file);
            Assert.True(qb.ScriptCount >= 1, "expected at least the cutscene load script");
            Assert.True(qb.GlobalCount >= 1, "expected the cutscene asset manifest global");

            // The LZSS-compressed script body references the camera animation by path,
            // and the manifest global carries the skin model paths.
            var text = QbDecompiler.Decompile(qb);
            Assert.Contains("CAM_0.SKA", text);
            Assert.Contains("Cut_Dave_Head.skin", text);
        }
        finally
        {
            Directory.Delete(gcDir, true);
        }
    }

    [CorpusFact]
    public void Parse_ThawQbPak_AllScriptFilesDecompile()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var qbPak = paths.FindSampleFile(ThawPs2Build, "qb.pak.ps2");
        Assert.SkipWhen(qbPak is null, "qb.pak.ps2 not found");

        var dir = ExtractPak(qbPak!, "QbPakSweep");
        try
        {
            var files = Directory.GetFiles(dir, "*.qb.ps2", SearchOption.AllDirectories);
            Assert.True(files.Length > 200, $"expected the full script pak, got {files.Length} files");

            var failures = new List<string>();
            var scripts = 0;
            foreach (var file in files)
            {
                try
                {
                    var qb = QbFile.Parse(file);
                    scripts += qb.ScriptCount;
                    QbDecompiler.Decompile(qb);
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                $"{failures.Count}/{files.Length} failed:\n" + string.Join("\n", failures.Take(10)));
            Assert.True(scripts > 2000, $"expected the main game scripts, got {scripts}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Parse_KeyboardScript_FastBranchesProduceBalancedBlocks()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var qbPak = paths.FindSampleFile(ThawPs2Build, "qb.pak.ps2");
        Assert.SkipWhen(qbPak is null, "qb.pak.ps2 not found");

        var dir = ExtractPak(qbPak!, "QbKeyboard");
        try
        {
            var file = Directory.GetFiles(dir, "keyboard.qb.ps2", SearchOption.AllDirectories).Single();
            var text = QbDecompiler.Decompile(QbFile.Parse(file));

            // THAW fast-branch tokens (0x47/0x48/0x49) must decompile into balanced
            // if/else/endif and switch/case/endswitch blocks.
            Assert.Contains("if", text);
            Assert.Contains("else", text);
            Assert.Contains("endif", text);
            Assert.Contains("switch", text);
            Assert.Contains("endswitch", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
