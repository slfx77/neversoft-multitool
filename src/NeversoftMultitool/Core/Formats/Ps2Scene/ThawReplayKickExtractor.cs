using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal enum GsVertexEventKind
{
    Vertex = 0,
    Gap = 1
}

internal sealed class GsVertexEvent
{
    public required int OutputIndex { get; init; }
    public required int FullOutputAddress { get; init; }
    public required byte OutputAddress { get; init; }
    public required GsVertexEventKind Kind { get; init; }
    public required ReplayVertexSource? VertexSource { get; init; }
    public required bool IsNoKick { get; init; }
    public required bool IsBufferedCarry { get; init; }
}

internal readonly record struct ReplayOutputSlot(
    ReplayVertexSource Source,
    bool IsNoKick);

internal static class ThawReplayKickExtractor
{
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

    public static List<ExtractedKick> ExtractKicks(
        IReadOnlyList<(uint MaterialChecksum, bool HasVertexColors)> entries,
        IReadOnlyList<ThawReplayBatch> batches)
    {
        var extracted = new List<ExtractedKick>();
        if (entries.Count == 0 || batches.Count == 0)
            return extracted;

        var setupSlotBufferBySetup = new Dictionary<int, Dictionary<int, ReplayOutputSlot>>();
        var kickBufferBySetupAndAddress = new Dictionary<(int SetupIndex, int BufferAddress), Dictionary<int, ReplayOutputSlot>>();

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            if (batch.VertexSources.Length == 0)
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

                foreach (var source in batch.VertexSources)
                {
                    preambleBuffer[source.OutputFullAddress] =
                        new ReplayOutputSlot(source, source.OutputNoKick);
                    if (source.DuplicateAddress != source.OutputAddress)
                    {
                        preambleBuffer[source.DuplicateFullAddress] =
                            new ReplayOutputSlot(source, source.DuplicateNoKick);
                    }
                }

                continue;
            }

            var entryIndex = Math.Min(batch.SetupIndex, entries.Count - 1);
            var entry = entries[entryIndex];
            setupSlotBufferBySetup.TryGetValue(batch.SetupIndex, out var setupSlotBuffer);

            var fullOutputWindow = BuildOutputWindow(batch.OutputKickPacket);
            var resolvedBatchSlots = BuildCurrentSlotMap(batch.VertexSources, fullOutputWindow);
            if (resolvedBatchSlots.Count == 0)
                continue;

            ApplyPostBatchCopies(resolvedBatchSlots, fullOutputWindow, batch.PostBatchElements, setupSlotBuffer);

            setupSlotBufferBySetup[batch.SetupIndex] = MergeSlotBuffer(setupSlotBuffer, resolvedBatchSlots);

            var bufferKey = (batch.SetupIndex, batch.OutputKickPacket.Address);
            kickBufferBySetupAndAddress.TryGetValue(bufferKey, out var previousKickBuffer);
            var resetKickBuffer = batch.Snapshot.ParserTag.IsPresent ||
                                  previousKickBuffer is null;
            var kickBuffer = resetKickBuffer
                ? new Dictionary<int, ReplayOutputSlot>()
                : new Dictionary<int, ReplayOutputSlot>(previousKickBuffer!);

            foreach (var (addr, source) in resolvedBatchSlots)
                kickBuffer[addr] = source;

            if (!batch.OutputKickPacket.Eop)
            {
                kickBufferBySetupAndAddress[bufferKey] = new Dictionary<int, ReplayOutputSlot>(kickBuffer);
                continue;
            }

