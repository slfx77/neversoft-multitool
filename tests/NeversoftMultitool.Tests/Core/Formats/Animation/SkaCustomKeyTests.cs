using System.Buffers.Binary;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public class SkaCustomKeyTests
{
    private const uint FlagPlatform = 1u << 28;
    private const uint FlagUseCompressTable = 1u << 23;
    private const uint KnownScriptQbKey = 0xBDF7F843; // CreateFromStructure

    [Fact]
    public void ParseThaw_CustomKeys_DecodesLittleAndBigEndianMirrors()
    {
        var little = SkaFile.Parse(BuildFixture(false,
            new SyntheticKey(30, 1, FloatPayload(1.25f, false)),
            new SyntheticKey(90, 4, U32Payload(KnownScriptQbKey, false))));
        var big = SkaFile.Parse(BuildFixture(true,
            new SyntheticKey(30, 1, FloatPayload(1.25f, true)),
            new SyntheticKey(90, 4, U32Payload(KnownScriptQbKey, true))));

        Assert.Empty(little.BoneTracks);
        Assert.Empty(big.BoneTracks);
        Assert.Equal(2, little.CustomKeys.Length);
        Assert.Equal(2, big.CustomKeys.Length);

        AssertEquivalent(little.CustomKeys[0], big.CustomKeys[0]);
        Assert.Equal(30u, little.CustomKeys[0].Timestamp);
        Assert.Equal(1u, little.CustomKeys[0].Type);
        Assert.Equal("changeFocalLength", little.CustomKeys[0].Name);
        Assert.Equal(1.25f, little.CustomKeys[0].Fov);
        Assert.Null(little.CustomKeys[0].ScriptQbKey);

        AssertEquivalent(little.CustomKeys[1], big.CustomKeys[1]);
        Assert.Equal(90u, little.CustomKeys[1].Timestamp);
        Assert.Equal(4u, little.CustomKeys[1].Type);
        Assert.Equal("runScript", little.CustomKeys[1].Name);
        Assert.Null(little.CustomKeys[1].Fov);
        Assert.Equal(KnownScriptQbKey, little.CustomKeys[1].ScriptQbKey);
    }

    [Fact]
    public void ParseThaw_UnknownVariableSizeCustomKey_PreservesPayload()
    {
        byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        var anim = SkaFile.Parse(BuildFixture(false, new SyntheticKey(7, 0x1234, payload)));

        var key = Assert.Single(anim.CustomKeys);
        Assert.Equal(7u, key.Timestamp);
        Assert.Equal(0x1234u, key.Type);
        Assert.Equal("unknown", key.Name);
        Assert.Equal(20u, key.Size);
        Assert.Equal(payload, key.Payload);
        Assert.Null(key.Fov);
        Assert.Null(key.ScriptQbKey);

        using var document = JsonDocument.Parse(SkaCustomKeyJsonExporter.Serialize("unknown.ska", anim));
        var jsonKey = document.RootElement.GetProperty("keys")[0];
        Assert.Equal("unknown", jsonKey.GetProperty("name").GetString());
        Assert.Equal("1020304050607080", jsonKey.GetProperty("payloadHex").GetString());
    }

    [Fact]
    public void ParseThawCompressed_CustomKeyTailStartsAfterQAndTBlobs()
    {
        var anim = SkaFile.Parse(BuildCompressedFixture(false,
            new SyntheticKey(12, 4, U32Payload(KnownScriptQbKey, false))));

        var key = Assert.Single(anim.CustomKeys);
        Assert.True(anim.UsesCompressTable);
        Assert.Equal(12u, key.Timestamp);
        Assert.Equal(4u, key.Type);
        Assert.Equal(KnownScriptQbKey, key.ScriptQbKey);
    }

    [Fact]
    public void ParseThawHiRes_PerBoneCountsMustMatchHeaderTotals()
    {
        var data = BuildHiResCountMismatchFixture();

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("per-bone Q counts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThawCompressed_BlobLengthCannotWrapBeforeCustomTail()
    {
        var data = BuildCompressedFixture(false,
            new SyntheticKey(12, 4, U32Payload(KnownScriptQbKey, false)));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x28), uint.MaxValue);

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("Q size table totals", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThawCompressed_SizeTableMustAccountForDeclaredBlob()
    {
        var data = BuildCompressedSizeMismatchFixture();

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("Q size table totals", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8u, "smaller than")]
    [InlineData(15u, "not four-byte aligned")]
    [InlineData(20u, "exceeds file length")]
    public void ParseThaw_MalformedCustomKeySize_IsRejected(uint declaredSize, string expectedMessage)
    {
        var data = BuildFixture(false, new SyntheticKey(0, 0x1234, [1, 2, 3, 4]));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), declaredSize);

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThaw_KnownCustomKeyWithoutPayload_IsRejected()
    {
        var data = BuildFixture(false, new SyntheticKey(0, 1, [1, 2, 3, 4]));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30), 12);

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("must be a 16-byte record", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThaw_KnownCustomKeyWithOversizedPayload_IsRejected()
    {
        var data = BuildFixture(false,
            new SyntheticKey(0, 4, [1, 2, 3, 4, 5, 6, 7, 8]));

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("must be a 16-byte record", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThaw_CustomKeyCountLeavingTrailingRecordBytes_IsRejected()
    {
        var data = BuildFixture(false, new SyntheticKey(0, 0x1234, [1, 2, 3, 4]));
        Array.Resize(ref data, data.Length + 4);

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("custom keys end", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThaw_ZeroCustomKeyCountWithTrailingRecord_IsRejected()
    {
        var data = BuildFixture(false, new SyntheticKey(0, 0x1234, [1, 2, 3, 4]));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), 0);

        var ex = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        Assert.Contains("declares no custom keys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonExporter_UsesStableTypedCustomKeySchema()
    {
        var anim = SkaFile.Parse(BuildFixture(false,
            new SyntheticKey(30, 1, FloatPayload(1.25f, false)),
            new SyntheticKey(90, 4, U32Payload(KnownScriptQbKey, false))));

        using var document = JsonDocument.Parse(
            SkaCustomKeyJsonExporter.Serialize("camera.ska.ngc", anim));
        var root = document.RootElement;
        Assert.Equal(SkaCustomKeyJsonExporter.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal(SkaCustomKeyJsonExporter.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("camera.ska.ngc", root.GetProperty("source").GetString());
        Assert.Equal(2f, root.GetProperty("durationSeconds").GetSingle());

        var keys = root.GetProperty("keys");
        Assert.Equal(2, keys.GetArrayLength());
        Assert.Equal(30u, keys[0].GetProperty("timestamp").GetUInt32());
        Assert.False(keys[0].TryGetProperty("timeSeconds", out _));
        Assert.Equal(1u, keys[0].GetProperty("type").GetUInt32());
        Assert.Equal("changeFocalLength", keys[0].GetProperty("name").GetString());
        Assert.Equal(1.25f, keys[0].GetProperty("fov").GetSingle());
        Assert.False(keys[0].TryGetProperty("payloadHex", out _));

        Assert.Equal(4u, keys[1].GetProperty("type").GetUInt32());
        Assert.Equal("runScript", keys[1].GetProperty("name").GetString());
        Assert.Equal("0xBDF7F843", keys[1].GetProperty("scriptQbKey").GetString());
        Assert.Equal("CreateFromStructure", keys[1].GetProperty("scriptName").GetString());
        Assert.False(keys[1].TryGetProperty("payloadHex", out _));
    }

    [Theory]
    [InlineData("foo.ska", "foo.ska.json")]
    [InlineData("foo.ska.ps2", "foo.ska.json")]
    [InlineData("foo.ska.ngc", "foo.ska.json")]
    public void CliCustomKeyOutputName_NormalizesPlatformSuffix(string input, string expected)
    {
        Assert.Equal(expected, SkaCommand.GetCustomKeyOutputName(input));
    }

    [Fact]
    public void CliCustomKeyOutputPath_PreservesRelativeDirectoriesForDuplicateBasenames()
    {
        var inputRoot = Path.Combine(Path.GetTempPath(), "ska-input");
        var outputRoot = Path.Combine(Path.GetTempPath(), "ska-output");
        var firstInput = Path.Combine(inputRoot, "cutscenes", "one", "CAM_0.ska.ngc");
        var secondInput = Path.Combine(inputRoot, "cutscenes", "two", "CAM_0.ska.ngc");

        var first = SkaCommand.GetCustomKeyOutputPath(outputRoot, firstInput, inputRoot);
        var second = SkaCommand.GetCustomKeyOutputPath(outputRoot, secondInput, inputRoot);

        Assert.Equal(
            Path.Combine(outputRoot, "cutscenes", "one", "CAM_0.ska.json"),
            first);
        Assert.Equal(
            Path.Combine(outputRoot, "cutscenes", "two", "CAM_0.ska.json"),
            second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CliCustomKeyOutputPath_RejectsInputOutsideDirectoryRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ska-parent");
        var inputRoot = Path.Combine(parent, "input");
        var outsideInput = Path.Combine(parent, "outside", "CAM_0.ska.ngc");

        var ex = Assert.Throws<InvalidDataException>(() =>
            SkaCommand.GetCustomKeyOutputPath(Path.Combine(parent, "output"), outsideInput, inputRoot));
        Assert.Contains("outside directory root", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertEquivalent(SkaCustomKey expected, SkaCustomKey actual)
    {
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Fov, actual.Fov);
        Assert.Equal(expected.ScriptQbKey, actual.ScriptQbKey);
    }

    private static byte[] BuildFixture(bool bigEndian, params SyntheticKey[] keys)
    {
        var length = 0x28 + keys.Sum(static key => 12 + key.Payload.Length);
        var data = new byte[length];
        WriteHeader(data, bigEndian, FlagPlatform, keys.Length);
        WriteRecords(data, 0x28, bigEndian, keys);
        return data;
    }

    private static byte[] BuildCompressedFixture(bool bigEndian, params SyntheticKey[] keys)
    {
        var length = 0x30 + keys.Sum(static key => 12 + key.Payload.Length);
        var data = new byte[length];
        WriteHeader(data, bigEndian, FlagUseCompressTable, keys.Length);
        WriteU32(data, 0x28, 0, bigEndian); // Q blob size
        WriteU32(data, 0x2C, 0, bigEndian); // T blob size
        WriteRecords(data, 0x30, bigEndian, keys);
        return data;
    }

    private static byte[] BuildHiResCountMismatchFixture()
    {
        var key = new SyntheticKey(12, 4, U32Payload(KnownScriptQbKey, false));
        var data = new byte[0x2C + 16];
        WriteHeader(data, false, FlagPlatform, 1);
        data[0x0D] = 1;
        data[0x28] = 1; // Per-bone Q count; header still declares zero Q keys.
        WriteRecords(data, 0x2C, false, [key]);
        return data;
    }

    private static byte[] BuildCompressedSizeMismatchFixture()
    {
        var key = new SyntheticKey(12, 4, U32Payload(KnownScriptQbKey, false));
        var data = new byte[0x38 + 16];
        WriteHeader(data, false, FlagUseCompressTable, 1);
        data[0x0D] = 1;
        WriteU32(data, 0x28, 1, false); // Header allocates one Q byte.
        WriteU32(data, 0x2C, 0, false);
        // The sole Q/T size-table entries at 0x30/0x32 remain zero.
        WriteRecords(data, 0x38, false, [key]);
        return data;
    }

    private static void WriteHeader(byte[] data, bool bigEndian, uint flags, int customKeyCount)
    {
        WriteU32(data, 0, SkaThawParser.ThawVersion, bigEndian);
        WriteU32(data, 4, flags, bigEndian);
        WriteF32(data, 8, 2f, bigEndian);
        data[0x0D] = 0; // no Q/T tracks are needed to exercise the event tail
        WriteU16(data, 0x0E, 0, bigEndian);
        WriteU16(data, 0x10, 0, bigEndian);
        WriteU16(data, 0x12, checked((ushort)customKeyCount), bigEndian);
        data.AsSpan(0x14, 20).Fill(0xFF);
    }

    private static void WriteRecords(byte[] data, int offset, bool bigEndian, SyntheticKey[] keys)
    {
        foreach (var key in keys)
        {
            if ((key.Payload.Length & 3) != 0)
                throw new ArgumentException("Synthetic custom-key payload must be four-byte aligned", nameof(keys));

            WriteU32(data, offset, key.Timestamp, bigEndian);
            WriteU32(data, offset + 4, key.Type, bigEndian);
            WriteU32(data, offset + 8, checked((uint)(12 + key.Payload.Length)), bigEndian);
            key.Payload.CopyTo(data, offset + 12);
            offset += 12 + key.Payload.Length;
        }
    }

    private static byte[] FloatPayload(float value, bool bigEndian)
    {
        var payload = new byte[4];
        WriteF32(payload, 0, value, bigEndian);
        return payload;
    }

    private static byte[] U32Payload(uint value, bool bigEndian)
    {
        var payload = new byte[4];
        WriteU32(payload, 0, value, bigEndian);
        return payload;
    }

    private static void WriteU16(byte[] data, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteU32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteF32(byte[] data, int offset, float value, bool bigEndian)
    {
        WriteU32(data, offset, BitConverter.SingleToUInt32Bits(value), bigEndian);
    }

    private sealed record SyntheticKey(uint Timestamp, uint Type, byte[] Payload);
}
