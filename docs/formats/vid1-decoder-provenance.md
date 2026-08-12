# THAW GameCube M4Decoder — Function Inventory

Derived from headless Ghidra decompilation of
`Tony Hawk's American Wasteland/System/main.dol` (Factor 5 `f5vid` engine,
compile tag `"f5vid:id:May 27 2003 13:40:20"`).

The transient build-specific Ghidra project and bulk decompiler output were not
retained. This promoted inventory records the durable function addresses,
roles, call relationships, porting conclusions, and current C# mapping.

## Pipeline summary

Each decoded video frame runs this pipeline:

```
FUN_80299dc0 (DecodeFrame)
  └── parse VOP / control prefix
  └── per macroblock:
        └── FUN_80299A38 (motion control)  or  FUN_802998F8 (special-16)
              └── VLCs: FUN_8029CDA0 / FUN_8029CEE0 / FUN_8029CFA4
              └── bit reader: FUN_802A0834
  └── per block (FUN_8029A878 runs 6× per MB: 4 luma + 2 chroma):
        1. setup/unpack        FUN_802A044C
        2. CBP decode          FUN_8029C214  (+ FUN_8029CE08 aux)
        3. coefficient decode  FUN_802A08B4   ← VLC + inverse-zigzag
        4. post-decode reorder FUN_8029D494
        5. dequantize          FUN_802A01E4 (inter)  or  FUN_802A0304 (intra)
        6. IDCT 8x8            FUN_8029E8A0   ← FP Chen-Wang style
  └── motion compensation (whole MB, end of block loop):
        FUN_8029EC34 (regular P-frame)  or  FUN_8029F7B8 (GMC sprite warp)
```

## Key primitives and current C# mapping

### Bit reader

`FUN_802A0834` — the universal MPEG bit-reader. Shared across VLCs and the
residual pipeline (called by 5 of the 7 anchor functions).

State layout (one `uint[4]`):
- `[0]` current 32-bit word
- `[1]` next 32-bit word (prefetched)
- `[2]` current bit position (0..31)
- `[3]` pointer into source buffer (advances 4 bytes when word exhausted)

Signature: `uint read_bits(uint* state, int n_bits)`. The shipped counterpart
is `Vid1BitReader`; the Python `dump_vid1_coeffs.BitReader` remains a diagnostic
cross-check. The C# reader preserves the observed MSB-first behavior without
claiming an in-memory replica of the DOL's four-word state.

### VLC decoders

| Addr | Role | Size |
|------|------|------|
| `0x8029CDA0` | selector (motion / a878 branch) | 104 B |
| `0x8029CEE0` | raw-code (control prefix) | 108 B |
| `0x8029CFA4` | MV delta | 148 B |
| `0x802A07AC` | bit reader wrapper (called by cda0/cee0) | 72 B |
| `0x802A07F4` | bit reader wrapper (called by cda0/cee0/a878) | 64 B |

Tiny leaf functions — each is a VLC table lookup + shift. Their control and
motion-vector counterparts ship in `Vid1VlcDecoder`; residual run/level VLCs
ship in `Vid1CoefficientDecoder`.

### Control-prefix parsers

| Addr | Role | Callees |
|------|------|---------|
| `0x80299A38` | class-3 motion control prefix (99A38 in diagnostics) | cda0, cee0, a878 |
| `0x802998F8` | special-16 / 998f8 control prefix | cda0, cee0, a878 |
| `0x8029A878` | A878 residual block pipeline (see above) | 12 — full block loop |

These match the bitstream probes retained in
[`tools/validation/video/dump_vid1_coeffs.py`](../../tools/validation/video/dump_vid1_coeffs.py)
(`probe_caller_control_99a38_from_reader`,
`probe_caller_control_998f8_from_reader`). The active C# dispatch lives in
`Vid1ControlPrefix`, `Vid1MacroblockDecoder`, and `Vid1Decoder`.

### Pixel path

| Addr | Role | Size | Notes |
|------|------|------|-------|
| `0x802A08B4` | **coefficient decode** | 688 B | VLC inverse-zigzag unpack, 14 shifts + 9 muls, 3 callees |
| `0x8029D494` | post-decode reorder | 264 B | likely inverse-scan swizzle or block prep for dequant |
| `0x802A01E4` | **dequant (inter)** | 164 B | small leaf; simple multiply by per-block quant |
| `0x802A0304` | **dequant (intra)** | 160 B | small leaf; extra arg `param_1 + 0x58` — DC predictor? |
| `0x8029E8A0` | **IDCT 8x8** | 876 B | **floating-point** Chen-Wang style; 48 muls, 8 cosine constants (`dVar50..dVar56`), no function calls |
| `0x8029EC34` | **motion compensation (regular)** | 180 B | called at end of a878 for P-frames |
| `0x8029F7B8` | **motion compensation (GMC)** | 176 B | called at end of a878 when `param_1+0x84 != 0` and CBP flag 4 set |

