namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal sealed class Vu1Memory
{
    public const int SizeQwords = 1024;

    private readonly Vu1Qword[] _words = new Vu1Qword[SizeQwords];
    private readonly bool[] _written = new bool[SizeQwords];

    public void WriteQword(int address, uint x, uint y, uint z, uint w)
    {
        var wrapped = Wrap(address);
        _words[wrapped] = new Vu1Qword(x, y, z, w);
        _written[wrapped] = true;
    }

    public Vu1Qword ReadQword(int address)
    {
        return _words[Wrap(address)];
    }

    public bool IsWritten(int address)
    {
        return _written[Wrap(address)];
    }

    public Vu1Qword[] ReadWindow(int startAddress, int length)
    {
        var window = new Vu1Qword[length];
        for (var i = 0; i < length; i++)
            window[i] = ReadQword(startAddress + i);
        return window;
    }

    private static int Wrap(int address)
    {
        var wrapped = address % SizeQwords;
        return wrapped < 0 ? wrapped + SizeQwords : wrapped;
    }
}
