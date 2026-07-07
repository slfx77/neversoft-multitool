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
- **✅ RECONSTRUCTION SHIPPED (2026-07-04) — 91.6% oracle-exact; renders recognizable characters.
  Improved to 95.6% (2026-07-06) via exact-coincidence scoring, see below.**
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
  - **Oracle regression test:** `ThpgQ412UnwrapTests` (P8↔THPG `gped_bam` per-vertex compare, ≥95% threshold).
- **✅ Exact-coincidence scoring (2026-07-06): 91.6% → 95.6% (246/5,555 mismatched).**
  Key insight: boundary kicks *re-render* geometry that already exists in the previous section, so the correct
  band placement makes their vertices coincide **exactly** (< 0.01 units) with already-placed vertices — while a
  mirror twin or nesting placement is merely *close*, never exact. `ContactScore` now reports exact-coincidence
  counts and the placement loop prefers the candidate with the most exact coincidences before falling back to
  contact distance. Fixed the three large boundary re-render meshes (8, 27, 67). Also tried and rejected
  (2026-07-06): two-pass placement deferring mislabeled components + file-order neighbor-grid proximity —
  slightly worse (246 → 256), reverted.
- **🔶 Remaining gap (~4.4% of vertices on `gped_bam`): three stubborn detail pieces.**
  Mesh 15 (74 verts: right-wrist skin patch placed at the neck, error (−2,0,0) bands — not a re-render, so no
  exact coincidence exists); meshes 58 + 72 (76 + 70 verts: geometrically near-identical twin detail pieces
  swapped feet↔hat, errors (0,+4)/(0,−4) — indistinguishable by any local geometric signal); mesh 57 (21/40
  partial); ~6 single-vertex stragglers. Earlier rejected approaches (all made things worse — see the unwrapper's
  comments): bone-offset cluster voting (c2[14:8] is a batch-local slot remap, not a global bone id), positional
  cross-section welds (identical instanced details weld to the wrong copy), carry/origin-provenance cross-section
  welds (merged components then fight their section boxes).
- **🔬 VU1 microcode RE (2026-07-06) — mechanism hunt; major format structures decoded, band rule still open.**
  Extracted VU1 microprograms from both ELFs (`SLUS_214.44` / `SLUS_216.16`) by scanning for VIF MPG chains
  (`tools/diagnostics/thpg_elf_ucode_scan.py`), disassembled with a new minimal VU disassembler
  (`tools/diagnostics/vu_disasm.py`, tables ported from PCSX2 `DisVUmicro.h`). **Definitive: THPG's programs are
  byte-identical to P8's except ITOF4→ITOF12 swaps (19 sites in the main renderer, 5 in the second) and ONE
  float constant scaled by exactly 1/256 (264192→1032) — the Q12.4→Q4.12 domain shift. There is NO band
  reconstruction logic in the microcode**: positions are ITOF12'd and immediately matrix-transformed.
  Structures decoded along the way (verified on all 76 gped_bam batches, exact vertex counts):
  - **V4_8/V3_8/V2_8 preamble tables = skinning RUN TABLES**: entries `(count×3, matrixAddr…)` — V4_8 = 3-bone
    runs, V3_8 = 2-bone, V2_8 = 1-bone, `(x,0)` terminators. Matrix palette stride 7 qwords, slot addr = 7k+1
    (1, 8, 15, 22, 29, 36, …). Vertex order = concatenated runs.
  - **V4_16 preamble entries = carry-vertex qword images** (raw pos/nrm/uv register data for stitch vertices),
    placed at grid addresses immediately below the batch arrays.
  - **Batch VU layout**: `[uv]@0x37D+3i, [nrm]@0x37E+3i (masked unpack), [pos]@0x37F+3i`, STCYCL CL=3 WL=1.
  Falsified for the band rule (all tested against the oracle): STROW/STMOD in-file (none exist — the earlier
  histogram hit was a desynced-walker false positive), USN unsigned-domain encoding, constant band per
  (batch,slot) / per run / per mat-set (bands drift ±1..2 along runs that span >16 units), global-palette
  anchors (slot verts span 75 units), additive per-slot anchors, (section,slot) spatial clustering (loose,
  62-unit extents — palettes re-upload per batch). Nearest-band-to-anchor with oracle-derived per-(batch,slot)
  anchors reaches 97.87% — an upper bound needing real bone anchors we don't have.
  **Open question**: how hardware recovers per-vertex bands with no per-vertex data and no ucode logic.
