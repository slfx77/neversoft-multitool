using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

/// <summary>An exact SFX cue sheet owned by one visible KAT or VAB row.</summary>
internal sealed record AudioCueSheetBinding(
    string FileName,
    string RelativePath,
    AssetSource Source);
