# Backlog — Tony Hawk's Proving Ground + Project 8 (PS2)

Created 2026-07-03; **re-investigated 2026-07-03** (render sweep, not just triangle counts). See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Games:** Tony Hawk's Project 8 (2006-9-21, PS2 — Final), Tony Hawk's Proving Ground (2007-9-3, PS2 — Final). Sample builds present under `Sample/Builds/`.

**Depends on / cross-ref:** `memory/thaw_ps2_skin_format.md` (THAW `.skin.ps2` VIF/DMA strip decode), `ThawPs2SkinFile.cs`, `ThawPs2SkinVifLayout.cs`.

> **Correction (2026-07-03).** The first pass of this file claimed *both* games' character skins garble — that was extrapolated from triangle counts **without rendering P8**. On actually rendering: **Project 8 skins render correctly; only Proving Ground (2007) skins garble.** P8 uses the THAW-compatible VIF/DMA encoding our decoder already handles; THPG re-encoded it. (Same over-generalization pattern as the `README.md` "tested games" error — render before claiming.)

---

## Project 8 — WORKS (🟢 verified 2026-07-03)

Rendered clean via `mesh` + `glb-render` at HEAD: `gped_bam` `(1,9,9)`, `gped_dustindollin` `(1,8,8)`, `gped_anchorman` `(2,3,3)` jacket, `gped_chalk`, `gped_colonel` — all complete, correct characters. Textures (`.tex.ps2`/`.img.ps2`) decode correctly. **No mesh work needed for P8 character skins.** (Other P8 families — `.mdl.ps2`, worldzones, `.ska`, `.col` — not yet swept; see below.)

---

## Remaining — needs work

### 🔶 Proving Ground (THPG) character skins garble — VIF vertex re-encoding
- Source: this session (2026-07-03).
- **Controlled repro** (the key lever): `gped_bam.skin.ps2` ships in **both** games. Same header `(1,9,9)` = THAW pre-compiled skin (`numObjects=1, meshes=9, dataSize=0xE4C0`≈filesize), **same file size 58,576 B**, headers byte-identical except one bbox float at `0x1C`. But `cmp` shows **16,553 bytes differ**, all in the VIF payload from offset `0x291` onward. Result: **P8 `gped_bam` renders perfectly; THPG `gped_bam` renders scrambled blocks.** Same decoder, same header, different VIF payload → THPG changed the vertex/strip encoding.
- More THPG evidence: `gped_anchorman_body` `(2,6,6)` → scrambled; `gped_bobburnquist` `(1,7,7)` → **head correct, body scrambled** (fails on higher-vertex meshes). Simple THPG meshes still work: `cas_acc_gloves01` renders clean gloves. So the break is in the character-body VIF batches, not the whole format.
- **Divergence localized (2026-07-03)** via `tools/diagnostics/thpg_vif_compare.py` on the P8↔THPG `gped_bam` pair:
  - VIF opcode **framing is identical** — same opcodes at the same offsets. Opcode 8 = `UNPACK V3_16 num=42` at `0x600` (data `0x604`) in **both** files. So it's not a framing/layout change (STCYCL, batch order, UNPACK types all match).
  - The **vertex position DATA differs from the very first batch**. Reading `0x604` as V3_16÷16: P8 = clean surface positions (v0 = −38, 942, 25; y ~838–942, z small/incrementing); THPG = garbage (v0 = −9616, −20977, 6504).
  - Byte relationship (v0): P8 `da ff | ae 03 | 19 00` vs THPG `70 da | 0f ae | 68 19`. **THPG's per-coord high byte ≈ P8's low (meaningful) byte, with an extra low byte of precision inserted.** THPG appears to store positions at **higher precision / a different fixed-point** inside the same V3_16 UNPACK slot — our `PositionScale = 1/16` V3_16 read (`ThawPs2ReplayVertexDecoder.DecodeVertexSources`) then produces scrambled positions.
  - Histogram hint: THPG's stream uses `STMOD`/`STMASK` (VIF row/mask add modes) that P8's does not — a candidate mechanism for the different position reconstruction (unpacked value + STROW base, or a masked write). Needs confirmation with a synced walk (the crude Python walker desyncs at the first gap chunk `0x21C4`).
