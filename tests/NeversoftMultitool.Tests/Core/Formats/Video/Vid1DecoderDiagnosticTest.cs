using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;
using System.Diagnostics;
using System.Reflection;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

/// <summary>
///     Diagnostic probe — not a correctness test. Prints the first few
///     macroblock decode attempts so we can see exactly where the
///     first-pass decoder bails. Run with xunit test output visible.
/// </summary>
public class Vid1DecoderDiagnosticTest(TestPaths paths)
{
    private static readonly int[] IntraDcThresholdTable = [32, 13, 15, 17, 19, 21, 23, 1];

    private sealed record DiagnosticReaders(Vid1BitReader VlcReader, Vid1BitReader FlagReader, string Mode);

    private string? FindIntroVid()
    {
        var repoCandidate = Path.Combine(GetRepoRoot(), "TestOutput", "intro_only_src", "intro.vid");
        if (File.Exists(repoCandidate))
            return repoCandidate;

        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains("American Wasteland", StringComparison.OrdinalIgnoreCase)
                              && Path.GetFileName(d).Contains("GC", StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;

        var candidate = Path.Combine(buildDir, "movies", "vid", "intro.vid");
        return File.Exists(candidate) ? candidate : null;
    }

    private static byte[] BuildDefaultIntraMatrix() =>
    [
        8, 17, 18, 19, 21, 23, 25, 27,
        17, 18, 19, 21, 23, 25, 27, 28,
        20, 21, 22, 23, 24, 26, 28, 30,
        21, 22, 23, 24, 26, 28, 30, 32,
        22, 23, 24, 26, 28, 30, 32, 35,
        23, 24, 26, 28, 30, 32, 35, 38,
        25, 26, 28, 30, 32, 35, 38, 41,
        27, 28, 30, 32, 35, 38, 41, 45,
    ];

    private static byte[] BuildDefaultInterMatrix()
    {
        var matrix = new byte[64];
        Array.Fill(matrix, (byte)16);
        return matrix;
    }

    private static Vid1FrameContext BuildFrameContext(Vid1VideoFile file, Vid1VideoFrame frame)
    {
        var intra = BuildDefaultIntraMatrix();
        var inter = BuildDefaultInterMatrix();
        var context = new Vid1FrameContext(file.Width, file.Height, intra, inter);

        Array.Fill(context.OutputY, (byte)128);
        Array.Fill(context.OutputCb, (byte)128);
        Array.Fill(context.OutputCr, (byte)128);
        Array.Fill(context.ReferenceY, (byte)128);
        Array.Fill(context.ReferenceCb, (byte)128);
        Array.Fill(context.ReferenceCr, (byte)128);

        context.CurrentQuantizer = frame.Quantizer;
        context.ForwardFCode = Math.Max(frame.ForwardCode ?? 1, 1);
        context.GmcEnabled = frame.PreambleClass == 3;
        context.SubpixelRoundingBias = frame.HasSpecialCallerGate ? 1 : 0;
        context.IntraDcThreshold = IntraDcThresholdTable[Math.Clamp(frame.IntraDcThresholdIndex, 0, 7)];
        context.UseIntraDequant = false;
        context.IntraMatrix = intra;
        context.InterMatrix = inter;
        context.ClearMbState();
        Vid1SpriteWarp.ApplyFrame(context, frame);
        return context;
    }

    private static string FormatNonZero(ReadOnlySpan<short> values)
    {
        var parts = new List<string>();
        for (var i = 0; i < values.Length && parts.Count < 16; i++)
        {
            if (values[i] != 0)
                parts.Add($"{i}:{values[i]}");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "(all zero)";
    }

    private static string FormatFirstRow(ReadOnlySpan<short> values)
    {
        return string.Join(",", values.Slice(0, Math.Min(8, values.Length)).ToArray());
    }

    private static string FormatFirstPixels(ReadOnlySpan<byte> values)
    {
        return string.Join(",", values.Slice(0, Math.Min(8, values.Length)).ToArray());
    }

    private static byte[] ReadFramePayload(string path, int frameIndex)
    {
        var data = File.ReadAllBytes(path);
        var root = ReadChunk(data, 0);
        var head = ReadChunk(data, root.EndOffset);
        var offset = head.EndOffset;
        var index = 0;

        while (offset + 8 <= data.Length)
        {
            var isZero = true;
            for (var i = offset; i < Math.Min(offset + 8, data.Length); i++)
            {
                if (data[i] != 0)
                {
                    isZero = false;
                    break;
                }
            }

            if (isZero)
                break;

            var frameChunk = ReadChunk(data, offset);
            if (frameChunk.Tag != "FRAM")
                break;

            var childOffset = frameChunk.Offset + 0x20;
            while (childOffset + 8 <= frameChunk.EndOffset)
            {
                var child = ReadChunk(data, childOffset);
                childOffset = child.EndOffset;
                if (child.Tag != "VIDD")
                    continue;

                if (index == frameIndex)
                {
                    var payload = new byte[child.EndOffset - (child.Offset + 8)];
                    Buffer.BlockCopy(data, child.Offset + 8, payload, 0, payload.Length);
                    return payload;
                }

                index++;
            }

            offset = frameChunk.EndOffset;
        }

        throw new InvalidOperationException($"Frame payload {frameIndex} not found in {path}");
    }

    private static (string Tag, int Offset, int EndOffset) ReadChunk(byte[] data, int offset)
    {
        var tag = System.Text.Encoding.ASCII.GetString(data, offset, 4);
        var size =
            (data[offset + 4] << 24) |
            (data[offset + 5] << 16) |
            (data[offset + 6] << 8) |
            data[offset + 7];
        return (tag, offset, offset + size);
    }

    private static void SkipMatrix(Vid1BitReader reader)
    {
        while (reader.ReadBits(8) != 0)
        {
        }
    }

    private static Vid1ControlProbe ProbeControl(Vid1VideoFrame frame, Vid1BitReader vlcReader, Vid1BitReader flagReader, int currentQuantizer)
    {
        return frame.PreambleClass switch
        {
            0 => Vid1ControlPrefix.Probe998F8(vlcReader, flagReader, currentQuantizer),
            1 => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, currentQuantizer, callerCr4: 0, gmcEnabled: false),
            3 => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, currentQuantizer, callerCr4: 1, gmcEnabled: (frame.SpritePointCount ?? 0) > 0),
            _ => Vid1ControlPrefix.Probe99A38(vlcReader, flagReader, currentQuantizer, callerCr4: 0, gmcEnabled: false),
        };
    }

