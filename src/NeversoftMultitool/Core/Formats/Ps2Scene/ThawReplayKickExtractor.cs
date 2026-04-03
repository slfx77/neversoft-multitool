using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawReplayKickExtractor
{
    public static List<ExtractedKick> ExtractKicks(
        IReadOnlyList<(uint MaterialChecksum, bool HasVertexColors)> entries,
        IReadOnlyList<int> setupEntryIndices,
        IReadOnlyList<ThawReplayBatch> batches)
    {
        var extracted = new List<ExtractedKick>();
        if (entries.Count == 0 || batches.Count == 0)
            return extracted;

        var setupSlotBufferBySetup = new Dictionary<int, Dictionary<int, ReplayOutputSlot>>();
        var kickBufferBySetupAndAddress =
            new Dictionary<(int SetupIndex, int BufferAddress), Dictionary<int, ReplayOutputSlot>>();

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            if (batch.EmittedVertices.Length == 0)
                continue;

            if (!IsValidKick(batch.OutputKickPacket))
            {
                // Batches without valid kicks (e.g., preamble) still contribute vertices
                // to the setup buffer so subsequent kicks can resolve them via post-batch copies.
                if (!setupSlotBufferBySetup.TryGetValue(batch.SetupIndex, out var preambleBuffer))
                {
                    preambleBuffer = new Dictionary<int, ReplayOutputSlot>();
                    setupSlotBufferBySetup[batch.SetupIndex] = preambleBuffer;
                }

                foreach (var emitted in batch.EmittedVertices)
                    preambleBuffer[emitted.FullOutputAddress] =
                        new ReplayOutputSlot(emitted.Source, emitted.IsNoKick);

                continue;
            }

            var entryIndex = ResolveEntryIndex(batch.SetupIndex, setupEntryIndices, entries.Count);
            var entry = entries[entryIndex];
            setupSlotBufferBySetup.TryGetValue(batch.SetupIndex, out var setupSlotBuffer);

            var fullOutputWindow = BuildOutputWindow(batch.OutputKickPacket);
            var resolvedBatchSlots = BuildCurrentSlotMap(batch.EmittedVertices, fullOutputWindow);
            if (resolvedBatchSlots.Count == 0)
                continue;

            ApplyPostBatchCopies(resolvedBatchSlots, fullOutputWindow, batch.PostBatchElements, setupSlotBuffer);

            setupSlotBufferBySetup[batch.SetupIndex] = MergeSlotBuffer(setupSlotBuffer, resolvedBatchSlots);

            var bufferKey = (batch.SetupIndex, batch.OutputKickPacket.Address);
            kickBufferBySetupAndAddress.TryGetValue(bufferKey, out var previousKickBuffer);
            // Buffer isolation is already handled by (SetupIndex, BufferAddress) key.
            // ParserTag changes within the same key don't invalidate vertex data.
            var kickBuffer = previousKickBuffer is null
                ? new Dictionary<int, ReplayOutputSlot>()
                : new Dictionary<int, ReplayOutputSlot>(previousKickBuffer);

            foreach (var (addr, source) in resolvedBatchSlots)
                kickBuffer[addr] = source;

            if (!batch.OutputKickPacket.Eop)
            {
                kickBufferBySetupAndAddress[bufferKey] = new Dictionary<int, ReplayOutputSlot>(kickBuffer);
                continue;
            }

            var events = ThawReplayKickMeshBuilder.BuildKickEvents(fullOutputWindow, kickBuffer, resolvedBatchSlots);
            var meshes = ThawReplayKickMeshBuilder.BuildMeshesFromEvents(
                events,
                entry.MaterialChecksum,
                entry.HasVertexColors);
            kickBufferBySetupAndAddress[bufferKey] = CaptureKickBufferWindow(kickBuffer, fullOutputWindow);

            extracted.Add(new ExtractedKick
            {
                KickIndex = extracted.Count,
                BatchIndex = batchIndex,
                SetupIndex = batch.SetupIndex,
                EntryIndex = entryIndex,
                IsPreambleBatch = batch.IsPreambleBatch,
                FirstCommandOffset = batch.FirstCommandOffset,
                KickPacket = batch.OutputKickPacket,
                FullOutputWindow = fullOutputWindow,
                OutputWindow = [.. fullOutputWindow.Select(address => (byte)address)],
                Events = events,
                Meshes = [.. meshes],
                TriangleCount = meshes.Sum(ThawReplayKickMeshBuilder.CountStripTriangles)
            });
        }

        // Second pass: if any kicks have gaps, re-run with pre-populated setup buffers.
        // On real PS2, the VU1 output buffer persists across frames. The first kick at each
        // address has gaps because post-batch copies reference source addresses in the OTHER
        // buffer (e.g., addr=280 kick copies from 653-869 range), which hasn't been populated yet.
        // After the first pass, setupSlotBufferBySetup contains vertices from BOTH address ranges.
        // Re-running with this pre-populated buffer lets post-batch copies resolve cross-buffer
        // sources. We do NOT seed kickBufferBySetupAndAddress to avoid carry-forward contamination
        // (which previously caused inward-facing spikes from wrong-body-part vertices).
        if (extracted.Any(k => k.Events.Any(e => e.Kind == GsVertexEventKind.Gap)))
        {
            return ExtractKicksWithSetupSeed(entries, setupEntryIndices, batches, setupSlotBufferBySetup);
        }

        return extracted;
    }

    private static List<ExtractedKick> ExtractKicksWithSetupSeed(
        IReadOnlyList<(uint MaterialChecksum, bool HasVertexColors)> entries,
        IReadOnlyList<int> setupEntryIndices,
        IReadOnlyList<ThawReplayBatch> batches,
        Dictionary<int, Dictionary<int, ReplayOutputSlot>> seedSetupBuffers)
    {
        var extracted = new List<ExtractedKick>();

        // Deep-copy the first-pass setup buffers as the starting state.
        var setupSlotBufferBySetup = new Dictionary<int, Dictionary<int, ReplayOutputSlot>>();
        foreach (var (setupIdx, buffer) in seedSetupBuffers)
            setupSlotBufferBySetup[setupIdx] = new Dictionary<int, ReplayOutputSlot>(buffer);

        // Kick buffers start fresh — no carry-forward seeding.
        var kickBufferBySetupAndAddress =
            new Dictionary<(int SetupIndex, int BufferAddress), Dictionary<int, ReplayOutputSlot>>();

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            if (batch.EmittedVertices.Length == 0)
                continue;

            if (!IsValidKick(batch.OutputKickPacket))
            {
                if (!setupSlotBufferBySetup.TryGetValue(batch.SetupIndex, out var preambleBuffer))
                {
                    preambleBuffer = new Dictionary<int, ReplayOutputSlot>();
                    setupSlotBufferBySetup[batch.SetupIndex] = preambleBuffer;
                }

                foreach (var emitted in batch.EmittedVertices)
                    preambleBuffer[emitted.FullOutputAddress] =
                        new ReplayOutputSlot(emitted.Source, emitted.IsNoKick);

                continue;
            }

            var entryIndex = ResolveEntryIndex(batch.SetupIndex, setupEntryIndices, entries.Count);
            var entry = entries[entryIndex];
            setupSlotBufferBySetup.TryGetValue(batch.SetupIndex, out var setupSlotBuffer);

            var fullOutputWindow = BuildOutputWindow(batch.OutputKickPacket);
            var resolvedBatchSlots = BuildCurrentSlotMap(batch.EmittedVertices, fullOutputWindow);
            if (resolvedBatchSlots.Count == 0)
                continue;

            ApplyPostBatchCopies(resolvedBatchSlots, fullOutputWindow, batch.PostBatchElements, setupSlotBuffer);

            setupSlotBufferBySetup[batch.SetupIndex] = MergeSlotBuffer(setupSlotBuffer, resolvedBatchSlots);

            var bufferKey = (batch.SetupIndex, batch.OutputKickPacket.Address);
            kickBufferBySetupAndAddress.TryGetValue(bufferKey, out var previousKickBuffer);
            var kickBuffer = previousKickBuffer is null
                ? new Dictionary<int, ReplayOutputSlot>()
                : new Dictionary<int, ReplayOutputSlot>(previousKickBuffer);

            foreach (var (addr, source) in resolvedBatchSlots)
                kickBuffer[addr] = source;

            if (!batch.OutputKickPacket.Eop)
            {
                kickBufferBySetupAndAddress[bufferKey] = new Dictionary<int, ReplayOutputSlot>(kickBuffer);
                continue;
            }

            var events = ThawReplayKickMeshBuilder.BuildKickEvents(fullOutputWindow, kickBuffer, resolvedBatchSlots);
            var meshes = ThawReplayKickMeshBuilder.BuildMeshesFromEvents(
                events,
                entry.MaterialChecksum,
                entry.HasVertexColors);
            kickBufferBySetupAndAddress[bufferKey] = CaptureKickBufferWindow(kickBuffer, fullOutputWindow);

            extracted.Add(new ExtractedKick
            {
                KickIndex = extracted.Count,
                BatchIndex = batchIndex,
                SetupIndex = batch.SetupIndex,
                EntryIndex = entryIndex,
                IsPreambleBatch = batch.IsPreambleBatch,
                FirstCommandOffset = batch.FirstCommandOffset,
                KickPacket = batch.OutputKickPacket,
                FullOutputWindow = fullOutputWindow,
                OutputWindow = [.. fullOutputWindow.Select(address => (byte)address)],
                Events = events,
                Meshes = [.. meshes],
                TriangleCount = meshes.Sum(ThawReplayKickMeshBuilder.CountStripTriangles)
            });
        }

        return extracted;
    }

    private static int ResolveEntryIndex(int setupIndex, IReadOnlyList<int> setupEntryIndices, int entryCount)
    {
        if (entryCount == 0)
            return -1;

        if ((uint)setupIndex < (uint)setupEntryIndices.Count)
        {
            var entryIndex = setupEntryIndices[setupIndex];
            if ((uint)entryIndex < (uint)entryCount)
                return entryIndex;
        }

        return Math.Min(Math.Max(setupIndex, 0), entryCount - 1);
    }

    private static Dictionary<int, ReplayOutputSlot> CaptureKickBufferWindow(
        Dictionary<int, ReplayOutputSlot> kickBuffer,
        int[] outputWindow)
    {
        var captured = new Dictionary<int, ReplayOutputSlot>(outputWindow.Length);
        foreach (var addr in outputWindow)
        {
            if (kickBuffer.TryGetValue(addr, out var source))
                captured[addr] = source;
        }

        return captured;
    }

    private static bool IsValidKick(GifKickPacket kickPacket)
    {
        return kickPacket.IsPresent &&
               kickPacket.Nloop > 0 &&
               kickPacket.Nloop <= 256 &&
               kickPacket.Address is 280 or 652;
    }

    private static int[] BuildOutputWindow(GifKickPacket kickPacket)
    {
        // GS processes GIF packets in ascending address order from XGKICK address.
        // Vertex data starts at Address+1, stride 3 (3 qwords per PACKED register set).
        var outputWindow = new int[kickPacket.Nloop];
        var start = kickPacket.Address + 1;
        for (var i = 0; i < kickPacket.Nloop; i++)
            outputWindow[i] = WrapVu1Address(start + 3 * i);

        return outputWindow;
    }

    private static Dictionary<int, ReplayOutputSlot> BuildCurrentSlotMap(
        IReadOnlyList<ReplayEmittedVertex> emittedVertices,
        int[] outputWindow)
    {
        var outputWindowSet = outputWindow.ToHashSet();
        var fullAddressByLowByte = new Dictionary<byte, int>(outputWindow.Length);
        foreach (var fullAddress in outputWindow)
            fullAddressByLowByte[(byte)fullAddress] = fullAddress;

        var slotMap = new Dictionary<int, ReplayOutputSlot>();
        foreach (var emitted in emittedVertices)
        {
            if (!TryResolveOutputAddress(
                    emitted.FullOutputAddress,
                    emitted.OutputAddress,
                    outputWindowSet,
                    fullAddressByLowByte,
                    out var outputAddress))
            {
                continue;
            }

            slotMap[outputAddress] = new ReplayOutputSlot(emitted.Source, emitted.IsNoKick);
        }

        return slotMap;
    }

    private static bool TryResolveOutputAddress(
        int fullAddress,
        byte lowByteAddress,
        HashSet<int> outputWindowSet,
        Dictionary<byte, int> fullAddressByLowByte,
        out int resolvedAddress)
    {
        if (outputWindowSet.Contains(fullAddress))
        {
            resolvedAddress = fullAddress;
            return true;
        }

        return fullAddressByLowByte.TryGetValue(lowByteAddress, out resolvedAddress);
    }

    private static void ApplyPostBatchCopies(
        Dictionary<int, ReplayOutputSlot> slotMap,
        int[] outputWindow,
        PostBatchElement[] postBatchElements,
        IReadOnlyDictionary<int, ReplayOutputSlot>? setupSlotBuffer)
    {
        if (postBatchElements.Length == 0)
            return;

        var outputWindowSet = outputWindow.ToHashSet();
        var firstHasTagInC0 = (postBatchElements[0].C0 & 0x8000) != 0;
        var firstHasTagInC2 = (postBatchElements[0].C2 & 0x8000) != 0;

        for (var i = 0; i < postBatchElements.Length; i++)
        {
            if (!(i == 0 && firstHasTagInC0))
            {
                ApplyPostBatchCopyWord(
                    slotMap,
                    outputWindowSet,
                    setupSlotBuffer,
                    postBatchElements[i].C0,
                    postBatchElements[i].C1);
            }

            if (!(i == 0 && firstHasTagInC2))
            {
                ApplyPostBatchCopyWord(
                    slotMap,
                    outputWindowSet,
                    setupSlotBuffer,
                    postBatchElements[i].C2,
                    postBatchElements[i].C3);
            }
        }
    }

    private static void ApplyPostBatchCopyWord(
        Dictionary<int, ReplayOutputSlot> slotMap,
        HashSet<int> outputWindowSet,
        IReadOnlyDictionary<int, ReplayOutputSlot>? setupSlotBuffer,
        ushort sourceWord,
        ushort destinationWord)
    {
        var sourceAddr = DecodeFullAddress(sourceWord);
        var destinationAddr = DecodeFullAddress(destinationWord);
        if (!outputWindowSet.Contains(destinationAddr) || slotMap.ContainsKey(destinationAddr))
            return;

        if (!slotMap.TryGetValue(sourceAddr, out var source) &&
            (setupSlotBuffer is null || !setupSlotBuffer.TryGetValue(sourceAddr, out source)))
        {
            return;
        }

        // Post-batch copies fill gap slots in the output strip.
        // The destination word's bit 15 is the ADC/NoKick flag for this output position,
        // set by the CPU to mark strip restarts at buffer boundaries and body-part transitions.
        var destinationNoKick = (destinationWord & 0x8000) != 0;
        slotMap[destinationAddr] = new ReplayOutputSlot(source.Source, destinationNoKick);
    }

    private static Dictionary<int, ReplayOutputSlot> MergeSlotBuffer(
        Dictionary<int, ReplayOutputSlot>? existingBuffer,
        Dictionary<int, ReplayOutputSlot> resolvedBatchSlots)
    {
        if (existingBuffer is null || existingBuffer.Count == 0)
            return new Dictionary<int, ReplayOutputSlot>(resolvedBatchSlots);

        foreach (var (addr, source) in resolvedBatchSlots)
            existingBuffer[addr] = source;

        return existingBuffer;
    }

    private static int DecodeFullAddress(ushort word)
    {
        return word & 0x3FF;
    }

    private static int WrapVu1Address(int address)
    {
        var wrapped = address % Vu1Memory.SizeQwords;
        return wrapped < 0 ? wrapped + Vu1Memory.SizeQwords : wrapped;
    }

    public sealed class ExtractedKick
    {
        public required int KickIndex { get; init; }
        public required int BatchIndex { get; init; }
        public required int SetupIndex { get; init; }
        public required int EntryIndex { get; init; }
        public required bool IsPreambleBatch { get; init; }
        public required int FirstCommandOffset { get; init; }
        public required GifKickPacket KickPacket { get; init; }
        public required int[] FullOutputWindow { get; init; }
        public required byte[] OutputWindow { get; init; }
        public required GsVertexEvent[] Events { get; init; }
        public required Ps2Mesh[] Meshes { get; init; }
        public required int TriangleCount { get; init; }
    }
}
