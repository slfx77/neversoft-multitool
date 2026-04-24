namespace NeversoftMultitool.Core.Formats.Video;

internal sealed class Vid1PresentationFrameProvider
{
    private readonly Vid1VideoFile _file;
    private readonly Vid1Decoder _decoder;
    private readonly Queue<Vid1DecodedFrame> _readyFrames = [];
    private int _decodeIndex;
    private bool _emittedInitialReference;
    private Vid1DecodedFrame? _heldReferenceFrame;

    public Vid1PresentationFrameProvider(Vid1VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _decoder = new Vid1Decoder(file);
    }

    public Vid1FrameDecodeStats LastFrameStats => _decoder.LastFrameStats;

    public Vid1DecodedFrame? DecodeNextFrame()
    {
        while (_readyFrames.Count == 0)
        {
            if (!DecodeNextInputFrame())
                break;
        }

        return _readyFrames.Count == 0 ? null : _readyFrames.Dequeue();
    }

    public void Reset()
    {
        _decoder.Reset();
        _readyFrames.Clear();
        _heldReferenceFrame = null;
        _emittedInitialReference = false;
        _decodeIndex = 0;
    }

    private bool DecodeNextInputFrame()
    {
        if (_decodeIndex >= _file.Frames.Count)
        {
            FlushHeldReference();
            return false;
        }

        var frame = _file.Frames[_decodeIndex++];
        var decoded = _decoder.DecodeFrame(frame);
        EnqueuePresentationFrame(frame, decoded);
        return true;
    }

    private void EnqueuePresentationFrame(Vid1VideoFrame sourceFrame, Vid1DecodedFrame decoded)
    {
        if (sourceFrame.PreambleClass == 2)
        {
            _readyFrames.Enqueue(decoded);
            return;
        }

        if (!_emittedInitialReference)
        {
            _readyFrames.Enqueue(decoded);
            _emittedInitialReference = true;
            return;
        }

        if (_heldReferenceFrame == null)
        {
            _heldReferenceFrame = decoded;
            return;
        }

        FlushHeldReference();
        _heldReferenceFrame = decoded;
    }

    private void FlushHeldReference()
    {
        if (_heldReferenceFrame == null)
            return;

        _readyFrames.Enqueue(_heldReferenceFrame);
        _heldReferenceFrame = null;
    }
}

internal sealed class Vid1BgraPresentationFrameProvider
{
    private readonly Vid1VideoFile _file;
    private readonly Vid1Decoder _decoder;
    private readonly Queue<Vid1DecodedBgraFrame> _readyFrames = [];
    private int _decodeIndex;
    private bool _emittedInitialReference;
    private Vid1DecodedBgraFrame? _heldReferenceFrame;

    public Vid1BgraPresentationFrameProvider(Vid1VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _decoder = new Vid1Decoder(file);
    }

    public Vid1DecodedBgraFrame? DecodeNextFrame()
    {
        while (_readyFrames.Count == 0)
        {
            if (!DecodeNextInputFrame())
                break;
        }

        return _readyFrames.Count == 0 ? null : _readyFrames.Dequeue();
    }

    public void Reset()
    {
        _decoder.Reset();
        _readyFrames.Clear();
        _heldReferenceFrame = null;
        _emittedInitialReference = false;
        _decodeIndex = 0;
    }

    private bool DecodeNextInputFrame()
    {
        if (_decodeIndex >= _file.Frames.Count)
        {
            FlushHeldReference();
            return false;
        }

        var frame = _file.Frames[_decodeIndex++];
        var decoded = _decoder.DecodeFrameToBgraFrame(frame);
        EnqueuePresentationFrame(frame, decoded);
        return true;
    }

    private void EnqueuePresentationFrame(Vid1VideoFrame sourceFrame, Vid1DecodedBgraFrame decoded)
    {
        if (sourceFrame.PreambleClass == 2)
        {
            _readyFrames.Enqueue(decoded);
            return;
        }

        if (!_emittedInitialReference)
        {
            _readyFrames.Enqueue(decoded);
            _emittedInitialReference = true;
            return;
        }

        if (_heldReferenceFrame == null)
        {
            _heldReferenceFrame = decoded;
            return;
        }

        FlushHeldReference();
        _heldReferenceFrame = decoded;
    }

    private void FlushHeldReference()
    {
        if (_heldReferenceFrame == null)
            return;

        _readyFrames.Enqueue(_heldReferenceFrame);
        _heldReferenceFrame = null;
    }
}