- **🔬 PCSX2 savestate RE (2026-07-06) — load-time rewrite FALSIFIED; runtime pipeline fully mapped.**
  User provided a THPG savestate (`SLUS-21616 (391A7331).01.p2s`, skater in-level). Savestates are ZIPs with
  raw 32MB `eeMemory.bin` + `vu1Memory.bin` + `vu1MicroMem.bin`. Findings (all verified in RAM):
  - **No load-time position rewrite.** 910 V3_16-format batches found in EE RAM; 762 matched to disk CAS
    pieces by uv-payload hash; 663 position payloads byte-identical to disk (the ~120 "different" are face
    pieces with 1–4 raw-unit deltas = CAS face morphing). Loaded file images differ from disk only in 38
    pointer-fixup bytes (entry +0x20/+0x2C, record headers). Positions arrive at VU1 still wrapped. No
    V3_32-widened variants exist anywhere in RAM.
  - **Resident VU1 microcode = the ELF programs we analyzed** (main@0x000 and p400@0x400 bit-identical to the
    MPG extractions) — the static analysis was of the real renderers.
  - **Frame DMA structure decoded**: per character: CALL shared setup (STMASK) → REF palette upload
    (`UNPACK V4_32 n=208 → VU 0x70` = **52 bone matrices × 4 qwords** from an engine buffer, animated
    anim×invBind deltas: orthonormal rows + small translations) → camera upload (7 qw → VU 0x000, MSCAL 2) →
    per-piece CALL with 5-qword object header (→ VU 7–11, MSCAL 0) into the file's flat RET-terminated VIF
    chain.
  - **VIF quirks confirmed live in frozen VU memory**: pos.w = next vertex's x (V3_16 CL=3/WL=1 quirk);
    nrm.w = 128 constant (engine STMASK row-fill); uv c2/c3 sign-extended with bit 15 = ADC. Output GIF
    packets use regs [ST, RGBAQ, XYZF2].
  - **Run-table matrix indices are per-section LOCAL slots** (`(v−1)/7` caps at ~8 for a 52-bone character);
    the local→global map was NOT located (runtime mesh-struct pointer trail leads to lighting/color tables).
  - **Matrix absorption definitively insufficient**: even 1-bone runs (single unambiguous matrix) span
    multiple bands per (batch, bone) — 13/97 groups, up to 28-vert groups. Bone-cluster band voting fails as
    a placement signal (slots local, clusters loose).
- **✅ MECHANISM QUESTION RESOLVED (2026-07-06, GS dumps + savestates): there is NO reconstruction mechanism —
  the game never renders the wrapping files.**
  - New tooling: `gsdump --dump-vertices` writes every post-VU1 kicked vertex (screen XYZ + STQ + no-kick) to
    CSV; `tools/diagnostics/thpg_gsdump_band_check.py` / `thpg_gsdump_alias_check.py` match `.skin.ps2` batches
    to GS draws by UV fingerprint (skin ucode emits ST = uv×Q → s/q = raw uv/4096). Matching validated at
    0.8–1.8px adjacent-vertex screen coherence across 100+ batches.
  - **Wrap census: 78 of ~750 THPG skins wrap** — ALL full-character exports (gped pros, 66 level peds,
    `skater_pro/shaba_*`, `skater_secret/*`). The Q4.12 exporter silently wraps geometry beyond ±8 units.
  - **Runtime character assets are piece-local and NEVER wrap**: two independent in-game captures (free-skate
    + a scene with two pedestrians close up) show every rendered character — skater AND pedestrians — is
    CAS-composed from `skater_male`/`skater_pro` piece files with zero wrapping batches. The pedestrians'
    draws match `cas_career_hoody01` 30/30, `cas_skater_head09` 35/35, etc.; NOTHING matches `peds/*.skin.ps2`
    (not by chain bytes in RAM, not by uv payload bytes, not by GS-draw fingerprints).
  - Conclusion: THPG switched to Q4.12 as a **precision upgrade for piece-local CAS assets** (256× finer
    within the ±8-unit window every real asset fits). The wrapping whole-character files are legacy exports
    run through the new pipeline — shipped with silently truncated positions the engine never loads. There is
    no band-recovery anywhere (VU1 ucode: proven identical ITOF-only diff; EE load path: proven byte-identical
    for loaded files; runtime: the files are simply unused).
  - **Implication for us: our 95.6% heuristic reconstruction recovers data the shipped game cannot render at
    all.** The residual ~4.4% has no in-game ground truth; the P8 oracle (same art exported pre-truncation)
    is the only reference and the remaining error is bounded by heuristic quality, not by undiscovered format
    data. Caveat: a career pro-goal capture could still be taken to confirm goal peds are CAS-composed like
    street peds, but the street-ped result makes that outcome near-certain.
  - Bonus verification: converted + rendered the actually-used CAS pieces (`cas_reg_tshirt_killsparrow`,
    `cas_skater_head01`, `cas_skater_body`, `cas_skater_legs_lower`) — all flawless with current code, since
    non-wrapping Q4.12 files decode exactly.
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
