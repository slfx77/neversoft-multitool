namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Emits frames in PRESENTATION order (B-frames immediately, reference
///     frames held one step for reorder) as BGRA buffers.
///     With <c>enableSeekAnchors</c>, it also supports seeking: reference-state
///     snapshots are captured every N emissions while decoding advances, and
///     <see cref="SeekToEmissionOrdinal" /> resumes from the best of {current
///     position, nearest anchor, nearest intra frame, stream start}, then
///     fast-forwards with plane-only decodes (no BGRA conversion). The held
///     presentation frame's BGRA is regenerated lazily from the reference
///     planes — the held frame is always the last promoted reference, because
///     B-frames never promote.
/// </summary>
internal sealed class Vid1BgraPresentationFrameProvider
{
    private readonly Vid1SeekAnchorIndex? _anchors;
    private readonly Vid1Decoder _decoder;
    private readonly Vid1VideoFile _file;
    private readonly int _frameByteLength;
    private int _decodeIndex;
    private int _emissionOrdinal;
    private bool _emittedInitialReference;
    private bool _heldBgraStale;
    private byte[]? _heldReferenceBuffer;
    private int _heldReferenceFrameIndex = -1;
    private int[]? _intraFramePositions;
    private byte[]? _scratchBuffer;

    public Vid1BgraPresentationFrameProvider(Vid1VideoFile file, bool enableSeekAnchors = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _frameByteLength = file.Width * file.Height * 4;
        _decoder = new Vid1Decoder(file);
        _anchors = enableSeekAnchors ? new Vid1SeekAnchorIndex() : null;
    }

    public Vid1DecodedBgraFrame? DecodeNextFrame()
    {
        RegenerateHeldBgraIfStale();

        while (true)
        {
            if (_decodeIndex >= _file.Frames.Count)
            {
                if (_heldReferenceFrameIndex < 0 || _heldReferenceBuffer == null)
                    return null;

                var heldBuffer = TakeHeldReferenceBuffer();
                var frameIndex = _heldReferenceFrameIndex;
                _heldReferenceFrameIndex = -1;
                _emissionOrdinal++;
                return new Vid1DecodedBgraFrame(frameIndex, _file.Width, _file.Height, heldBuffer);
            }

            var frame = _file.Frames[_decodeIndex++];
            if (frame.PreambleClass == 2)
            {
                var bgra = new byte[_frameByteLength];
                _decoder.DecodeFrameToBgra(frame, bgra);
                _emissionOrdinal++;
                return new Vid1DecodedBgraFrame(frame.Index, _file.Width, _file.Height, bgra);
            }

            if (!_emittedInitialReference)
            {
                var bgra = new byte[_frameByteLength];
                _decoder.DecodeFrameToBgra(frame, bgra);
                _emittedInitialReference = true;
                _emissionOrdinal++;
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
            _emissionOrdinal++;
            return new Vid1DecodedBgraFrame(outputFrameIndex, _file.Width, _file.Height, outputBuffer);
        }
    }

    internal bool TryDecodeNextFrame(Span<byte> destination, out int frameIndex)
    {
        if (destination.Length < _frameByteLength)
            throw new ArgumentException($"Destination must be at least {_frameByteLength} bytes", nameof(destination));

        RegenerateHeldBgraIfStale();

        while (true)
        {
            if (_decodeIndex >= _file.Frames.Count)
            {
                if (_heldReferenceFrameIndex >= 0 && _heldReferenceBuffer != null)
                {
                    _heldReferenceBuffer.AsSpan(0, _frameByteLength).CopyTo(destination);
                    frameIndex = _heldReferenceFrameIndex;
                    _heldReferenceFrameIndex = -1;
                    CompleteEmission();
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
                CompleteEmission();
                return true;
            }

            if (!_emittedInitialReference)
            {
                _decoder.DecodeFrameToBgra(frame, destination);
                _emittedInitialReference = true;
                frameIndex = frame.Index;
                CompleteEmission();
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
            CompleteEmission();
            return true;
        }
    }

    public void Reset()
    {
        _decoder.Reset();
        _heldReferenceFrameIndex = -1;
        _emittedInitialReference = false;
        _decodeIndex = 0;
        _emissionOrdinal = 0;
        _heldBgraStale = false;
    }

    /// <summary>
    ///     Repositions so the NEXT emitted frame has the given presentation
    ///     ordinal. Returns the ordinal actually resumed at (== target unless
    ///     cancelled early or past end-of-stream, which returns -1).
    ///     <paramref name="isCancelled" /> is polled between frames so a newer
    ///     seek can supersede a scan in progress.
    /// </summary>
    internal int SeekToEmissionOrdinal(int target, Func<bool>? isCancelled = null)
    {
        target = Math.Max(0, target);

        // Pick the cheapest valid starting point at or below the target.
        var bestStart = _emissionOrdinal <= target ? _emissionOrdinal : -1;

        var anchor = _anchors?.FindBestAtOrBelow(target);
        if (anchor != null && anchor.NextEmissionOrdinal > bestStart)
        {
            RestoreAnchor(anchor);
            bestStart = anchor.NextEmissionOrdinal;
        }

        // Intra frames are self-contained restart points. Emission ordinals
        // and container indices differ by at most the one-frame reorder hold,
        // so treating the container position as the resume ordinal costs at
        // most a single-frame timestamp offset — cheaper than decoding from a
        // much earlier anchor. Only used when it beats everything else.
        var intra = FindIntraAtOrBelow(target);
        if (intra > bestStart)
        {
            RestartAtIntraFrame(intra);
            bestStart = intra;
        }

        if (bestStart < 0)
        {
            Reset();
        }

        while (_emissionOrdinal < target)
        {
            if (isCancelled?.Invoke() == true)
                break;

            if (!TryAdvancePresentationOnly())
            {
                RegenerateHeldBgraIfStale();
                return -1;
            }
        }

        RegenerateHeldBgraIfStale();
        return _emissionOrdinal;
    }

    /// <summary>
    ///     One presentation-order step without BGRA conversion — same held /
    ///     initial-reference bookkeeping as <see cref="TryDecodeNextFrame" />,
    ///     but the emitted pixels are discarded so only plane decodes run.
    /// </summary>
    private bool TryAdvancePresentationOnly()
    {
        while (true)
        {
            if (_decodeIndex >= _file.Frames.Count)
            {
                if (_heldReferenceFrameIndex < 0)
                    return false;

                _heldReferenceFrameIndex = -1;
                CompleteEmission();
                return true;
            }

            var frame = _file.Frames[_decodeIndex++];
            if (frame.PreambleClass == 2 || !_emittedInitialReference)
            {
                _decoder.DecodeFrameToPlanesOnly(frame);
                if (frame.PreambleClass != 2)
                    _emittedInitialReference = true;
                CompleteEmission();
                return true;
            }

            if (_heldReferenceFrameIndex < 0)
            {
                _decoder.DecodeFrameToPlanesOnly(frame);
                _heldReferenceFrameIndex = frame.Index;
                _heldBgraStale = true;
                continue;
            }

            _decoder.DecodeFrameToPlanesOnly(frame);
            _heldReferenceFrameIndex = frame.Index;
            _heldBgraStale = true;
            CompleteEmission();
            return true;
        }
    }

    private void CompleteEmission()
    {
        _emissionOrdinal++;
        if (_anchors == null || !_anchors.ShouldCapture(_emissionOrdinal))
            return;

        _anchors.Add(new Vid1SeekAnchor
        {
            DecodeIndex = _decodeIndex,
            EmittedInitialReference = _emittedInitialReference,
            HeldReferenceFrameIndex = _heldReferenceFrameIndex,
            NextEmissionOrdinal = _emissionOrdinal,
            State = _decoder.CaptureReferenceState()
        });
    }

    private void RestoreAnchor(Vid1SeekAnchor anchor)
    {
        _decoder.RestoreReferenceState(anchor.State);
        _decodeIndex = anchor.DecodeIndex;
        _emittedInitialReference = anchor.EmittedInitialReference;
        _heldReferenceFrameIndex = anchor.HeldReferenceFrameIndex;
        _emissionOrdinal = anchor.NextEmissionOrdinal;
        _heldBgraStale = _heldReferenceFrameIndex >= 0;
    }

    private void RestartAtIntraFrame(int containerPosition)
    {
        _decoder.Reset();
        _decodeIndex = containerPosition;
        _emittedInitialReference = false;
        _heldReferenceFrameIndex = -1;
        _emissionOrdinal = containerPosition;
        _heldBgraStale = false;
    }

    /// <summary>Largest intra-frame (PreambleClass 0) container position ≤ target, or -1.</summary>
    private int FindIntraAtOrBelow(int target)
    {
        if (_anchors == null)
            return -1;

        _intraFramePositions ??= BuildIntraFrameIndex();
        var best = -1;
        foreach (var position in _intraFramePositions)
        {
            if (position > target)
                break;

            best = position;
        }

        return best;
    }

    private int[] BuildIntraFrameIndex()
    {
        var positions = new List<int>();
        for (var i = 0; i < _file.Frames.Count; i++)
        {
            if (_file.Frames[i].PreambleClass == 0)
                positions.Add(i);
        }

        return [.. positions];
    }

    private void RegenerateHeldBgraIfStale()
    {
        if (!_heldBgraStale)
            return;

        _heldBgraStale = false;
        if (_heldReferenceFrameIndex < 0)
            return;

        EnsureHeldReferenceBuffer();
        _decoder.ConvertReferencePlanesToBgra(_heldReferenceBuffer);
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
