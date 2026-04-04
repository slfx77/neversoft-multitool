namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal readonly record struct VifReplayRegisters(
    byte Cl,
    byte Wl,
    int Base,
    int Offset,
    int Tops,
    int Top,
    int Dbf,
    int Itop,
    int Stmod,
    uint Stmask);
