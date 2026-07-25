namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>One 16-byte cue record from the .SFX table (fields per SFX_ParseSFXFile).</summary>
internal sealed record SfxCue(
    int CueIndex,
    bool Loop,
    int Program,
    int Category,
    int Note,
    int Pitch,
    int Volume,
    int Alias);