    private static string GetDiagnosticOutputDir()
    {
        var outputDir = Path.Combine(
            Path.GetDirectoryName(typeof(Vid1DecoderDiagnosticTest).Assembly.Location)!,
            "Vid1Diagnostic");
        Directory.CreateDirectory(outputDir);
        return outputDir;
    }

    private static DiagnosticReaders CreateDiagnosticReaders(Vid1VideoFrame frame)
    {
        var readerMode = Environment.GetEnvironmentVariable("VID1_DIAG_READER_MODE");
        if (string.IsNullOrWhiteSpace(readerMode))
            readerMode = Environment.GetEnvironmentVariable("VID1_READER_MODE");
        readerMode = string.IsNullOrWhiteSpace(readerMode) ? "" : readerMode.Trim();

        var legacyHeaderPayload = frame.Bitstream.Length > 8
            ? frame.Bitstream.AsSpan(8).ToArray()
            : frame.CodedPayload;

        var vlcReader = readerMode switch
        {
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" or "header-split" => new Vid1BitReader(legacyHeaderPayload),
            _ => new Vid1BitReader(frame.CodedPayload),
        };
        var flagReader = readerMode switch
        {
            "" => vlcReader,
            "coded-flags" => new Vid1BitReader(frame.CodedPayload),
            "shared-coded" => vlcReader,
            "bitstream" => new Vid1BitReader(frame.Bitstream),
            "header" or "legacy-header" => new Vid1BitReader(legacyHeaderPayload),
            _ => new Vid1BitReader(frame.Bitstream),
        };

        if (frame.FlagBitOffset > 0)
            flagReader.SkipBits(frame.FlagBitOffset);

        return new DiagnosticReaders(vlcReader, flagReader, string.IsNullOrEmpty(readerMode) ? "default" : readerMode);
    }

    private static DiagnosticReaders CreateDiagnosticReadersAtBits(
        Vid1VideoFrame frame,
        int vlcBitPosition,
        int flagBitPosition)
    {
        var readers = CreateDiagnosticReaders(frame);
        readers.VlcReader.SetBitPosition(vlcBitPosition);
        readers.FlagReader.SetBitPosition(flagBitPosition);
        return readers;
    }

    private static Vid1BitReader CreateFlagReader(Vid1VideoFrame frame) => CreateDiagnosticReaders(frame).FlagReader;