- **Second-pass confirmation (2026-07-03, `tools/diagnostics/thpg_vif_diff.py`) — sharpens the above:**
  - **Positions-ONLY.** Diffed each UNPACK payload: V3_8 normals (`0x704`) and V4_16 uv/bone (`0x788`) are
    **byte-identical** P8↔THPG. Only the V3_16 positions differ. So normals/UVs/bones/strip-topology all decode
    correctly for THPG already — the fix is isolated to the position values.
  - **Scale is ÷4096, not ÷16 (256× finer), confirmed on X/Z.** THPG raw sint16 X ≈ P8·256 (v0 X: P8 −38 →
    THPG −9616 ≈ −38·256+112; Z likewise). Decoding THPG at `/16` fills the full ±2047 cube uniformly (glb range
    THPG `±2047` vs P8 `[-37,74,8]`) = garbage.
  - **The blocker is Y overflow.** Real Y is large (P8 raw sint16 Y ∈ [732,1008] → ÷16 = [45.75,63]); `real·4096`
    overflows sint16, so THPG's Y wraps mod 65536 (verified: `THPG.y ≈ (real·4096) mod 65536` — v0 −20992≈−20977,
    v3 wraps to +17920≈17987). A plain `/4096` can't recover Y; the VU microprogram reconstructs the high bits.
    **This is why it's not a one-line scale fix.**
  - **ENCODING CONFIRMED:** `THPG_raw_sint16 = round(true_position · 4096)` (Q4.12 fixed-point), which **wraps**
    mod 65536 because the mesh-local extent (>±8 units) exceeds Q4.12's ±8 range. Verified against the P8 oracle:
    `THPG ≈ P8real·4096` to within P8's own 1/16 quantization (|diff| ≤128 = 256·½). So THAW/P8 use Q12.4 (`·16`,
    no wrap); THPG use Q4.12 (`·4096`, wraps). No extra per-vertex data (V3_8/V4_16 identical) → the VU program
    reconstructs the integer part.
  - **Fix path (validated but non-trivial):** decode = `unwrap(raw)/4096`. Phase-unwrap works (consecutive strip
    verts <8 units apart → a raw jump implies a ±16 band cross) BUT must run along the **true strip order** (raw
    UNPACK order is not spatially contiguous — unwrapping raw order left v20/v41 off by exactly ±16; v0/v1 perfect).
    The strip order is reconstructable from the V4_16 output-address convention (which is identical to P8, so already
    correct). **Open piece: the absolute anchor** — unwrap fixes *relative* positions but leaves each mesh's absolute
    16-unit band ambiguous, and the 64B entry table carries no per-mesh position. Needs either a global unwrap across
    the whole strip from a single origin, or the VU-program base. **Gate strictly to THPG** (detect: V3_16 positions
    span near-full sint16 range / glb bbox ~±2047) so P8/THAW keep `/16`.
