# THUG2 PC `.snd`

THUG2's Windows-only `.snd` files are RIFF/WAVE containers around a continuous,
headerless 4-bit IMA-family stream. They are not PCM despite advertising PCM in
their `fmt ` chunk.

## Container

All 788 files in the retail Windows corpus have this decoded format:

- mono signed 16-bit PCM;
- sample rates 11025, 22050, 44100, or 48000 Hz;
- `wFormatTag = 1`, `nBlockAlign = 2`, and `wBitsPerSample = 16`;
- `nAvgBytesPerSec` repurposed as the decoded byte count, not a byte rate;
- two decoded samples per packed byte, except that 253 files omit the final high
  nibble and therefore declare `4 * dataSize - 2` decoded bytes.

The RIFF size describes the decoded stream rather than the smaller packed file,
so its declared end lies beyond the physical file in all 788 cases (by at least
864 bytes). That invariant prevents an ordinary quarter-second mono PCM WAV,
whose byte-rate geometry otherwise collides with SND, from being misclassified.
Data starts at offset 44 in 722 files, offset 46
after an 18-byte `fmt ` chunk in 13, offset 512 after `JUNK` in four, and offset
1024 after `bext`/`minf`/`elmo` in 49.
The game itself never validates RIFF/WAVE magic or the RIFF size and advances
chunks by their declared size without adding RIFF word padding. Every chunk
before `data` in this corpus has an even size, so the stricter shared
`RiffWaveReader` reaches the same payload while walking real buffer bounds. It
stops at `data` because later authoring chunks can be damaged.

Under the game's exact no-padding walk, 210 files contain `smpl`: 205 declare
one loop and five declare none. The loader reads only the 36-byte sampler header
and reduces `cSampleLoops` to a boolean; it never consumes the loop start/end
record. Runtime looping therefore means “loop the whole decoded buffer” and has
no effect on the PCM bytes produced here.

## Executable provenance

The algorithm was recovered from the standalone THUG2 executable reconstructed
from the protected retail image:

- executable SHA-256:
  `F7CA9C1D0E4EED40808CE3DEC6A9DF854C0236C4916AA6D09CB1A3405D2676AE`;
- file loader: VA `0x0047C870`;
- decoder: VA `0x005F5A20..0x005F5AE2`;
- decoder byte SHA-256:
  `B4A7006E8B5794BCEDE30DA1D97405902FF8EAA6FDDFBCC286FAC005C367D2DE`;
- IMA index deltas: VA `0x0068E0C0`;
- canonical 89-entry IMA step table: VA `0x0068E0E0`.

The loader saves the original `nAvgBytesPerSec`, allocates that many output
bytes, and calls the decoder as:

```c
decode(int16_t *destination, const uint8_t *packed, decodedBytes >> 1);
```

The decoder and both tables each have one direct code reference. Its important
difference from textbook IMA is operation order: it updates the step index
*before* looking up the step used for the current sample. It also computes two
separately truncated delta terms.

```text
predictor = 0
index = 0

for sample = 0 .. decodedSampleCount - 1:
    nibble = low nibble first, then high nibble
    magnitude = nibble & 7
    index = clamp(index + [-1,-1,-1,-1,2,4,6,8][magnitude], 0, 88)
    step = imaStepTable[index]
    delta = ((step * magnitude) >> 2) + (step >> 3)
    if nibble & 8: delta = -delta
    predictor = clamp(predictor + delta, -32768, 32767)
    emit int16(predictor)
```

There are no per-block headers or resets. Predictor and index both start at
zero and carry across the complete payload.

## Independent validation

The original x86 routine was executed directly under Unicorn against a stress
stream containing every byte value and an odd final sample. Its 1,541-sample
PCM output matched the clean-room algorithm byte-for-byte; both produced
SHA-256
`494260DBC6DAB888AC14AC5CCDBF1A631FE704F53B90EA7BBDC3A2D991FD7F6A`.

Corpus validation provides a second, independent signal. There are 350 sound
names shared between the independently encoded PC `.snd` and Xbox `.pcm`
trees. After decoding both formats, median windowed normalized correlation is
0.9906 (the old textbook-order candidate scored 0.6619). All 788 Windows
files satisfy the container geometry and decode to exactly
`nAvgBytesPerSec / 2` samples: 29,186,186 packed bytes become 58,372,119
samples. All 253 odd-sample files have a zero unused high nibble.

Representative raw-s16le output hashes pin rates and container layouts:

| File | Coverage | PCM SHA-256 |
|---|---|---|
| `SloppyLanding.snd` | 11025 Hz, odd sample count | `5D574F299B37A98BC52C776E64B37AF76F23920E5A663C3086664283FECA53D5` |
| `GrindWireSpark.snd` | 22050 Hz, odd sample count | `60F0342123D3787E6ED58EAFF74782A0C467C192F1C1BF2F47E6B9A536C350D6` |
| `CarBrakeSqueal.snd` | 44100 Hz, 18-byte `fmt ` | `323CE48F5C7ED72BB254604110B1AB71216945A3200F696062E003CEFBCFA6D3` |
| `MB_HiHat_01.snd` | 44100 Hz, `JUNK` layout | `9EF1B8B4DF515B90469E49B18E1B957D501E01E08C6628582F02E32FFB8A4FC6` |
| `Bouncy_AluminumCanHit01.snd` | 44100 Hz, broadcast-WAV layout | `C957DAC9F2742F8AFE1FA661C092DFF1F0981410770C80F674ADA78E30BABC30` |
| `Bouncy_PlasticHit02.snd` | 48000 Hz | `A01E6ECE92805E9CBF87388B8000C28643AD73CE5179D08F5A78F3CA6EDD1C86` |

The shipping implementation is `Thug2PcSndCodec` plus
`Thug2PcSndDecoder`. Conversion emits ordinary mono PCM16 WAV and is routed
through the CLI, format probe, Audio Converter, and preview path.
