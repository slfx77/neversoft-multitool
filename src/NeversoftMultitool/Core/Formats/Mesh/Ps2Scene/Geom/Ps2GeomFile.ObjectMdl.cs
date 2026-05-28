using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

public static partial class Ps2GeomFile
{
    /// <summary>
    ///     Object-MDL path (THAW worldzone object MDLs — cars etc., 0x9BCC234D). These have
    ///     explicit bone sections and few preamble records; the scanner + ScanBatchForCenter
    ///     combo has historically worked for them.
    /// </summary>
    private static Ps2GeomScene ParseObjectMdl(
        byte[] data,
        Ps2MdlPreamble.Preamble? preamble,
        IReadOnlyList<Ps2MdlPreamble.MdlBone>? bones,
        int vifStart,
        string? diagnosticsName,
        Action<Ps2GeomLeafRejection>? rejectionLogger)
    {
        var leaves = new List<Ps2GeomLeaf>();
        var currentGsCtx = new Ps2GeomGsContext();
        var currentCenter = Vector3.Zero;
        var hasGsContext = false;

        var batchRanges = Ps2GeomMdlBatchScanner.FindMscalBatchRanges(data, vifStart, data.Length);
        var signatureBatchRanges = Ps2GeomMdlBatchScanner.FindRepeatedBatchSignatureRanges(data, vifStart, data.Length);
        if (signatureBatchRanges.Count >= 8 && signatureBatchRanges.Count > batchRanges.Count)
            batchRanges = signatureBatchRanges;

        var placements = Ps2MdlPlacementResolver.ResolveObjectPlacements(preamble, batchRanges);

        for (var batchIndex = 0; batchIndex < batchRanges.Count; batchIndex++)
        {
            var (batchStart, batchEnd) = batchRanges[batchIndex];
            var gsCtx = Ps2GeomMdlBatchScanner.ScanBatchForGsContext(data, batchStart, batchEnd);
            var center = Ps2GeomMdlBatchScanner.ScanBatchForCenter(data, batchStart, batchEnd);

            if (gsCtx is { HasRegisters: true } scan)
            {
                currentGsCtx = scan.MergeWith(currentGsCtx);
                hasGsContext = true;
                currentCenter = center ?? Vector3.Zero;
            }
            else if (center.HasValue)
            {
                currentCenter = center.Value;
            }

            var batchVerts = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(data, batchStart, batchEnd, currentCenter);
            if (placements.TryGetValue(batchIndex, out var placement))
                batchVerts = Ps2MdlPlacementResolver.ApplyPlacement(batchVerts, placement);
            if (batchVerts.Length == 0)
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "empty_batch", batchIndex, batchVerts, currentGsCtx.Tex0));
                continue;
            }

            if (ShouldSkipWorldZoneBatch(batchVerts))
            {
                rejectionLogger?.Invoke(MakeRejection(
                    diagnosticsName, "parse", "huge_origin_helper_batch", batchIndex, batchVerts, currentGsCtx.Tex0));
                continue;
            }

            leaves.Add(MakeLeafFromMdlMesh(batchVerts, hasGsContext ? currentGsCtx : new Ps2GeomGsContext()));
        }

        return new Ps2GeomScene { Leaves = leaves, MdlPreamble = preamble, Bones = bones };
    }

    private static void WalkNodeTree(byte[] data, int baseOffset, int rootNodeOffset, List<Ps2GeomLeaf> leaves)
    {
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        stack.Push(rootNodeOffset);

        while (stack.Count > 0)
        {
            var nodeOffset = stack.Pop();
            if (nodeOffset == -1 || !visited.Add(nodeOffset))
                continue;

            var abs = baseOffset + nodeOffset;
            if (abs + 80 > data.Length)
                continue;

            var span = data.AsSpan(abs);
            var sphereX = BinaryPrimitives.ReadSingleLittleEndian(span);
            var sphereY = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
            var sphereZ = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
            var sphereR = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
            var u1 = BinaryPrimitives.ReadInt32LittleEndian(span[0x20..]);
            var sibling = BinaryPrimitives.ReadInt32LittleEndian(span[0x28..]);
            var groupChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x30..]);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
            var textureChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]);
            var nextLod = BinaryPrimitives.ReadInt32LittleEndian(span[0x4C..]);
            var isLeaf = (flags & NodeFlagLeaf) != 0;

            if (isLeaf && u1 != -1)
            {
                var dmaAbs = baseOffset + u1;
                if (dmaAbs + 8 <= data.Length)
                {
                    var center = new Vector3(sphereX, sphereY, sphereZ);
                    var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromDma(data, dmaAbs, center);
                    var gsCtx = Ps2GeomVifVertexDecoder.ExtractGsContextFromDma(data, dmaAbs);

                    if (vertices.Length > 0)
                    {
                        leaves.Add(new Ps2GeomLeaf
                        {
                            Checksum = checksum,
                            TextureChecksum = textureChecksum,
                            GroupChecksum = groupChecksum,
                            Colour = colour,
                            Flags = flags,
                            BoundingSphere = new Vector4(sphereX, sphereY, sphereZ, sphereR),
                            Vertices = vertices,
                            DmaTex0 = gsCtx.Tex0,
                            DmaTex1 = gsCtx.Tex1,
                            DmaMipTbp1 = gsCtx.MipTbp1,
                            DmaMipTbp2 = gsCtx.MipTbp2,
                            DmaClamp1 = gsCtx.Clamp1,
                            DmaAlpha1 = gsCtx.Alpha1,
                            DmaTest1 = gsCtx.Test1,
                            DmaFrame1 = gsCtx.Frame1,
                            DmaTexa = gsCtx.Texa
                        });
                    }
                }
            }

            if (nextLod != -1)
                stack.Push(nextLod);
            if (sibling != -1)
                stack.Push(sibling);
            if (!isLeaf && u1 != -1)
                stack.Push(u1);
        }
    }
}