            var events = BuildKickEvents(fullOutputWindow, kickBuffer, resolvedBatchSlots);
            var meshes = BuildMeshesFromEvents(events, entry.MaterialChecksum, entry.HasVertexColors);
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
                TriangleCount = meshes.Sum(CountStripTriangles)
            });
        }

        return extracted;
    }

    private static Dictionary<int, ReplayOutputSlot> CaptureKickBufferWindow(
        IReadOnlyDictionary<int, ReplayOutputSlot> kickBuffer,
        IReadOnlyList<int> outputWindow)
    {
        var captured = new Dictionary<int, ReplayOutputSlot>(outputWindow.Count);
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
        IReadOnlyList<ReplayVertexSource> vertexSources,
        IReadOnlyList<int> outputWindow)
    {
        var outputWindowSet = outputWindow.ToHashSet();
        var fullAddressByLowByte = new Dictionary<byte, int>(outputWindow.Count);
        foreach (var fullAddress in outputWindow)
            fullAddressByLowByte[(byte)fullAddress] = fullAddress;

        var slotMap = new Dictionary<int, ReplayOutputSlot>();
        foreach (var source in vertexSources)
        {
            if (TryResolveOutputAddress(source.OutputFullAddress, source.OutputAddress, outputWindowSet, fullAddressByLowByte, out var outputAddress))
                slotMap[outputAddress] = new ReplayOutputSlot(source, source.OutputNoKick);

            if (source.DuplicateAddress != source.OutputAddress &&
                TryResolveOutputAddress(source.DuplicateFullAddress, source.DuplicateAddress, outputWindowSet, fullAddressByLowByte, out var duplicateAddress))
            {
                slotMap[duplicateAddress] = new ReplayOutputSlot(source, source.DuplicateNoKick);
            }
        }

        return slotMap;
    }

    private static bool TryResolveOutputAddress(
        int fullAddress,
        byte lowByteAddress,
        HashSet<int> outputWindowSet,
        IReadOnlyDictionary<byte, int> fullAddressByLowByte,
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
        IReadOnlyList<int> outputWindow,
        IReadOnlyList<PostBatchElement> postBatchElements,
        IReadOnlyDictionary<int, ReplayOutputSlot>? setupSlotBuffer)
    {
        if (postBatchElements.Count == 0)
            return;

        var outputWindowSet = outputWindow.ToHashSet();
        var firstHasTagInC0 = (postBatchElements[0].C0 & 0x8000) != 0;
        var firstHasTagInC2 = (postBatchElements[0].C2 & 0x8000) != 0;

        for (var i = 0; i < postBatchElements.Count; i++)
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

    private static GsVertexEvent[] BuildKickEvents(
        IReadOnlyList<int> outputWindow,
        IReadOnlyDictionary<int, ReplayOutputSlot> kickBuffer,
        IReadOnlyDictionary<int, ReplayOutputSlot> currentBatchSlots)
    {
        var events = new GsVertexEvent[outputWindow.Count];
        for (var i = 0; i < outputWindow.Count; i++)
        {
            var addr = outputWindow[i];
            if (kickBuffer.TryGetValue(addr, out var source))
            {
                events[i] = new GsVertexEvent
                {
                    OutputIndex = i,
                    FullOutputAddress = addr,
                    OutputAddress = (byte)addr,
                    Kind = GsVertexEventKind.Vertex,
                    VertexSource = source.Source,
                    IsNoKick = source.IsNoKick,
                    IsBufferedCarry = !currentBatchSlots.ContainsKey(addr)
                };
            }
            else
            {
                events[i] = new GsVertexEvent
                {
                    OutputIndex = i,
                    FullOutputAddress = addr,
                    OutputAddress = (byte)addr,
                    Kind = GsVertexEventKind.Gap,
                    VertexSource = null,
                    IsNoKick = false,
                    IsBufferedCarry = false
                };
            }
        }

        return events;
    }

    private static List<Ps2Mesh> BuildMeshesFromEvents(
        IReadOnlyList<GsVertexEvent> events,
        uint materialChecksum,
        bool hasColors)
    {
        var subStrips = new List<(Ps2Vertex[] Vertices, bool StartsOnOddOutputSlot)>();
        var currentStrip = new List<Ps2Vertex>();
        var stripStartIndex = -1;

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (evt.Kind != GsVertexEventKind.Vertex || evt.VertexSource is not ReplayVertexSource source)
            {
                AddSubStrip(subStrips, currentStrip, stripStartIndex);
                currentStrip.Clear();
                stripStartIndex = -1;
                continue;
            }

            if (currentStrip.Count == 0)
                stripStartIndex = i;

            currentStrip.Add(ToPs2Vertex(source, hasColors, evt.IsNoKick));
        }

        AddSubStrip(subStrips, currentStrip, stripStartIndex);

        var meshes = new List<Ps2Mesh>(subStrips.Count);
        foreach (var (vertices, startsOnOddOutputSlot) in subStrips)
        {
            meshes.Add(new Ps2Mesh
            {
                Checksum = materialChecksum,
                MaterialChecksum = materialChecksum,
                StartsOnOddOutputSlot = startsOnOddOutputSlot,
                Vertices = vertices
            });
        }

        return meshes;
    }

    private static void AddSubStrip(
        List<(Ps2Vertex[] Vertices, bool StartsOnOddOutputSlot)> subStrips,
        List<Ps2Vertex> currentStrip,
        int stripStartIndex)
    {
        if (currentStrip.Count < 3 || stripStartIndex < 0)
            return;

        subStrips.Add(([.. currentStrip], (stripStartIndex & 1) != 0));
    }

    private static Ps2Vertex ToPs2Vertex(ReplayVertexSource source, bool hasColors, bool isNoKick)
    {
        return new Ps2Vertex(
            source.Position,
            source.Normal,
            128,
            128,
            128,
            128,
            source.U,
            source.V,
            source.HasNormal,
            hasColors,
            source.HasUv,
            isStripRestart: isNoKick);
    }

    private static int CountStripTriangles(Ps2Mesh mesh)
    {
        var verts = mesh.Vertices;
        var count = 0;
        var stripStart = 0;
        var parityBias = mesh.StartsOnOddOutputSlot ? 1 : 0;

        for (var i = 0; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart)
                continue;

            if (i - stripStart < 2)
                continue;

            Ps2Vertex a, b, c;
            if (((i - stripStart + parityBias) & 1) == 0)
            {
                a = verts[i - 2];
                b = verts[i - 1];
                c = verts[i];
            }
            else
            {
                a = verts[i - 1];
                b = verts[i - 2];
                c = verts[i];
            }

            if (IsDegenerate(a, b, c))
                continue;

            count++;
        }

        return count;
    }

    private static bool IsDegenerate(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        const float epsilon = 1e-8f;

        if (Vector3.DistanceSquared(a.Position, b.Position) <= epsilon ||
            Vector3.DistanceSquared(b.Position, c.Position) <= epsilon ||
            Vector3.DistanceSquared(a.Position, c.Position) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        return cross.LengthSquared() <= epsilon;
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
}
