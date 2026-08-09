# THAW GameCube M4Decoder — Function Inventory

Derived from headless Ghidra decompilation of
`Tony Hawk's American Wasteland/System/main.dol` (Factor 5 `f5vid` engine,
compile tag `"f5vid:id:May 27 2003 13:40:20"`).

The transient build-specific Ghidra project and bulk decompiler output were not
retained. This promoted inventory records the durable function addresses,
roles, call relationships, and porting conclusions.

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

## Key primitives (ordered by port priority)

### Bit reader

`FUN_802A0834` — the universal MPEG bit-reader. Shared across VLCs and the
residual pipeline (called by 5 of the 7 anchor functions).

State layout (one `uint[4]`):
- `[0]` current 32-bit word
- `[1]` next 32-bit word (prefetched)
- `[2]` current bit position (0..31)
- `[3]` pointer into source buffer (advances 4 bytes when word exhausted)

Signature: `uint read_bits(uint* state, int n_bits)`. Straight port of what
we already have in `dump_vid1_coeffs.BitReader`, but confirms the
word-boundary handling and the prefetch convention.

### VLC decoders

| Addr | Role | Size |
|------|------|------|
| `0x8029CDA0` | selector (motion / a878 branch) | 104 B |
| `0x8029CEE0` | raw-code (control prefix) | 108 B |
| `0x8029CFA4` | MV delta | 148 B |
| `0x802A07AC` | bit reader wrapper (called by cda0/cee0) | 72 B |
| `0x802A07F4` | bit reader wrapper (called by cda0/cee0/a878) | 64 B |

Tiny leaf functions — each is a VLC table lookup + shift. Port alongside
the bit reader as a set.

### Control-prefix parsers

| Addr | Role | Callees |
|------|------|---------|
| `0x80299A38` | class-3 motion control prefix (99A38 in diagnostics) | cda0, cee0, a878 |
| `0x802998F8` | special-16 / 998f8 control prefix | cda0, cee0, a878 |
| `0x8029A878` | A878 residual block pipeline (see above) | 12 — full block loop |

These match the bitstream probes we already have in
[`tools/validation/video/dump_vid1_coeffs.py`](../../tools/validation/video/dump_vid1_coeffs.py)
(`probe_caller_control_99a38_from_reader`,
`probe_caller_control_998f8_from_reader`).

### Pixel path (core port target)

| Addr | Role | Size | Notes |
|------|------|------|-------|
| `0x802A08B4` | **coefficient decode** | 688 B | VLC inverse-zigzag unpack, 14 shifts + 9 muls, 3 callees |
| `0x8029D494` | post-decode reorder | 264 B | likely inverse-scan swizzle or block prep for dequant |
| `0x802A01E4` | **dequant (inter)** | 164 B | small leaf; simple multiply by per-block quant |
| `0x802A0304` | **dequant (intra)** | 160 B | small leaf; extra arg `param_1 + 0x58` — DC predictor? |
| `0x8029E8A0` | **IDCT 8x8** | 876 B | **floating-point** Chen-Wang style; 48 muls, 8 cosine constants (`dVar50..dVar56`), no function calls |
| `0x8029EC34` | **motion compensation (regular)** | 180 B | called at end of a878 for P-frames |
| `0x8029F7B8` | **motion compensation (GMC)** | 176 B | called at end of a878 when `param_1+0x84 != 0` and CBP flag 4 set |

**Note on IDCT**: Factor 5 used double-precision floating-point IDCT on
Gekko, leveraging the PPC750 double-FP path. Typical PPC-PSN `0x3ff00000...`
idiom converts `short` to `double` by OR-ing into the exponent bits of 1.0.
We won't match bit-exact against integer-IDCT references (FFmpeg, reference
C), but we will match this engine's PS2/PC output if they also use FP.

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

These aren't blocking the port — read them when their callers are being
translated.

## What this unlocks for the native C# port

The full pixel path is now known. For the Vid1 decoder's
`DecodePixelFrame()` equivalent, port these in order:

1. **Bit reader** + VLC lookups — straightforward 1:1 from `FUN_802A0834`
   and the 5 tiny VLC functions.
2. **Control-prefix parsers** — mostly already done in Python diagnostics;
   port those to C# and validate against the Ghidra output.
3. **Per-block pipeline** (the 7-step sequence inside `FUN_8029A878`) —
   this is the heart of the port. Implement each step in the order above,
   unit-test against a single macroblock's PS2/PC reference.
4. **Motion compensation** — `FUN_8029EC34` first, then GMC
   (`FUN_8029F7B8`) for class-3 frames.
5. **Top-level `DecodeFrame`** — `FUN_80299DC0` orchestrates macroblock
   iteration; wire it up last.

With this pipeline implemented faithfully, the single-frame MAE against
PS2 references should drop from the current ~65 (ffmpeg-transcode error
frames) toward the ~17.77 PS2-vs-PC baseline.

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
