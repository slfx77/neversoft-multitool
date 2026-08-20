namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Activation gates a THAW zone NodeArray authors on one LevelGeometry
///     sector. The engine deactivates any sector whose node lacks
///     <c>CreatedAtStart</c> at load (THUG <c>cfuncs.cpp:6276-6281</c>
///     <c>SetActive(false)</c>); <c>createdfromvariable</c> names the story
///     state (NODEFLAG_*) whose script re-activates it, and
///     <c>createdfromtod</c> names the time-of-day group
///     (TOD_{Morning|Afternoon|Evening|Night}{On|Off}_NN). The two gate kinds
///     are mutually exclusive with CreatedAtStart in the shipped corpus.
/// </summary>
public sealed record Ps2WorldzoneNodeGates(
    bool CreatedAtStart,
    uint CreatedFromVariable,
    uint CreatedFromTod,
    bool AbsentInNetGames);
