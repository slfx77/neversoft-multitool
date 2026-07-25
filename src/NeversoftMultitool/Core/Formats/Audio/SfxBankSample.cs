namespace NeversoftMultitool.Core.Formats.Audio;

internal sealed record SfxBankSample(int ExternalIndex, int DataSize, int SampleRate, int Channels, string Encoding);
