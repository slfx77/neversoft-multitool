namespace NeversoftMultitool.Core.Formats.Mesh;

public delegate byte[]? MeshChecksumTextureResolver(uint textureChecksum);

public delegate byte[]? Ps2TexaTextureResolver(uint textureChecksum, ulong texa);

public delegate uint Ps2Tex0ChecksumResolver(ulong dmaTex0, uint groupChecksum);

public delegate byte[]? MeshNamedTextureResolver(string textureName);