- **✅ RECONSTRUCTION SHIPPED (2026-07-04) — 91.6% oracle-exact; renders recognizable characters.**
  `ThpgPositionUnwrapper` (new, in `Replay/`), wired through `ThawPs2SkinFile.ReplayExtractKicks`:
  - **Detection:** decoded-at-Q12.4 extent grossly exceeding the header bounding sphere (`UsesQ412Positions`,
    signature-scans STCYCL(3,1)+V3_16 batches byte-wise so gap chunks can't desync). THAW/P8 never trigger —
    THAW corpus regression is bit-identical (332/332, 227,487 tris) and P8 `gped_bam` renders unchanged.
  - **Relative unwrap (100% correct per oracle):** weighted union-find over strip-adjacent vertex pairs
    (confidence-ordered by wrap residual) + same-section exact-fine welds. Every remaining oracle error is a
    whole-component constant band shift — the relative structure is solved.
  - **Absolute placement:** the 80-byte per-mesh gap-chunk records (base = entry-table end, stride 80) carry each
    section's **bbox half-extents (+0x28) and centre (+0x38)** (+0x14 = section VIF offset, +0x10 = material) —
    the authored bounds THPG relies on. Placement scores candidates by per-vertex containment in the section box
    + a coverage bonus (pieces that close the gap to a stored box face win — this resolves the hands at the arms
    box's ±X faces) + chirality-aware proximity for ties (close contact with disagreeing normals = mirror-twin
    interpenetration, penalized).
  - **Oracle regression test:** `ThpgQ412UnwrapTests` (P8↔THPG `gped_bam` per-vertex compare, ≥90% threshold).
- **🔶 Remaining gap (~8% of vertices on `gped_bam`): small boundary/detail pieces off by one band.**
  The "first mesh of each section" is a boundary kick re-rendering the previous section's geometry under the next
  section's label — its section box contradicts its true position, so it falls to pure-proximity placement, which is
  ambiguous for tiny pieces. Tried and rejected (all made things worse — see the unwrapper's comments): bone-offset
  cluster voting (c2[14:8] is a batch-local slot remap, not a global bone id), positional cross-section welds
  (identical instanced details weld to the wrong copy), carry/origin-provenance cross-section welds (merged
  components then fight their section boxes). Most promising next leads: (a) decode the per-batch preamble
  slot-remap tables (V4_8/V3_8/V2_8, stride-7 index patterns) to get REAL global bone ids for cluster votes;
  (b) VU1 microcode from the THPG ELF for the true reconstruction rule; (c) joint per-section assignment
  (components of one section placed together against box + coverage + non-overlap constraints).
- Debug: `THPG_UNWRAP_DBG=1` prints component structure, candidate scores, and placement decisions.
- Reusable tools: `tools/diagnostics/thpg_vif_compare.py`, `thpg_vif_diff.py`, `thpg_band_analysis.py`
  (oracle band computation; proved bands spatially continuous and bone-slot-uncorrelated).

### 🔴 Collision (`.col`) is a newer, unsupported version
- Source: this session (2026-07-03).
- Evidence: `col .../m_c1_demo.pak/6F980DC3.col` → *"0 supported, 1 unsupported — Unrecognized mesh format: .col"*. THPG `.col` begins `00 FF 00 FF 03 00 00 00` (a `0xFF00FF00` marker + a `3`), not the version `9`/`10` int32 the COL parser (`ColFile.cs`, `FormatProbeMesh.ProbeColFile`) expects. Project 8 `.col` also fails the 9/10 check.
- What's left: decode the newer COL container. It is **not** the THUG/THUG2/THAW v9/v10 layout — reverse the `0x00FF00FF`-prefixed structure (probably a chunked/marker-delimited variant). Reference: `io_thps_scene` collision importers may cover THPG.

---

## Not yet checked this session (unknowns — confirm before claiming support)

Exercised so far: textures (both), character skins (both — P8 good, THPG garbled), `.col` (both unsupported). Status **unknown** for both games: `.mdl.ps2` object meshes, worldzone PAKs, `.ska` animations, `.pak` archive discovery, audio (`.vag`/streams), and `.bik` video (Bink — a distinct container, almost certainly unsupported). A session picking this up should sweep each family and record pass/fail rather than assuming parity — the P8-vs-THPG skin surprise shows same-family assumptions across these two games can be wrong.

## By design / won't-fix (⚪)

- `.bik` (Bink Video) — proprietary RAD Game Tools codec; out of scope unless a decoder dependency is acceptable. Note in output, don't attempt.