The corresponding C# implementations are `Vid1CoefficientDecoder`,
`Vid1Prediction`, `Vid1Dequant`, `Vid1Idct`, `Vid1MotionComp`, and
`Vid1SpriteWarp`, orchestrated by `Vid1Decoder` and
`Vid1MacroblockDecoder`.

**Note on IDCT**: Factor 5 used double-precision floating-point IDCT on
Gekko, leveraging the PPC750 double-FP path. Typical PPC-PSN `0x3ff00000...`
idiom converts `short` to `double` by OR-ing into the exponent bits of 1.0.
`Vid1Idct` carries the DOL constants and rounding behavior. That structural
match does not by itself prove bit-exact output against the GameCube decoder,
an integer-IDCT reference, or another platform's build.

## Remaining unknowns

- `FUN_802A044C` (832 B, no shifts, 5 muls) — per-block setup. Likely
  builds the quantizer matrix or DC predictor context.
- `FUN_802A1874` (1632 B, **56 array accesses**, no callees) — called by
  both top-level and motion. Likely large **coefficient table zeroing /
  frame buffer clear**.
- `FUN_802B3BBC` (300 B, distant addr cluster) — called only by top-level.
  Likely a frame-buffer flip / output surface finalize.
- `FUN_8029DA68`, `FUN_8029DA9C` — small helpers called from setup /
  teardown; probably quant-table setup and context-reset.

These identities remain hypotheses. The C# decoder implements the required
state setup and frame reconstruction directly, but it does not establish a
one-to-one mapping for these helpers. They therefore remain reverse-engineering
follow-ups, not proof that the native port is bit-exact.

## Native C# implementation status

The former port-order checklist is complete: `Vid1Decoder` now drives the
control-prefix, coefficient, prediction, dequantization, IDCT, motion, sprite,
and reference-frame paths. `Vid1BFrameDecoder` handles the supported class-2
direct, forward, backward, and bidirectional modes. `Vid1VideoConverter`
streams the native decoder's RGB output to ffmpeg for MP4 encoding and muxing;
ffmpeg is not used to decode VID1 frames. This is the shipped path documented
in the README and format backlog.

Focused tests cover the bit reader, control/VLC primitives, dequantization,
IDCT, motion compensation, canonical `intro.vid`/`atvi.vid`/`credits.vid`
decoding behavior, presentation-order and seek consistency, current-output
regression hashes, and an `intro.vid` MP4 conversion smoke test. Those tests
pin the current implementation; the regression hashes and successful
conversion are not a native-render pixel-fidelity oracle.

The remaining decoder limitations are explicit:

- no retained GameCube framebuffer capture or equivalent oracle currently
  proves pixel-exact output, so the historical MAE estimates are not a current
  acceptance gate;
- malformed or still-unmodelled macroblocks can take bounded bit-resynchronization,
  neutral-plane, or reference-copy recovery paths, reported through
  `Vid1FrameDecodeStats`;
- class-2 field/GMC controls remain unsupported in `Vid1BFrameDecoder`, and
  the non-class-2 field-prediction path remains opt-in pending score validation;
- the exact pre-GMC feature-bit enable condition in `FUN_8029A878` is not yet
  modelled; and
- the helper identities listed under Remaining unknowns are still unproven.

## Reproducing targeted analysis

Import `System/main.dol` into a fresh Ghidra project, then use the generic
[`DecompileFunctionsByAddress.java`](../../tools/reverse-engineering/ghidra/DecompileFunctionsByAddress.java)
helper with the addresses listed above. Use
[`DumpFunctionCallEdgesByAddress.java`](../../tools/reverse-engineering/ghidra/DumpFunctionCallEdgesByAddress.java)
for the call graph and
[`DumpInstructionsByAddress.java`](../../tools/reverse-engineering/ghidra/DumpInstructionsByAddress.java)
when decompiler output obscures PPC instruction details. These helpers accept
caller-supplied addresses and output paths and do not depend on a saved
build-specific project.