    private static Vid1BitReader CreateFlagReaderAtBit(Vid1VideoFrame frame, int bitPosition)
    {
        var reader = CreateFlagReader(frame);
        reader.SetBitPosition(bitPosition);
        return reader;
    }

    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "NeversoftMultitool.slnx")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent == null)
                break;

            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static (string? Summary, string StdOut, string StdErr)? TryRunPythonGroundTruth(
        string path,
        int frameIndex,
        int bitOffset,
        string bundleName,
        string scanName,
        int initialIndex,
        int dcValue)
    {
        try
        {
            var startInfo = new ProcessStartInfo("python")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = GetRepoRoot(),
            };
            startInfo.ArgumentList.Add("tools/diagnostics/dump_vid1_coeffs.py");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("--frame");
            startInfo.ArgumentList.Add(frameIndex.ToString());
            startInfo.ArgumentList.Add("--offsets");
            startInfo.ArgumentList.Add(bitOffset.ToString());
            startInfo.ArgumentList.Add("--bundle");
            startInfo.ArgumentList.Add(bundleName);
            startInfo.ArgumentList.Add("--scan");
            startInfo.ArgumentList.Add(scanName);
            startInfo.ArgumentList.Add("--initial-index");
            startInfo.ArgumentList.Add(initialIndex.ToString());
            startInfo.ArgumentList.Add("--dc-value");
            startInfo.ArgumentList.Add(dcValue.ToString());

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var summaryLine = stdOut
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault(static line => line.StartsWith("coeffs:", StringComparison.Ordinal));

            var summary = summaryLine == null
                ? null
                : summaryLine["coeffs:".Length..].Trim();

            return (summary, stdOut, stdErr);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AdvanceMacroblockBitsOnly(
        Vid1VideoFrame frame,
        Vid1BitReader vlcReader,
        Vid1BitReader flagReader,
        ref int currentQuantizer)
    {
        var probe = ProbeControl(frame, vlcReader, flagReader, currentQuantizer);
        currentQuantizer = probe.Quantizer;

        if (probe.Stage != Vid1ControlStage.A878)
            return;

        var cbp = probe.ControlWord & 0x3F;
        var dcThreshold = IntraDcThresholdTable[Math.Clamp(frame.IntraDcThresholdIndex, 0, 7)];
        var dcPreDecode = probe.Quantizer < dcThreshold;

        for (var block = 0; block < 6; block++)
        {
            if (dcPreDecode)
            {
                var isLuma = block < 4;
                var dcSize = Vid1IntraDc.DecodeSize(vlcReader, isLuma);
                if (dcSize != 0)
                    _ = Vid1IntraDc.DecodeValue(vlcReader, dcSize);
                if (dcSize > 8)
                    flagReader.SkipBits(1);
            }

            var isCoded = (cbp & (1 << (5 - block))) != 0;
            if (!isCoded)
                continue;

            Span<short> quant = stackalloc short[64];
            Vid1CoefficientDecoder.DecodeBlock(
                vlcReader,
                useBundleB: true,
                Vid1CoefficientDecoder.GetScanTable("zigzag"),
                quant,
                startIndex: dcPreDecode ? 1 : 0);
        }
    }

    private static string ProbeResidualLength(
        byte[] codedPayload,
        int acStartBit,
        int startIndex,
        bool useBundleB)
    {
        var reader = new Vid1BitReader(codedPayload);
        reader.SkipBits(acStartBit);

        try
        {
            Span<short> quant = stackalloc short[64];
            Vid1CoefficientDecoder.DecodeBlock(
                reader,
                useBundleB,
                Vid1CoefficientDecoder.GetScanTable("zigzag"),
                quant,
                startIndex);
            return $"{reader.BitPosition - acStartBit}";
        }
        catch (Exception ex)
        {
            return $"ERR:{ex.GetType().Name}";
        }
    }

    [Fact]
    public void Diagnostic_Frame0_FirstMacroblock()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[0];

        var log = new List<string>();
        log.Add($"Frame 0: tag16=0x{frame.Tag16:X4} preamble_class={frame.PreambleClass}");
        log.Add($"  quantizer={frame.Quantizer} forward_code={frame.ForwardCode} has_special_gate={frame.HasSpecialCallerGate}");
        log.Add($"  coded_payload={frame.CodedPayload.Length} bytes, intra_dc_threshold={frame.IntraDcThresholdIndex}");
        log.Add($"  state_word=0x{frame.CurrentFrameStateWord:X8} alt_state=0x{frame.AlternateFrameStateWord:X8}");
        log.Add($"  custom_matrices={frame.UsesCustomQuantMatrices}");

        // Probe the first macroblock by hand
        var reader = new Vid1BitReader(frame.CodedPayload);
        log.Add($"\nStarting bit position: {reader.BitPosition}");
        log.Add("First 8 bytes of coded_payload (hex, then binary):");
        var firstBytes = frame.CodedPayload.AsSpan(0, Math.Min(8, frame.CodedPayload.Length)).ToArray();
        log.Add("  hex:    " + string.Join(" ", firstBytes.Select(b => $"{b:X2}")));
        log.Add("  binary: " + string.Join(" ", firstBytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))));

        var currentQuantizer = frame.Quantizer;
        int mbIdx = 0;
        try
        {
            for (; mbIdx < 10; mbIdx++)
            {
                var probeStartBit = reader.BitPosition;
                var probe = ProbeControl(frame, reader, reader, currentQuantizer);

                log.Add($"MB {mbIdx}: bit_range={probeStartBit}..{reader.BitPosition}  stage={probe.Stage} mb_type={probe.MacroblockType} cp={probe.ControlPrefix} sel={probe.Selector} cw=0x{probe.ControlWord:X2} cbp=0x{probe.ControlWord & 0x3F:X2} feat={probe.FeatureBit} q={probe.Quantizer}");
                currentQuantizer = probe.Quantizer;

                // Try decoding per-block (DC pre-decode + AC VLC) for all 6 blocks.
                // Hypothesis: no GMC pre-bit (+0x84 == 0 for this frame), DC pre-decode runs.
                if (probe.Stage == Vid1ControlStage.A878)
                {
                    var cbp = probe.ControlWord & 0x3F;
                    var blockOk = 0;
                    try
                    {
                        // Hypothesis: no pre-GMC bit. DC pre-decode + bundle A for AC.
                        // This matches FUN_8029A878's structure when +0x84 == 0 for this frame.
                        for (var block = 0; block < 6; block++)
                        {
                            var isLuma = block < 4;
                            var savedBefore = reader.BitPosition;
                            var dcSize = Vid1IntraDc.DecodeSize(reader, isLuma);
                            var dcValue = Vid1IntraDc.DecodeValue(reader, dcSize);
                            if (dcSize > 8) reader.SkipBits(1);
                            var coded = (cbp & (1 << (5 - block))) != 0;
                            log.Add($"    block {block}: DC @{savedBefore} size={dcSize} val={dcValue} coded={coded} AC@{reader.BitPosition}");
                            if (coded)
                            {
                                var acStart = reader.BitPosition;
                                var peek12 = reader.PeekBits(12);
                                log.Add($"      AC peek12 = 0x{peek12:X3}");

                                // Try bundle B with startIndex=1:
                                var attemptB = new Vid1BitReader(frame.CodedPayload);
                                attemptB.SkipBits(acStart);
                                var quantB = new short[64];
                                quantB[0] = (short)dcValue;
                                try
                                {
                                    Vid1CoefficientDecoder.DecodeBlock(
                                        attemptB, useBundleB: true,
                                        Vid1CoefficientDecoder.GetScanTable("zigzag"),
                                        quantB, startIndex: 1);
                                    log.Add($"      bundle B+start1 OK @{attemptB.BitPosition}, coeffs[0..3]: {quantB[0]},{quantB[1]},{quantB[2]},{quantB[3]}");
                                    reader.SkipBits(attemptB.BitPosition - acStart);
                                    continue;
                                }
                                catch (InvalidDataException bexB)
                                {
                                    log.Add($"      bundle B+start1 FAIL: {bexB.Message}");
                                }

                                // Try bundle A with startIndex=1:
                                var attemptA = new Vid1BitReader(frame.CodedPayload);
                                attemptA.SkipBits(acStart);
                                var quantA = new short[64];
                                quantA[0] = (short)dcValue;
                                try
                                {
                                    Vid1CoefficientDecoder.DecodeBlock(
                                        attemptA, useBundleB: false,
                                        Vid1CoefficientDecoder.GetScanTable("zigzag"),
                                        quantA, startIndex: 1);
                                    log.Add($"      bundle A+start1 OK @{attemptA.BitPosition}, coeffs[0..3]: {quantA[0]},{quantA[1]},{quantA[2]},{quantA[3]}");
                                    reader.SkipBits(attemptA.BitPosition - acStart);
                                    continue;
                                }
                                catch (InvalidDataException bexA)
                                {
                                    log.Add($"      bundle A+start1 FAIL: {bexA.Message}");
                                    break;
                                }
                            }
                            blockOk = block + 1;
                        }
                    }
                    catch (Exception bex)
                    {
                        log.Add($"    block {blockOk} decode failed: {bex.GetType().Name}: {bex.Message}");
                    }
                    break; // stop after first MB fully inspected
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"  FAILED at MB {mbIdx}: {ex.GetType().Name}: {ex.Message}");
        }

        // Print everything so we can see it in test output
        var message = string.Join("\n", log);
        Console.WriteLine(message);

        // Also write to a known path so we can cat it
        var reportPath = Path.Combine(GetDiagnosticOutputDir(), "frame0_mb0.txt");
        File.WriteAllText(reportPath, message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame0_FirstCodedBlock_PixelPipeline()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[0];
        var log = new List<string>();
        log.Add("=== Frame 0 first coded luma block-0 pixel pipeline ===");
        log.Add($"frame0: tag16=0x{frame.Tag16:X4} preamble_class={frame.PreambleClass} quantizer={frame.Quantizer} has_special_gate={frame.HasSpecialCallerGate}");

        var context = BuildFrameContext(file, frame);
        var readers = CreateDiagnosticReaders(frame);
        var vlcReader = readers.VlcReader;
        var flagReader = readers.FlagReader;
        log.Add($"reader_mode={readers.Mode}");

        Vid1ControlProbe control = default;
        var targetMbIndex = -1;
        var targetMbX = 0;
        var targetMbY = 0;
        var totalMacroblocks = context.MbCols * context.MbRows;

        try
        {
            for (var mbIdx = 0; mbIdx < totalMacroblocks; mbIdx++)
            {
                var mbX = mbIdx % context.MbCols;
                var mbY = mbIdx / context.MbCols;
                var mbVlcStart = vlcReader.BitPosition;
                var mbFlagStart = flagReader.BitPosition;
                control = ProbeControl(frame, vlcReader, flagReader, context.CurrentQuantizer);
                var cbpProbe = control.ControlWord & 0x3F;
                var hasCodedBlock0 = control.Stage == Vid1ControlStage.A878 && (cbpProbe & (1 << 5)) != 0;

                if (hasCodedBlock0)
                {
                    targetMbIndex = mbIdx;
                    targetMbX = mbX;
                    targetMbY = mbY;
                    log.Add(
                        $"target MB {mbIdx} ({mbX},{mbY}): vlc@{mbVlcStart}->{vlcReader.BitPosition} " +
                        $"flag@{mbFlagStart}->{flagReader.BitPosition}");
                    break;
                }

                Vid1MacroblockDecoder.Decode(vlcReader, flagReader, control, context, mbX, mbY);
            }
        }
        catch (Exception ex)
        {
            log.Add($"abort while searching target: {ex.GetType().Name}: {ex.Message} vlc={vlcReader.BitPosition} flag={flagReader.BitPosition}");
        }

        if (targetMbIndex < 0)
        {
            log.Add("abort: no coded A878 block-0 found before the first decode failure");
            var earlyMessage = string.Join("\n", log);
            Console.WriteLine(earlyMessage);
            File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame0_firstcoded_pipeline.txt"), earlyMessage);
            Assert.NotEmpty(log);
            return;
        }

        log.Add($"control: stage={control.Stage} mb_type={control.MacroblockType} cp=0x{control.ControlPrefix:X} sel={control.Selector} cw=0x{control.ControlWord:X2} feat={control.FeatureBit} q={control.Quantizer}");
        log.Add($"reader positions after control: vlc={vlcReader.BitPosition} flag={flagReader.BitPosition}");

        var cbp = control.ControlWord & 0x3F;
        var dcThreshold = IntraDcThresholdTable[Math.Clamp(frame.IntraDcThresholdIndex, 0, 7)];
        var dcPreDecode = control.Quantizer < dcThreshold;
        var targetBlock = 0;
        var predictionMode = Environment.GetEnvironmentVariable("VID1_A878_PREDICT")?.ToLowerInvariant() ?? "none";
        var scanMode = Environment.GetEnvironmentVariable("VID1_A878_SCAN_MODE")?.ToLowerInvariant() ?? "auto";
        var usePrediction = predictionMode is "all" or "luma";
        var scanTableIndex = 0;
        Span<short> predictions = stackalloc short[8];
        if (usePrediction)
        {
            scanTableIndex = Vid1Prediction.ComputePredictions(
                context,
                targetMbX,
                targetMbY,
                targetBlock,
                control.Quantizer,
                Vid1MacroblockDecoder.ComputeDcScale(control.Quantizer, isLuma: true),
                predictions);
            if (control.FeatureBit == 0)
            {
                Vid1Prediction.ForceZigzagScan(context, targetMbX, targetMbY, targetBlock);
                scanTableIndex = 0;
            }

            scanTableIndex = scanMode switch
            {
                "horizontal" => 1,
                "vertical" => 2,
                "zigzag" => 0,
                _ => scanTableIndex,
            };
        }

        var scanName = scanTableIndex switch
        {
            1 => "horizontal",
            2 => "vertical",
            _ => "zigzag",
        };
        var bundleName = "B";

        Span<short> quant = stackalloc short[64];
        var startIndex = 0;
        var dcSize = 0;
        var dcValue = 0;
        var dcStartBit = vlcReader.BitPosition;
        if (dcPreDecode)
        {
            dcSize = Vid1IntraDc.DecodeSize(vlcReader, isLuma: true);
            dcValue = dcSize == 0 ? 0 : Vid1IntraDc.DecodeValue(vlcReader, dcSize);
            if (dcSize > 8)
                flagReader.SkipBits(1);

            quant[0] = (short)dcValue;
            startIndex = 1;
        }

        var residualStartBit = vlcReader.BitPosition;
        Vid1CoefficientDecoder.DecodeBlock(
            vlcReader,
            useBundleB: true,
            Vid1CoefficientDecoder.GetScanTable(scanName),
            quant,
            startIndex);
        var residualEndBit = vlcReader.BitPosition;

        if (usePrediction)
        {
            Vid1Prediction.ApplyAndStorePredictions(
                context,
                targetMbX,
                targetMbY,
                targetBlock,
                Vid1MacroblockDecoder.ComputeDcScale(control.Quantizer, isLuma: true),
                predictions,
                quant);
        }

        Span<short> dequant = stackalloc short[64];
        var dcScale = Vid1MacroblockDecoder.ComputeDcScale(control.Quantizer, isLuma: targetBlock < 4);
        var defaultIntraMatrix = BuildDefaultIntraMatrix();
        if (frame.UsesCustomQuantMatrices)
            Vid1Dequant.DequantIntra(dequant, quant, control.Quantizer, dcScale, defaultIntraMatrix);
        else
            Vid1Dequant.DequantInter(dequant, quant, control.Quantizer, dcScale);

        var idctBlock = dequant.ToArray();
        Vid1Idct.Transform(idctBlock);

        var outputPlane = new byte[64];
        var referencePlane = new byte[64];
        Array.Fill(referencePlane, (byte)128);

        var writePath = control.Stage == Vid1ControlStage.A878 ? "intra" : "inter";
        if (writePath == "intra")
        {
            var configuredOffset = Environment.GetEnvironmentVariable("VID1_A878_WRITE_OFFSET");
            var intraWriteOffset = int.TryParse(configuredOffset, out var parsedOffset) ? parsedOffset : 0;
            Vid1MotionComp.WriteIntraBlock(idctBlock, outputPlane, 8, 0, 0, intraWriteOffset);
        }
        else
        {
            Vid1MotionComp.PredictInterBlock(
                referencePlane, 8, 8, 8,
                srcX: 0, srcY: 0, halfX: 0, halfY: 0,
                idctBlock,
                outputPlane, 8,
                dstX: 0, dstY: 0);
        }

        log.Add($"block{targetBlock}: dc_threshold={dcThreshold} dc_predecode={dcPreDecode} coded={(cbp & (1 << (5 - targetBlock))) != 0} bundle={bundleName} scan={scanName} startIndex={startIndex}");
        log.Add($"prediction: mode={predictionMode} enabled={usePrediction} preds={FormatFirstRow(predictions)}");
        log.Add($"dc: start={dcStartBit} size={dcSize} value={dcValue}");
        log.Add($"residual: start={residualStartBit} end={residualEndBit} bits={residualEndBit - residualStartBit}");
        log.Add($"raw_coeffs: {FormatNonZero(quant)}");
        log.Add($"dequant_coeffs: {FormatNonZero(dequant)}");
        log.Add($"dequant_row0: {FormatFirstRow(dequant)}");
        log.Add($"idct_range: {idctBlock.Min()}..{idctBlock.Max()}");
        log.Add($"idct_row0: {FormatFirstRow(idctBlock)}");
        log.Add($"write_path: {writePath}");
        log.Add($"output_row0: {FormatFirstPixels(outputPlane)}");

        var groundTruth = TryRunPythonGroundTruth(path, frame.Index, residualStartBit, bundleName, scanName, startIndex, dcValue);
        if (groundTruth is { } python)
        {
            log.Add($"python_raw_coeffs: {python.Summary ?? "<missing coeffs line>"}");
            if (python.Summary != null)
                log.Add($"raw_coeff_match: {string.Equals(FormatNonZero(quant), python.Summary, StringComparison.Ordinal)}");
            if (python.Summary == null)
                log.Add("python_stdout_snippet: " + python.StdOut.Replace("\r", " ").Replace("\n", " ").Trim());
            if (!string.IsNullOrWhiteSpace(python.StdErr))
                log.Add("python_stderr: " + python.StdErr.Replace("\r", " ").Replace("\n", " ").Trim());
        }
        else
        {
            log.Add("python_ground_truth: unavailable");
        }

        var message = string.Join("\n", log);
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame0_firstcoded_pipeline.txt"), message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame0_TwoReaderSplit()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[0];

        var log = new List<string>();
        log.Add("=== Dual-reader diagnostic ===");
        log.Add($"CodedPayload length: {frame.CodedPayload.Length} bytes");
        log.Add($"Bitstream length: {frame.Bitstream.Length} bytes");
        log.Add($"FlagBitOffset: {frame.FlagBitOffset} bits");
        log.Add("Bitstream start reflects the decomp-backed flag window: VIDD payload + 0x04 through the 8-byte trailer.");

        log.Add($"First 8 bytes of CodedPayload (hex):");
        var cpFirst = frame.CodedPayload.AsSpan(0, Math.Min(8, frame.CodedPayload.Length)).ToArray();
        log.Add("  " + string.Join(" ", cpFirst.Select(b => $"{b:X2}")));
        log.Add($"First 8 bytes of Bitstream (hex):");
        var flagFirst = frame.Bitstream.AsSpan(0, Math.Min(8, frame.Bitstream.Length)).ToArray();
        log.Add("  " + string.Join(" ", flagFirst.Select(b => $"{b:X2}")));

        var readers = CreateDiagnosticReaders(frame);
        var vlcReader = readers.VlcReader;
        var flagReader = readers.FlagReader;
        log.Add($"reader_mode={readers.Mode}");

        var currentQuantizer = frame.Quantizer;
        try
        {
            for (var mbIdx = 0; mbIdx < 5; mbIdx++)
            {
                var flagPos = flagReader.BitPosition;
                var vlcPos = vlcReader.BitPosition;

                var probe = ProbeControl(frame, vlcReader, flagReader, currentQuantizer);

                log.Add($"\nMB {mbIdx}: flag@{flagPos}→{flagReader.BitPosition} vlc@{vlcPos}→{vlcReader.BitPosition}");
                log.Add($"  stage={probe.Stage} mb_type={probe.MacroblockType} cp={probe.ControlPrefix} sel={probe.Selector} cw=0x{probe.ControlWord:X2} feat={probe.FeatureBit} q={probe.Quantizer}");
                currentQuantizer = probe.Quantizer;

                if (probe.Stage == Vid1ControlStage.A878)
                {
                    var cbp = probe.ControlWord & 0x3F;
                    for (var block = 0; block < 6; block++)
                    {
                        var isLuma = block < 4;
                        var dcVlcPos = vlcReader.BitPosition;
                        var dcSize = Vid1IntraDc.DecodeSize(vlcReader, isLuma);
                        var dcValue = Vid1IntraDc.DecodeValue(vlcReader, dcSize);
                        if (dcSize > 8)
                            flagReader.SkipBits(1);
                        var coded = (cbp & (1 << (5 - block))) != 0;
                        log.Add($"  block {block}: DC vlc@{dcVlcPos}→{vlcReader.BitPosition} size={dcSize} val={dcValue} coded={coded}");
                        if (coded)
                        {
                            var acStart = vlcReader.BitPosition;
                            var peek12 = vlcReader.PeekBits(12);
                            log.Add($"    AC peek12=0x{peek12:X3} vlc@{acStart}");

                            var quantBuf = new short[64];
                            quantBuf[0] = (short)dcValue;
                            try
                            {
                                Vid1CoefficientDecoder.DecodeBlock(
                                    vlcReader, useBundleB: true,
                                    Vid1CoefficientDecoder.GetScanTable("zigzag"),
                                    quantBuf, startIndex: 1);
                                log.Add($"    AC OK vlc@{vlcReader.BitPosition}, coeffs[0..5]: {string.Join(",", quantBuf.Take(6))}");
                            }
                            catch (Exception ex)
                            {
                                log.Add($"    AC FAIL: {ex.GetType().Name}: {ex.Message}");
                                goto done;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"  EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        done:
        var message = string.Join("\n", log);
        Console.WriteLine(message);

        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame0_two_reader.txt"), message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame0_FirstFailureResyncSearch()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[0];
        var log = new List<string>();
        log.Add("=== Frame 0 first-failure resync search ===");

        var readers = CreateDiagnosticReaders(frame);
        var vlcReader = readers.VlcReader;
        var flagReader = readers.FlagReader;
        log.Add($"reader_mode={readers.Mode}");
        var currentQuantizer = frame.Quantizer;

        var failMbIndex = -1;
        var failVlcStart = 0;
        var failFlagStart = 0;
        var failQuantizer = currentQuantizer;
        string failMessage;

        try
        {
            for (var mbIdx = 0; mbIdx < 80; mbIdx++)
            {
                failMbIndex = mbIdx;
                failVlcStart = vlcReader.BitPosition;
                failFlagStart = flagReader.BitPosition;
                failQuantizer = currentQuantizer;
                AdvanceMacroblockBitsOnly(frame, vlcReader, flagReader, ref currentQuantizer);
            }

            failMessage = "walked 80 macroblocks without reproducing the failure";
        }
        catch (Exception ex)
        {
            failMessage = $"{ex.GetType().Name}: {ex.Message}";
        }

        log.Add($"failure_mb={failMbIndex} fail_vlc={failVlcStart} fail_flag={failFlagStart} q={failQuantizer}");
        log.Add($"failure={failMessage}");

        var candidates = new List<(int VlcDelta, int FlagDelta, int OkMbs, int EndVlc, int EndFlag, string? Failure)>();
        for (var vlcDelta = -8; vlcDelta <= 8; vlcDelta++)
        {
            for (var flagDelta = -4; flagDelta <= 4; flagDelta++)
            {
                var trialVlc = failVlcStart + vlcDelta;
                var trialFlag = failFlagStart + flagDelta;
                if (trialVlc < 0 || trialFlag < 0)
                    continue;

                var trialReaders = CreateDiagnosticReadersAtBits(frame, trialVlc, trialFlag);
                var trialVlcReader = trialReaders.VlcReader;
                var trialFlagReader = trialReaders.FlagReader;
                var trialQuantizer = failQuantizer;
                var succeeded = 0;
                string? trialFailure = null;

                try
                {
                    for (; succeeded < 8; succeeded++)
                        AdvanceMacroblockBitsOnly(frame, trialVlcReader, trialFlagReader, ref trialQuantizer);
                }
                catch (Exception ex)
                {
                    trialFailure = $"{ex.GetType().Name}: {ex.Message}";
                }

                if (succeeded > 0)
                {
                    candidates.Add((vlcDelta, flagDelta, succeeded, trialVlcReader.BitPosition, trialFlagReader.BitPosition, trialFailure));
                }
            }
        }

        if (candidates.Count == 0)
        {
            log.Add("no nearby resync candidates found");
        }
        else
        {
            log.Add("nearby candidates:");
            foreach (var candidate in candidates
                         .OrderByDescending(static c => c.OkMbs)
                         .ThenBy(static c => Math.Abs(c.VlcDelta))
                         .ThenBy(static c => Math.Abs(c.FlagDelta))
                         .Take(40))
            {
                log.Add(
                    $"  vlc_delta={candidate.VlcDelta:+#;-#;0} flag_delta={candidate.FlagDelta:+#;-#;0} " +
                    $"ok_mbs={candidate.OkMbs} end_vlc={candidate.EndVlc} end_flag={candidate.EndFlag} " +
                    $"fail={candidate.Failure ?? "<none>"}");
            }
        }

        var message = string.Join("\n", log);
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame0_resync_search.txt"), message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame0_PreFailureWindow()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[0];
        var log = new List<string>();
        log.Add("=== Frame 0 pre-failure window ===");

        var readers = CreateDiagnosticReaders(frame);
        var vlcReader = readers.VlcReader;
        var flagReader = readers.FlagReader;
        log.Add($"reader_mode={readers.Mode}");
        var currentQuantizer = frame.Quantizer;

        try
        {
            for (var mbIdx = 0; mbIdx < 60; mbIdx++)
            {
                var shouldLogMb = mbIdx < 6 || mbIdx >= 48;
                var mbVlcStart = vlcReader.BitPosition;
                var mbFlagStart = flagReader.BitPosition;
                var probe = ProbeControl(frame, vlcReader, flagReader, currentQuantizer);

                if (shouldLogMb)
                {
                    log.Add(
                        $"MB {mbIdx}: vlc@{mbVlcStart}->{vlcReader.BitPosition} " +
                        $"flag@{mbFlagStart}->{flagReader.BitPosition} stage={probe.Stage} " +
                        $"type={probe.MacroblockType} cp=0x{probe.ControlPrefix:X} sel={probe.Selector} " +
                        $"cw=0x{probe.ControlWord:X2} feat={probe.FeatureBit} q={probe.Quantizer}");
                }

                currentQuantizer = probe.Quantizer;
                if (probe.Stage != Vid1ControlStage.A878)
                    continue;

                var cbp = probe.ControlWord & 0x3F;
                var dcThreshold = IntraDcThresholdTable[Math.Clamp(frame.IntraDcThresholdIndex, 0, 7)];
                var dcPreDecode = probe.Quantizer < dcThreshold;

                for (var block = 0; block < 6; block++)
                {
                    var blockVlcStart = vlcReader.BitPosition;
                    var blockFlagStart = flagReader.BitPosition;
                    var dcSize = 0;
                    var dcValue = 0;

                    if (dcPreDecode)
                    {
                        var isLuma = block < 4;
                        dcSize = Vid1IntraDc.DecodeSize(vlcReader, isLuma);
                        if (dcSize != 0)
                            dcValue = Vid1IntraDc.DecodeValue(vlcReader, dcSize);
                        if (dcSize > 8)
                            flagReader.SkipBits(1);
                    }

                    var coded = (cbp & (1 << (5 - block))) != 0;
                    var acStart = vlcReader.BitPosition;
                    string bundleABits = "-";
                    string bundleBBits = "-";

                    if (coded)
                    {
                        Span<short> quant = stackalloc short[64];
                        if (dcPreDecode)
                            quant[0] = (short)dcValue;

                        if (shouldLogMb)
                        {
                            bundleABits = ProbeResidualLength(
                                frame.CodedPayload,
                                acStart,
                                dcPreDecode ? 1 : 0,
                                useBundleB: false);
                            bundleBBits = ProbeResidualLength(
                                frame.CodedPayload,
                                acStart,
                                dcPreDecode ? 1 : 0,
                                useBundleB: true);
                        }
                        else
                        {
                            bundleABits = "-";
                            bundleBBits = "-";
                        }

                        Vid1CoefficientDecoder.DecodeBlock(
                            vlcReader,
                            useBundleB: true,
                            Vid1CoefficientDecoder.GetScanTable("zigzag"),
                            quant,
                            startIndex: dcPreDecode ? 1 : 0);
                    }

                    if (shouldLogMb)
                    {
                        log.Add(
                            $"  block {block}: vlc@{blockVlcStart}->{vlcReader.BitPosition} " +
                            $"flag@{blockFlagStart}->{flagReader.BitPosition} dc_size={dcSize} dc_val={dcValue} " +
                            $"coded={coded} ac_bits={vlcReader.BitPosition - acStart} " +
                            $"probeA={bundleABits} probeB={bundleBBits}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"FAIL: {ex.GetType().Name}: {ex.Message} vlc@{vlcReader.BitPosition} flag@{flagReader.BitPosition}");
        }

        var message = string.Join("\n", log);
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame0_prefailure_window.txt"), message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame1_FirstFailureWithActiveDecoderPath()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[1];
        var log = new List<string>();
        log.Add("=== Frame 1 active-path first failure ===");
        log.Add(
            $"frame1: tag16=0x{frame.Tag16:X4} class={frame.PreambleClass} qp={frame.Quantizer} " +
            $"fwd={frame.ForwardCode} bwd={frame.BackwardCode} thr={frame.IntraDcThresholdIndex} " +
            $"special={frame.HasSpecialCallerGate} coded={frame.CodedPayload.Length} flag={frame.Bitstream.Length}");
        log.Add("coded first16: " + string.Join(" ", frame.CodedPayload.Take(16).Select(static b => $"{b:X2}")));
        log.Add("flag  first16: " + string.Join(" ", frame.Bitstream.Take(16).Select(static b => $"{b:X2}")));

        var context = BuildFrameContext(file, frame);
        var readers = CreateDiagnosticReaders(frame);
        var vlcReader = readers.VlcReader;
        var flagReader = readers.FlagReader;
        log.Add($"reader_mode={readers.Mode}");
        var currentQuantizer = frame.Quantizer;

        try
        {
            for (var mbIdx = 0; mbIdx < 24; mbIdx++)
            {
                var mbX = mbIdx % context.MbCols;
                var mbY = mbIdx / context.MbCols;
                var vlcStart = vlcReader.BitPosition;
                var flagStart = flagReader.BitPosition;
                var probe = ProbeControl(frame, vlcReader, flagReader, currentQuantizer);
                log.Add(
                    $"MB {mbIdx} ({mbX},{mbY}): vlc@{vlcStart}->{vlcReader.BitPosition} " +
                    $"flag@{flagStart}->{flagReader.BitPosition} stage={probe.Stage} type={probe.MacroblockType} " +
                    $"cp=0x{probe.ControlPrefix:X} sel={probe.Selector} cw=0x{probe.ControlWord:X2} " +
                    $"pre={probe.PreCbpFlag} feat={probe.FeatureBit} q={probe.Quantizer}");
                currentQuantizer = probe.Quantizer;

                var decodeVlcStart = vlcReader.BitPosition;
                var decodeFlagStart = flagReader.BitPosition;
                Vid1MacroblockDecoder.Decode(vlcReader, flagReader, probe, context, mbX, mbY);
                log.Add(
                    $"  decode ok: vlc@{decodeVlcStart}->{vlcReader.BitPosition} " +
                    $"flag@{decodeFlagStart}->{flagReader.BitPosition}");
            }
        }
        catch (Exception ex)
        {
            log.Add($"FAIL: {ex.GetType().Name}: {ex.Message}");
            log.Add($"reader state: vlc={vlcReader.BitPosition} flag={flagReader.BitPosition} current_q={currentQuantizer}");
        }

        var message = string.Join("\n", log);
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame1_active_path.txt"), message);

        Assert.NotEmpty(log);
    }

    [Fact]
    public void Diagnostic_Frame1_HeaderWalk_CurrentLayout()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var frame = file.Frames[1];
        var payload = ReadFramePayload(path, 1);
        var headerStream = payload.AsSpan(12, payload.Length - 12 - 8).ToArray();
        var reader = new Vid1BitReader(headerStream);
        var log = new List<string>();
        log.Add("=== Frame 1 header walk ===");
        log.Add($"parsed frame: qp={frame.Quantizer} fwd={frame.ForwardCode} thr={frame.IntraDcThresholdIndex} coded={frame.CodedPayload.Length}");

        var spritePointCount = frame.SpritePointCount;
        var spriteWarpAccuracy = frame.SpriteWarpAccuracy;

        _ = reader.ReadBits(16);
        var preambleClass = reader.ReadBits(2);
        var hasOptionalHeader = reader.ReadFlag();
        log.Add($"class={preambleClass} hasOptional={hasOptionalHeader} bit={reader.BitPosition}");

        if (hasOptionalHeader)
        {
            var spriteConfigPresent = reader.ReadFlag();
            log.Add($"spriteConfigPresent={spriteConfigPresent} bit={reader.BitPosition}");
            if (spriteConfigPresent)
            {
                spritePointCount = reader.ReadBits(2);
                spriteWarpAccuracy = reader.ReadBits(2);
                log.Add($"spritePointCount={spritePointCount} spriteWarpAccuracy={spriteWarpAccuracy} bit={reader.BitPosition}");
            }
        }

        var usesCustomQuantMatrices = reader.ReadFlag();
        log.Add($"usesCustomQuantMatrices={usesCustomQuantMatrices} bit={reader.BitPosition}");
        if (usesCustomQuantMatrices)
        {
            var intraFlag = reader.ReadFlag();
            log.Add($"intraMatrixFlag={intraFlag} bit={reader.BitPosition}");
            if (intraFlag)
            {
                SkipMatrix(reader);
                log.Add($"afterIntraMatrix bit={reader.BitPosition}");
            }

            var interFlag = reader.ReadFlag();
            log.Add($"interMatrixFlag={interFlag} bit={reader.BitPosition}");
            if (interFlag)
            {
                SkipMatrix(reader);
                log.Add($"afterInterMatrix bit={reader.BitPosition}");
            }
        }

        var stateFlag3c = reader.ReadFlag();
        var discard = reader.ReadFlag();
        var threshold = reader.ReadBits(3);
        var quantizer = reader.ReadBits(5);
        var forwardCode = preambleClass != 0 ? reader.ReadBits(3) : -1;
        log.Add(
            $"stateFlag3c={stateFlag3c} discard={discard} thr={threshold} qp={quantizer} " +
            $"fwd={forwardCode} bit={reader.BitPosition}");

        var stateWord = reader.ReadBitsUInt32();
        log.Add($"stateWord=0x{stateWord:X8} bit={reader.BitPosition}");

        if (preambleClass == 3 && spritePointCount.HasValue)
        {
            for (var i = 0; i < spritePointCount.Value; i++)
            {
                var dx = reader.ReadBits(14);
                var dxNeg = reader.ReadFlag();
                var dy = reader.ReadBits(14);
                var dyNeg = reader.ReadFlag();
                log.Add($"traj[{i}] dx={dx} dxNeg={dxNeg} dy={dy} dyNeg={dyNeg} bit={reader.BitPosition}");
            }
        }

        if (stateFlag3c)
        {
            var extra0 = reader.ReadFlag();
            var extra1 = reader.ReadFlag();
            log.Add($"state extras={extra0},{extra1} bit={reader.BitPosition}");
        }

        reader.AlignToNextByte();
        log.Add($"alignedBit={reader.BitPosition} codedOffset={12 + reader.BytesConsumed}");
        log.Add("coded first16 from payload walk: " + string.Join(" ", payload.Skip(12 + reader.BytesConsumed).Take(16).Select(static b => $"{b:X2}")));

        var message = string.Join("\n", log);
        Console.WriteLine(message);
        File.WriteAllText(Path.Combine(GetDiagnosticOutputDir(), "frame1_header_walk.txt"), message);

        Assert.NotEmpty(log);
    }
}
