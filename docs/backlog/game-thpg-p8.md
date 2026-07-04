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
- What's left (multi-session RE):
  1. Determine THPG's exact position encoding — test the "higher-precision fixed point" hypothesis (different scale, or 24/32-bit-effective packing) and the STMOD/STROW-add hypothesis. Best done by dumping the **C# replay engine's decoded batch positions** for both files and matching THPG's raw bytes to P8's known-good positions (there is no THAW-replay trace CLI yet — a small analyzer or a `--trace` hook would help).
  2. Apply the THPG-specific decode gated to the variant (do **not** change the P8/THAW path — it's correct). Detection needs a signal that distinguishes THPG from P8/THAW skins (header fields alone don't — `gped_bam` headers are near-identical; likely a per-batch or gap-chunk marker).
  3. Oracle: the P8↔THPG `gped_bam` pair is ideal — P8 is the correct decode of the exact same character, so a candidate THPG decode can be checked against P8's positions numerically, not just visually.
- Reusable tool added: `tools/diagnostics/thpg_vif_compare.py` (aligned VIF opcode diff + first-divergence byte offset).

### 🔴 Collision (`.col`) is a newer, unsupported version
- Source: this session (2026-07-03).
- Evidence: `col .../m_c1_demo.pak/6F980DC3.col` → *"0 supported, 1 unsupported — Unrecognized mesh format: .col"*. THPG `.col` begins `00 FF 00 FF 03 00 00 00` (a `0xFF00FF00` marker + a `3`), not the version `9`/`10` int32 the COL parser (`ColFile.cs`, `FormatProbeMesh.ProbeColFile`) expects. Project 8 `.col` also fails the 9/10 check.
- What's left: decode the newer COL container. It is **not** the THUG/THUG2/THAW v9/v10 layout — reverse the `0x00FF00FF`-prefixed structure (probably a chunked/marker-delimited variant). Reference: `io_thps_scene` collision importers may cover THPG.

---

## Not yet checked this session (unknowns — confirm before claiming support)

Exercised so far: textures (both), character skins (both — P8 good, THPG garbled), `.col` (both unsupported). Status **unknown** for both games: `.mdl.ps2` object meshes, worldzone PAKs, `.ska` animations, `.pak` archive discovery, audio (`.vag`/streams), and `.bik` video (Bink — a distinct container, almost certainly unsupported). A session picking this up should sweep each family and record pass/fail rather than assuming parity — the P8-vs-THPG skin surprise shows same-family assumptions across these two games can be wrong.

## By design / won't-fix (⚪)

- `.bik` (Bink Video) — proprietary RAD Game Tools codec; out of scope unless a decoder dependency is acceptable. Note in output, don't attempt.
