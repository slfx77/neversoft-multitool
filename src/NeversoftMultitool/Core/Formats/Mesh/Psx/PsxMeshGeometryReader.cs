using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal static class PsxMeshGeometryReader
{
    /// <summary>
    ///     First pass: scans all meshes to collect type-1 (attachable) vertices.
    ///     These are joint anchor vertices at body part boundaries. Each type-1 vertex
    ///     gets a file-wide sequential index used by type-2 vertices for stitching.
    ///     For hierarchical models (characters), positions are stored in WORLD space
    ///     (local + object offset) so type-2 vertices in other meshes can be correctly
    ///     placed regardless of their different object offsets.
    /// </summary>
    internal static List<PsxAttachmentVertex> CollectAttachableVertices(
        BinaryReader reader, uint[] meshTopPointers, ushort version, float scaleDivisor,
        List<PsxMeshObject> objects, int[] meshToObjectIndex,
        float translationDivisor, bool hasMeshLodField)
    {
        var attachmentVertices = new List<PsxAttachmentVertex>();

        // Build LOD variant set: any mesh pointed to by another mesh's LodNextMeshIndex
        // is a lower-detail duplicate and must be excluded from attachment collection,
        // otherwise sequential attachment indices get shifted by the extra type-1 vertices.
        var lodVariants = new HashSet<int>();
        for (var mi = 0; mi < meshTopPointers.Length; mi++)
        {
            reader.BaseStream.Seek(meshTopPointers[mi], SeekOrigin.Begin);
            if (version == 0x03) reader.ReadBytes(16);
            else reader.ReadBytes(8);
            reader.ReadBytes(16); // bbox
            if (hasMeshLodField)
            {
                reader.ReadInt16(); // lodDepth
                var lodNext = reader.ReadUInt16();
                if (lodNext != ushort.MaxValue && lodNext < meshTopPointers.Length)
                    lodVariants.Add(lodNext);
            }
        }

        for (var meshIndex = 0; meshIndex < meshTopPointers.Length; meshIndex++)
        {
            if (lodVariants.Contains(meshIndex))
                continue;

            var objectOffset = Vector3.Zero;
            var objectIndex = -1;
            if (meshToObjectIndex[meshIndex] >= 0)
            {
                objectIndex = meshToObjectIndex[meshIndex];
                var obj = objects[objectIndex];
                objectOffset = PsxMeshSemantics.GetObjectOffset(obj, translationDivisor);
            }

            reader.BaseStream.Seek(meshTopPointers[meshIndex], SeekOrigin.Begin);

            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            var vertexCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
            _ = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();

            reader.ReadBytes(16);

            if (hasMeshLodField)
                reader.ReadBytes(4);

            for (uint vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                var x = reader.ReadInt16();
                var y = reader.ReadInt16();
                var z = reader.ReadInt16();
                var type = reader.ReadUInt16();

                if (PsxMeshSemantics.IsExactStitchSource(type))
                {
                    var localPosition = new Vector3(x / scaleDivisor, y / scaleDivisor, z / scaleDivisor);
                    attachmentVertices.Add(new PsxAttachmentVertex
                    {
                        AttachmentIndex = (uint)attachmentVertices.Count,
                        MeshIndex = meshIndex,
                        ObjectIndex = objectIndex,
                        VertexIndex = (int)vertexIndex,
                        LocalPosition = localPosition,
                        WorldPosition = localPosition + objectOffset
                    });
                }
            }
        }

        return attachmentVertices;
    }

    internal static PsxMesh ReadMesh(BinaryReader reader, ushort version, float scaleDivisor,
        uint[] textureHashes, IReadOnlyDictionary<uint, PsxAttachmentVertex>? attachmentVertices = null,
        bool hasMeshLodField = true)
    {
        var flags = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var vertexCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var normalCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();
        var faceCount = version == 0x03 ? reader.ReadUInt32() : reader.ReadUInt16();

        reader.ReadUInt32();
        reader.ReadBytes(12);

        var lodDepth = short.MaxValue;
        var lodNextMeshIndex = ushort.MaxValue;
        if (hasMeshLodField)
        {
            lodDepth = reader.ReadInt16();
            lodNextMeshIndex = reader.ReadUInt16();
        }

        var vertices = new List<PsxVertex>((int)vertexCount);
        var stitchFailures = 0;
        for (uint vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            var type = reader.ReadUInt16();

            vertices.Add(new PsxVertex
            {
                X = x / scaleDivisor,
                Y = y / scaleDivisor,
                Z = z / scaleDivisor,
                Type = type,
                RawX = x,
                RawY = y,
                RawZ = z
            });

            if (PsxMeshSemantics.IsExactStitchedReference(type)
                && (attachmentVertices == null || !attachmentVertices.ContainsKey((ushort)y)))
            {
                stitchFailures++;
            }
        }

        var normals = new List<PsxNormal>((int)normalCount);
        for (uint normalIndex = 0; normalIndex < normalCount; normalIndex++)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var z = reader.ReadInt16();
            reader.ReadUInt16();
            normals.Add(new PsxNormal
            {
                X = x / 4096f,
                Y = y / 4096f,
                Z = z / 4096f
            });
        }

        var faces = new List<PsxFace>((int)faceCount);
        var invisibleFaces = new List<PsxFace>();
        var faceReadInfos = new List<PsxFaceReadInfo>((int)faceCount);
        for (uint faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            var (face, invisibleFace, faceReadInfo) =
                ReadFace(reader, version, vertexCount, normalCount, textureHashes, (int)faceIndex);
            if (face != null)
            {
                faceReadInfo.IsAccepted = true;
                faceReadInfo.AcceptedFaceIndex = faces.Count;
                faces.Add(face);
            }
            else if (invisibleFace != null)
            {
                invisibleFaces.Add(invisibleFace);
            }

            faceReadInfos.Add(faceReadInfo);
        }

        return new PsxMesh
        {
            Flags = flags,
            Vertices = vertices,
            Normals = normals,
            Faces = faces,
            InvisibleFaces = invisibleFaces,
            LodDepth = lodDepth,
            LodNextMeshIndex = lodNextMeshIndex,
            HasPerVertexNormals = normalCount == vertexCount + faceCount,
            VertexCount = vertexCount,
            StitchFailureCount = stitchFailures,
            FaceReadInfos = faceReadInfos
        };
    }

    /// <summary>
    ///     Probes whether a v3 mesh header has a 4-byte LOD field between the bounding box and
    ///     vertex data. THPS1 Proto (1999) v3 files have it (sentinel 0x7FFF/0xFFFF);
    ///     Apocalypse (1998) v3 files do not. Peeks ahead without advancing the stream.
    /// </summary>
    internal static bool ProbeV3HasLod(BinaryReader reader)
    {
        var savedPos = reader.BaseStream.Position;
        if (savedPos + 12 > reader.BaseStream.Length)
            return false;

        reader.BaseStream.Seek(savedPos + 6, SeekOrigin.Begin);
        var typeA = reader.ReadUInt16();
        reader.BaseStream.Seek(savedPos + 10, SeekOrigin.Begin);
        var typeB = reader.ReadUInt16();
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);

        var validA = (typeA & ~0x13) == 0;
        var validB = (typeB & ~0x13) == 0;

        if (validA && !validB) return false;
        if (!validA && validB) return true;

        var peekValue = reader.ReadInt16();
        reader.BaseStream.Seek(savedPos, SeekOrigin.Begin);
        return peekValue == 0x7FFF;
    }

    private static (PsxFace? Face, PsxFace? InvisibleFace, PsxFaceReadInfo Info) ReadFace(
        BinaryReader reader, ushort version,
        uint vertexCount, uint normalCount, uint[] textureHashes, int rawFaceIndex)
    {
        var facePosition = reader.BaseStream.Position;

        var faceFlags = reader.ReadUInt16();
        var faceLength = reader.ReadUInt16();
        var hasTexturePayload = (faceFlags & 0x0003) != 0;
        var isTextured = hasTexturePayload;
        var quad = (faceFlags & 0x0010) == 0;
        var semiTrans = (faceFlags & 0x0040) != 0;
        var gouraud = (faceFlags & 0x0800) != 0;

        // Disc vs runtime: the render path (M3dAsm_ProcessPolys @0x800999F0)
        // skips faces with (flags & 0xC0) == 0 and never toggles bit 0x0080.
        // The loader M3dInit_ParsePSX (decomp-VERIFIED @0x80093BE4, matched
        // 2026-07-07) applies exactly this rewrite at load: if bit6 (0x40,
        // semi-transparent) is clear it XORs bit7 (0x80) into the flag word —
        // arming the GPU opaque/blend-enable for solid faces. Reproducing it
        // here on raw disc flags means opaque faces (bit7 clear on disc)
        // become drawable, and a raw bit7-set-without-bit6 face is toggled
        // dark = collision-only/invisible. See the decomp's
        // psx_model_load_format.md §4.2 / face_flag_semantics.md §4a.
        // The rule holds for v3 too: THPS1-proto levels ship whole meshes
        // with bit7 pre-set (skdown: 147 meshes / 808 faces) that are
        // collision blockers and trigger-marker boards the game never draws
        // (user-verified in-viewer, 2026-07-08 — briefly un-dropped, they
        // occluded Downtown's rooftops). The "missing ground" that prompted
        // that experiment was the per-mesh LOD-probe misparse, not this drop.
        var invisible = PsxFaceFlags.IsInvisible(faceFlags);

        uint i0;
        uint i1;
        uint i2;
        uint i3;
        if (version != 0x03)
        {
            i0 = reader.ReadByte();
            i1 = reader.ReadByte();
            i2 = reader.ReadByte();
            i3 = reader.ReadByte();
        }
        else
        {
            i0 = reader.ReadUInt16();
            i1 = reader.ReadUInt16();
            i2 = reader.ReadUInt16();
            i3 = reader.ReadUInt16();
        }

        var r = reader.ReadByte();
        var g = reader.ReadByte();
        var b = reader.ReadByte();
        var mode = reader.ReadByte();

        var normalIndex = reader.ReadUInt16();
        reader.ReadInt16();

        uint textureIndex = 0;
        var textureCoordinates = new PsxTextureCoordinate[]
        {
            default,
            default,
            default,
            default
        };

        if (hasTexturePayload)
        {
            textureIndex = reader.ReadUInt32();

            if (version == 0x06)
            {
                // v6 (Spider-Man DC/PC port containers) widens UVs from the
                // PS1 byte texel pairs to plane-separated u16 U values then
                // i16 V values (all four Us, then all four Vs), in a fixed
                // 512-texel normalized space (see ComputePsxTextureUv). Layout
                // is empirical — pinned by the BLACKCAT.PSX locked fixture and
                // the DC/PC sweeps — with no decomp reference (SH-4/x86 exes
                // sit outside the MIPS toolkit).
                var xs = new int[4];
                var ys = new int[4];

                for (var i = 0; i < 4; i++)
                    xs[i] = reader.ReadUInt16();
                for (var i = 0; i < 4; i++)
                    ys[i] = reader.ReadInt16();

                for (var i = 0; i < 4; i++)
                    textureCoordinates[i] = new PsxTextureCoordinate(xs[i], ys[i]);
            }
            else
            {
                for (var i = 0; i < 4; i++)
                    textureCoordinates[i] = new PsxTextureCoordinate(reader.ReadByte(), reader.ReadByte());
            }
        }

        var expectedFaceEnd = facePosition + faceLength;
        var bytesConsumed = (int)(reader.BaseStream.Position - facePosition);
        var underreadBytes = Math.Max(faceLength - bytesConsumed, 0);
        var overreadBytes = Math.Max(bytesConsumed - faceLength, 0);
        if (reader.BaseStream.Position < expectedFaceEnd)
            reader.BaseStream.Seek(expectedFaceEnd, SeekOrigin.Begin);

        var faceReadInfo = new PsxFaceReadInfo
        {
            RawFaceIndex = rawFaceIndex,
            Offset = facePosition,
            Flags = faceFlags,
            Length = faceLength,
            BytesConsumed = bytesConsumed,
            UnderreadBytes = underreadBytes,
            OverreadBytes = overreadBytes,
            IsLengthAligned = (faceLength & 0x0003) == 0,
            TextureHash = textureIndex < textureHashes.Length
                ? textureHashes[textureIndex]
                : 0,
            TextureCoordinates = textureCoordinates
        };

        if (invisible)
        {
            // The historical rejection is preserved verbatim (accepted counts,
            // dump snapshots, and every regression fixture stay untouched), but
            // a structurally valid face is still constructed so the entity
            // forced-blend path can render it (PsxMesh.InvisibleFaces).
            faceReadInfo.RejectionReason = "invisible (M3dInit STP toggle)";
            var isRetainable = i0 < vertexCount && i1 < vertexCount && i2 < vertexCount
                               && (!quad || i3 < vertexCount)
                               && normalIndex < normalCount;
            return (null, isRetainable ? CreateFace() : null, faceReadInfo);
        }

        if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount)
        {
            faceReadInfo.RejectionReason = "vertex index out of range";
            return (null, null, faceReadInfo);
        }

        if (quad && i3 >= vertexCount)
        {
            faceReadInfo.RejectionReason = "quad vertex index out of range";
            return (null, null, faceReadInfo);
        }

        if (normalIndex >= normalCount)
        {
            faceReadInfo.RejectionReason = "normal index out of range";
            return (null, null, faceReadInfo);
        }

        return (CreateFace(), null, faceReadInfo);

        PsxFace CreateFace()
        {
            uint textureHash = 0;
            if (hasTexturePayload && textureIndex < (uint)textureHashes.Length)
                textureHash = textureHashes[textureIndex];

            return new PsxFace
            {
                Flags = faceFlags,
                IsQuad = quad,
                IsTextured = isTextured,
                IsGouraud = gouraud,
                IsSemiTransparent = semiTrans,
                Index0 = i0,
                Index1 = i1,
                Index2 = i2,
                Index3 = i3,
                NormalIndex = normalIndex,
                R = r,
                G = g,
                B = b,
                Mode = mode,
                TextureHash = textureHash,
                U0 = ToLegacyByte(textureCoordinates[0].U),
                V0 = ToLegacyByte(textureCoordinates[0].V),
                U1 = ToLegacyByte(textureCoordinates[1].U),
                V1 = ToLegacyByte(textureCoordinates[1].V),
                U2 = ToLegacyByte(textureCoordinates[2].U),
                V2 = ToLegacyByte(textureCoordinates[2].V),
                U3 = ToLegacyByte(textureCoordinates[3].U),
                V3 = ToLegacyByte(textureCoordinates[3].V),
                TextureCoordinates = textureCoordinates
            };
        }
    }

    private static byte ToLegacyByte(int value)
    {
        return (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
    }
}
