namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Resolves the object-table matrix and skin joint used by an N64
///     render-bank corner. Static rendering and the one exact flat-map profile
///     use the established placement-relative expression
///     (<c>placing object + G_MTX</c>); all other admitted animation uses the
///     recovered global-joint interpretation. The plan also owns the raw
///     render-vertex scale so binding and scale cannot diverge.
/// </summary>
internal readonly record struct N64GeometryBindingPlan
{
    private N64GeometryBindingPlan(
        int objectCount,
        N64GeometryBindingMode mode,
        float vertexScaleFactor)
    {
        if (objectCount < 0)
            throw new ArgumentOutOfRangeException(nameof(objectCount));
        if (!float.IsFinite(vertexScaleFactor) || vertexScaleFactor <= 0f)
            throw new ArgumentOutOfRangeException(nameof(vertexScaleFactor));

        ObjectCount = objectCount;
        Mode = mode;
        VertexScaleFactor = vertexScaleFactor;
    }

    public int ObjectCount { get; }
    public N64GeometryBindingMode Mode { get; }
    public float VertexScaleFactor { get; }
    public bool IsSkinned => Mode is not N64GeometryBindingMode.StaticRelative;
    private bool IsGlobal => Mode is N64GeometryBindingMode.AnimatedGlobal;

    public static N64GeometryBindingPlan StaticRelative(
        int objectCount,
        float vertexScaleFactor)
    {
        return new N64GeometryBindingPlan(
            objectCount, N64GeometryBindingMode.StaticRelative, vertexScaleFactor);
    }

    public static N64GeometryBindingPlan AnimatedGlobal(
        int objectCount,
        float vertexScaleFactor)
    {
        return new N64GeometryBindingPlan(
            objectCount, N64GeometryBindingMode.AnimatedGlobal, vertexScaleFactor);
    }

    public static N64GeometryBindingPlan AnimatedRelative(
        int objectCount,
        float vertexScaleFactor)
    {
        return new N64GeometryBindingPlan(
            objectCount, N64GeometryBindingMode.AnimatedRelative, vertexScaleFactor);
    }

    /// <summary>
    ///     Resolves the object whose bind offset the corner receives. Returning
    ///     false preserves the historical static fallback to the origin; an
    ///     animated plan is never published unless every corner resolves.
    /// </summary>
    public bool TryResolveOffsetObjectIndex(
        int placingObjectIndex,
        int matrixIndex,
        out int offsetObjectIndex)
    {
        var candidate = IsGlobal
            ? matrixIndex
            : (long)placingObjectIndex + matrixIndex;
        if (candidate < 0 || candidate >= ObjectCount)
        {
            offsetObjectIndex = -1;
            return false;
        }

        offsetObjectIndex = (int)candidate;
        return true;
    }

    public int ResolveOffsetObjectIndexOrDefault(int placingObjectIndex, int matrixIndex)
    {
        return TryResolveOffsetObjectIndex(placingObjectIndex, matrixIndex, out var resolved)
            ? resolved
            : -1;
    }

    public int ResolveSkinJoint(int placingObjectIndex, int matrixIndex)
    {
        if (!IsSkinned)
            throw new InvalidOperationException("N64 corner is not covered by an animated binding plan.");
        if (!TryResolveOffsetObjectIndex(placingObjectIndex, matrixIndex, out var jointIndex))
            throw new InvalidOperationException("N64 corner is outside the animated binding plan.");
        return jointIndex;
    }
}

internal enum N64GeometryBindingMode
{
    StaticRelative,
    AnimatedGlobal,
    AnimatedRelative
}
