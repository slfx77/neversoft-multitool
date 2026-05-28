namespace NeversoftMultitool.Core.Formats.Vid1;

internal sealed class Vid1BgraPresentationFrameProvider
{
    private readonly Vid1Decoder _decoder;
    private readonly Vid1VideoFile _file;
    private readonly int _frameByteLength;
    private int _decodeIndex;
    private bool _emittedInitialReference;
    private byte[]? _heldReferenceBuffer;
    private int _heldReferenceFrameIndex = -1;
    private byte[]? _scratchBuffer;

    public Vid1BgraPresentationFrameProvider(Vid1VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _frameByteLength = file.Width * file.Height * 4;
        _decoder = new Vid1Decoder(file);
    }

    public Vid1DecodedBgraFrame? DecodeNextFrame()
    {
        while (true)
        {
            if (_decodeIndex >= _file.Frames.Count)
            {
                if (_heldReferenceFrameIndex < 0 || _heldReferenceBuffer == null)
                    return null;

                var heldBuffer = TakeHeldReferenceBuffer();
                var frameIndex = _heldReferenceFrameIndex;
                _heldReferenceFrameIndex = -1;
                return new Vid1DecodedBgraFrame(frameIndex, _file.Width, _file.Height, heldBuffer);
            }

            var frame = _file.Frames[_decodeIndex++];
            if (frame.PreambleClass == 2)
            {
                var bgra = new byte[_frameByteLength];
                _decoder.DecodeFrameToBgra(frame, bgra);
                return new Vid1DecodedBgraFrame(frame.Index, _file.Width, _file.Height, bgra);
            }

            if (!_emittedInitialReference)
            {
                var bgra = new byte[_frameByteLength];
                _decoder.DecodeFrameToBgra(frame, bgra);
                _emittedInitialReference = true;
                return new Vid1DecodedBgraFrame(frame.Index, _file.Width, _file.Height, bgra);
            }

            EnsureHeldReferenceBuffer();
            if (_heldReferenceFrameIndex < 0)
            {
                _decoder.DecodeFrameToBgra(frame, _heldReferenceBuffer);
                _heldReferenceFrameIndex = frame.Index;
                continue;
            }

            EnsureScratchBuffer();
            _decoder.DecodeFrameToBgra(frame, _scratchBuffer);

            var outputBuffer = TakeHeldReferenceBuffer();
            var outputFrameIndex = _heldReferenceFrameIndex;
            _heldReferenceBuffer = _scratchBuffer;
            _scratchBuffer = null;
            _heldReferenceFrameIndex = frame.Index;
            return new Vid1DecodedBgraFrame(outputFrameIndex, _file.Width, _file.Height, outputBuffer);
        }
    }

    internal bool TryDecodeNextFrame(Span<byte> destination, out int frameIndex)
    {
        if (destination.Length < _frameByteLength)
            throw new ArgumentException($"Destination must be at least {_frameByteLength} bytes", nameof(destination));

        while (true)
        {
            if (_decodeIndex >= _file.Frames.Count)
            {
                if (_heldReferenceFrameIndex >= 0 && _heldReferenceBuffer != null)
                {
                    _heldReferenceBuffer.AsSpan(0, _frameByteLength).CopyTo(destination);
                    frameIndex = _heldReferenceFrameIndex;
                    _heldReferenceFrameIndex = -1;
                    return true;
                }

                frameIndex = -1;
                return false;
            }

            var frame = _file.Frames[_decodeIndex++];
            if (frame.PreambleClass == 2)
            {
                _decoder.DecodeFrameToBgra(frame, destination);
                frameIndex = frame.Index;
                return true;
            }

            if (!_emittedInitialReference)
            {
                _decoder.DecodeFrameToBgra(frame, destination);
                _emittedInitialReference = true;
                frameIndex = frame.Index;
                return true;
            }

            EnsureHeldReferenceBuffer();
            EnsureScratchBuffer();
            if (_heldReferenceFrameIndex < 0)
            {
                _decoder.DecodeFrameToBgra(frame, _heldReferenceBuffer);
                _heldReferenceFrameIndex = frame.Index;
                continue;
            }

            _decoder.DecodeFrameToBgra(frame, _scratchBuffer);
            _heldReferenceBuffer.AsSpan(0, _frameByteLength).CopyTo(destination);

            (_heldReferenceBuffer, _scratchBuffer) = (_scratchBuffer, _heldReferenceBuffer);
            frameIndex = _heldReferenceFrameIndex;
            _heldReferenceFrameIndex = frame.Index;
            return true;
        }
    }

    public void Reset()
    {
        _decoder.Reset();
        _heldReferenceFrameIndex = -1;
        _emittedInitialReference = false;
        _decodeIndex = 0;
    }

    private void EnsureHeldReferenceBuffer()
    {
        _heldReferenceBuffer ??= new byte[_frameByteLength];
    }

    private void EnsureScratchBuffer()
    {
        _scratchBuffer ??= new byte[_frameByteLength];
    }

    private byte[] TakeHeldReferenceBuffer()
    {
        var buffer = _heldReferenceBuffer;
        _heldReferenceBuffer = null;
        return buffer ?? throw new InvalidOperationException("Held reference buffer was not available.");
    }
}
