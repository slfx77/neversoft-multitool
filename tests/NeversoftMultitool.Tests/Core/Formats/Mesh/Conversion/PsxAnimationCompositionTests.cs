using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Verifies the piecewise-rigid rotation composition fix in
///     <see cref="ModelDocumentGeometryAdapter.PopulatePsxAnimations" />. The
///     THPS2 PSX engine's <c>Decomp_GetAnimTransform</c> writes each bone's
///     world rotation as its own local Euler matrix only — no parent
///     multiplication. To reproduce that invariant inside glTF's chained scene
///     graph the adapter pre-divides each emitted quaternion by its parent's
///     engine-local rotation, so the chain composes back to the engine's intent.
/// </summary>
public sealed class PsxAnimationCompositionTests
{
    private const float AngleEpsilon = 1e-3f;

    [Fact]
    public void Composition_GrandchildWorldRotation_EqualsEngineLocalRotation()
    {
        // 3-bone chain: root (Y=90°) → mid (X=45°) → leaf (identity).
        // Engine semantics: leaf.world_rot = leaf.engine_local_rot = identity.
        // After Piece 1's correction, chaining the emitted glTF quaternions
        // through the parent chain must compose back to identity (in glTF coords).
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f)), // Ry=90° on root
            (bone: 1, channelIndex: 0, s16Value: AngleToS16Units(MathF.PI / 4f))  // Rx=45° on mid
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var (root, mid, leaf) = ExtractRotationsAtFrame0(document.Animations[0]);

        // glTF chain: world_rot[leaf] = root * mid * leaf
        var composed = root * mid * leaf;
        AssertQuaternionsClose(Quaternion.Identity, composed);
    }

    [Fact]
    public void Composition_PlaceholderBoneUnderAnimatedParent_GetsCorrectingChannel()
    {
        // Only root has rotation data. Mid + leaf are placeholders. The
        // piecewise-rigid fix must STILL emit channels for mid and leaf so
        // the engine's "world_rot = local_rot (= identity for placeholders)"
        // is preserved despite glTF's automatic parent chaining.
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var rotationBones = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .Select(c => c.BoneIndex)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal([0, 1, 2], rotationBones);
        Assert.All(
            document.Animations[0].Channels.Where(static c => c.Property == ModelAnimationProperty.Rotation),
            static c => Assert.Equal(ModelAnimationInterpolation.Linear, c.Interpolation));

        // Mid and leaf channels should compose with root to keep the leaf
        // at engine-local identity.
        var (root, mid, leaf) = ExtractRotationsAtFrame0(document.Animations[0]);
        AssertQuaternionsClose(Quaternion.Identity, root * mid * leaf);
    }

    [Fact]
    public void Composition_NoAnimationData_EmitsNoChannels()
    {
        // Regression: all-placeholder animation must not emit any channels.
        var animation = BuildAnimation();
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("rest", animation)],
            new PsxAnimationOptions());

        Assert.Empty(document.Animations);
    }

    [Fact]
    public void Translation_AllZeroPlaceholderStreams_KeepBindAndEmitNoChannels()
    {
        // A clip whose translation streams are entirely zero carries placeholder
        // data. Emitting it absolutely would collapse every bone onto its
        // parent's origin, so the writer keeps bind placement instead. (Per-bone
        // zeros inside a clip that DOES carry translation data are engine truth
        // and are emitted — see the parent-relative test below.)
        var animation = BuildAnimation();
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(SkipRotation: true, SkipTranslation: false));

        Assert.Empty(document.Animations);
    }

    [Fact]
    public void Translation_WithBoneFilter_EmitsOnlySelectedStreams()
    {
        // Skater banks can carry meaningful board motion on one root-side bone
        // while limb translations are noise for the current export path. The
        // diagnostic filter lets visual checks isolate that case.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 3, frame: 1, s16Value: 10),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 20),
            (bone: 2, channelIndex: 3, frame: 1, s16Value: 30));
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("board_only", animation)],
            new PsxAnimationOptions(
                SkipRotation: true,
                SkipTranslation: false,
                TranslationBoneFilter: new HashSet<int> { 1 }));

        var channel = Assert.Single(document.Animations[0].Channels);
        Assert.Equal(ModelAnimationProperty.Translation, channel.Property);
        Assert.Equal(1, channel.BoneIndex);
    }

    [Fact]
    public void Translation_Default_UsesVertexScaleDivisorAbsolute()
    {
        // Engine fixed-point contract: anim s16 translations share the model
        // vertex unit (world x16); Decomp_GetAnimTransform copies them into
        // SMatrix.t raw. The default export therefore emits ABSOLUTE values
        // at the vertex ScaleDivisor — no bind anchoring, no extra /16.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 100),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 820));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(SkipRotation: true, SkipTranslation: false));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(100f / 36f, channel.Values[0], 3);
        Assert.Equal(820f / 36f, channel.Values[3], 3);
    }

    [Fact]
    public void Translation_LegacyBindAnchoredDelta_ReproducesOldExportPath()
    {
        // A/B diagnostic: AbsoluteTranslation=false + TranslationDivisorScale=16
        // reproduces the pre-contract export (bind + frame-0-anchored delta at
        // ScaleDivisor x16 = 576). Kept so older exports can be compared.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 100),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 820));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(
                SkipRotation: true,
                SkipTranslation: false,
                TranslationDivisorScale: 16f,
                AbsoluteTranslation: false));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(3f, channel.Values[0], 3);
        Assert.Equal(4.25f, channel.Values[3], 3);
    }

    [Fact]
    public void Translation_Default_ReplacesBindTranslation()
    {
        // Engine matrices store Tx/Ty/Tz directly and never consult bind:
        // the emitted channel must replace the bone's bind translation
        // (3,4,5) with the decoded value.
        var animation = BuildAnimation(
            frameCount: 1,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 72));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("absolute_trans", animation)],
            new PsxAnimationOptions(SkipRotation: true, SkipTranslation: false));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(2f, channel.Values[0], 3);
        Assert.Equal(0f, channel.Values[1], 3);
        Assert.Equal(0f, channel.Values[2], 3);
    }

    [Fact]
    public void Translation_EngineWorldMode_MatchesDefaultLocalPath()
    {
        // Contract equivalence: for a chained skeleton, the explicit
        // engine-world recursion (compose like Decomp_GetAnimTransform, then
        // solve back to glTF locals) must produce the same channels as the
        // default local path, because glTF's own parent chaining performs the
        // identical composition once locals are the absolute anim values.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 2, frame: 1, s16Value: AngleToS16Units(MathF.PI / 2f)),
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 108),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 108));
        var psxFile = BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f, withHierarchy: true);

        var localDocument = CreateTranslatedThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            localDocument, psxFile, skeletonIndex: 0,
            [("local", animation)],
            new PsxAnimationOptions(SkipTranslation: false));

        var worldDocument = CreateTranslatedThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            worldDocument, psxFile, skeletonIndex: 0,
            [("engine_world", animation)],
            new PsxAnimationOptions(
                SkipTranslation: false,
                EngineWorldTranslation: true));

        for (var bone = 0; bone < 3; bone++)
        {
            var localChannel = Assert.Single(
                localDocument.Animations[0].Channels,
                c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == bone);
            var worldChannel = Assert.Single(
                worldDocument.Animations[0].Channels,
                c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == bone);
            for (var frame = 0; frame < 2; frame++)
            {
                AssertVectorClose(
                    ReadVector3Frame(localChannel, frame),
                    ReadVector3Frame(worldChannel, frame));
            }
        }

        // And the shared value is the contract one: mid's local is the raw
        // anim translation at the vertex divisor, independent of frame.
        var mid = Assert.Single(
            localDocument.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);
        AssertVectorClose(new Vector3(3f, 0f, 0f), ReadVector3Frame(mid, 0));
        AssertVectorClose(new Vector3(3f, 0f, 0f), ReadVector3Frame(mid, 1));
    }

    [Fact]
    public void Translation_Default_LocalIsParentRelative_IndependentOfParentRotation()
    {
        // Locals are parent-relative by construction: a rotating parent must
        // not alter the child's emitted local translation (glTF chaining
        // applies the parent rotation at compose time, exactly like the
        // engine's (parent.rot x anim_t) >> 12 + parent.t).
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 2, frame: 1, s16Value: AngleToS16Units(MathF.PI / 2f)),
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 72),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 72));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f, withHierarchy: true),
            skeletonIndex: 0,
            [("local_trans", animation)],
            new PsxAnimationOptions(SkipTranslation: false));

        var midTranslation = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        AssertVectorClose(new Vector3(2f, 0f, 0f), ReadVector3Frame(midTranslation, 0));
        AssertVectorClose(new Vector3(2f, 0f, 0f), ReadVector3Frame(midTranslation, 1));
    }

    [Fact]
    public void Translation_ClipWithForeignHierarchy_AutoRoutesToWorldSolve()
    {
        // A clip decoded from an external bank carries the bank's parent
        // table. When it differs from the glTF skeleton's chain (here: flat
        // bank vs chained skeleton), the adapter must compose engine-world
        // through the BANK hierarchy and solve back to glTF locals — the
        // local path would chain the character's parents onto values the
        // engine never chained.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 2, frame: 1, s16Value: AngleToS16Units(MathF.PI / 2f)),
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 72),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 72));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimationClips(
            document,
            BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f, withHierarchy: true),
            skeletonIndex: 0,
            [new PsxAnimationClip("bank_clip", animation, TranslationParentIndices: [-1, -1, -1])],
            new PsxAnimationOptions(SkipTranslation: false));

        var midTranslation = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        // Flat bank hierarchy: mid's engine world stays (72,0,0)/36 = (2,0,0)
        // at both frames. Under the rotated glTF root at frame 1, the solved
        // local counter-rotates so the world position holds still.
        AssertVectorClose(new Vector3(2f, 0f, 0f), ReadVector3Frame(midTranslation, 0));
        AssertVectorClose(new Vector3(0f, 2f, 0f), ReadVector3Frame(midTranslation, 1));
    }

    [Fact]
    public void Legacy_DoesNotEmitCorrectionChannels()
    {
        // LegacyRotationChain=true reproduces the pre-fix behaviour: only bones
        // with their own rotation data get channels. Provided so users can A/B
        // compare animations during validation.
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions(LegacyRotationChain: true));

        var rotationBones = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .Select(c => c.BoneIndex)
            .ToArray();

        Assert.Equal([0], rotationBones);
    }

    [Fact]
    public void Composition_RootRotationOnly_LeafChannelUndoesParent()
    {
        // Single-rotation case: only root has Y=90°. Engine says leaf.world_rot
        // = identity. With chaining, leaf's emitted quaternion must be exactly
        // inverse(root) so root * leaf = identity.
        var rootYaw = MathF.PI / 2f;
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(rootYaw))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var (root, mid, _) = ExtractRotationsAtFrame0(document.Animations[0]);

        // Root is parentless, so it's emitted raw (just glTF-flipped).
        // Mid's correction = inverse(root_engine_local) * identity = inverse(root).
        // Verify root * mid == identity (engine_local_rot[mid] = identity).
        AssertQuaternionsClose(Quaternion.Identity, root * mid);
    }

    [Fact]
    public void Composition_ForwardParentIndex_StillEmitsDescendantCorrection()
    {
        // PSX object order is not guaranteed parent-first. Spidey-style
        // hierarchies can have a child whose parent appears later in the
        // object list. The correction mask must follow the graph, not index
        // order, otherwise placeholder children inherit too much rotation in
        // glTF and the animation bends past the engine pose.
        var rootYaw = MathF.PI / 2f;
        var animation = BuildAnimation(
            (bone: 2, channelIndex: 1, s16Value: AngleToS16Units(rootYaw))
        );

        var document = CreateForwardParentDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("idle", animation)],
            new PsxAnimationOptions());

        var byBone = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .ToDictionary(c => c.BoneIndex, ReadQuaternionFrame0);

        Assert.Equal([0, 2], byBone.Keys.OrderBy(static x => x).ToArray());
        AssertQuaternionsClose(Quaternion.Identity, byBone[2] * byBone[0]);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PsxMeshFile BuildPsxFile(
        float scaleDivisor = 1f, float translationDivisor = 1f, bool withHierarchy = false)
    {
        // withHierarchy mirrors the three-bone test skeleton (root → mid →
        // leaf) in the PSX object table, so BuildPsxEngineParentIndices sees
        // the same chain the glTF skeleton uses — as it does for real files.
        List<PsxMeshObject> objects = withHierarchy
            ?
            [
                new PsxMeshObject { ParentIndex = -1 },
                new PsxMeshObject { ParentIndex = 0 },
                new PsxMeshObject { ParentIndex = 1 }
            ]
            : [];
        return new PsxMeshFile
        {
            Version = 4,
            Objects = objects,
            Meshes = [],
            MeshNameHashes = [],
            TextureHashes = [],
            ScaleDivisor = scaleDivisor,
            TranslationDivisor = translationDivisor
        };
    }

    private static ModelDocument CreateThreeBoneDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "root", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone { Name = "mid", ParentIndex = 0 });
        skeleton.Bones.Add(new ModelBone { Name = "leaf", ParentIndex = 1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static ModelDocument CreateTranslatedThreeBoneDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "root", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "mid",
            ParentIndex = 0,
            LocalTransform = Matrix4x4.CreateTranslation(3f, 4f, 5f)
        });
        skeleton.Bones.Add(new ModelBone { Name = "leaf", ParentIndex = 1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static ModelDocument CreateForwardParentDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "child_before_parent", ParentIndex = 2 });
        skeleton.Bones.Add(new ModelBone { Name = "orphan", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone { Name = "root_after_child", ParentIndex = -1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static PsxAnimation BuildAnimation(params (int bone, int channelIndex, short s16Value)[] keys)
    {
        const int boneCount = 3;
        const int frameCount = 1;
        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        foreach (var (bone, channel, s16) in keys)
            channels[bone, channel, 0] = s16;

        return new PsxAnimation
        {
            BoneCount = boneCount,
            FrameCount = frameCount,
            Channels = channels
        };
    }

    private static PsxAnimation BuildAnimation(
        int frameCount,
        params (int bone, int channelIndex, int frame, short s16Value)[] keys)
    {
        const int boneCount = 3;
        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        foreach (var (bone, channel, frame, s16) in keys)
            channels[bone, channel, frame] = s16;

        return new PsxAnimation
        {
            BoneCount = boneCount,
            FrameCount = frameCount,
            Channels = channels
        };
    }

    /// <summary>
    ///     Converts a radian angle to PSY-Q units used by
    ///     <see cref="PsxAnimation.GetBoneRotation" /> (4096 = 360°).
    /// </summary>
    private static short AngleToS16Units(float radians)
    {
        return (short)Math.Round(radians * (4096f / (2f * MathF.PI)));
    }

    private static (Quaternion Root, Quaternion Mid, Quaternion Leaf) ExtractRotationsAtFrame0(ModelAnimation animation)
    {
        var byBone = animation.Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .ToDictionary(c => c.BoneIndex, ReadQuaternionFrame0);

        return (
            byBone.GetValueOrDefault(0, Quaternion.Identity),
            byBone.GetValueOrDefault(1, Quaternion.Identity),
            byBone.GetValueOrDefault(2, Quaternion.Identity));
    }

    private static Quaternion ReadQuaternionFrame0(ModelAnimationChannel channel)
    {
        return ReadQuaternionFrame(channel, 0);
    }

    private static Quaternion ReadQuaternionFrame(ModelAnimationChannel channel, int frame)
    {
        var offset = frame * 4;
        return new Quaternion(
            channel.Values[offset],
            channel.Values[offset + 1],
            channel.Values[offset + 2],
            channel.Values[offset + 3]);
    }

    private static Vector3 ReadVector3Frame(ModelAnimationChannel channel, int frame)
    {
        var offset = frame * 3;
        return new Vector3(
            channel.Values[offset],
            channel.Values[offset + 1],
            channel.Values[offset + 2]);
    }

    private static void AssertQuaternionsClose(Quaternion expected, Quaternion actual)
    {
        // Quaternions q and -q represent the same rotation, so accept either sign.
        var direct = Distance(expected, actual);
        var flipped = Distance(expected, Quaternion.Negate(actual));
        var min = MathF.Min(direct, flipped);
        Assert.True(
            min < AngleEpsilon,
            $"Quaternion mismatch: expected {expected}, actual {actual} (distance {min}).");
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        var distance = Vector3.Distance(expected, actual);
        Assert.True(
            distance < AngleEpsilon,
            $"Vector mismatch: expected {expected}, actual {actual} (distance {distance}).");
    }

    private static float Distance(Quaternion a, Quaternion b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        var dw = a.W - b.W;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
    }

}
