# Backlog — Tony Hawk's Proving Ground + Project 8 (PS2)

Created 2026-07-03. Status **verified this session** against the sample builds. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Games:** Tony Hawk's Project 8 (2006-9-21, PS2 — Final), Tony Hawk's Proving Ground (2007-9-3, PS2 — Final). Sample builds present under `Sample/Builds/`.

**Depends on / cross-ref:** `mesh-fidelity.md` (same strip-reconstruction root), `memory/thaw_ps2_skin_format.md`, `memory/thaw_ps2_skin_format` (THAW `.skin.ps2` VIF/DMA), `ThawPs2SkinFile.cs`.

> **Why these aren't "tested games."** They were never development targets — no format spec in `CLAUDE.md`, no test coverage, and the author confirmed never testing them. They were briefly (and wrongly) listed as tested in `README.md`; corrected in commit `f88f66d`. They reuse the THAW-era engine formats, so a lot converts, but the two gaps below make character/level output unreliable.

---

## What works today (🟢 verified 2026-07-03)

Reproduced by running the CLI against the THPG PS2 sample build (`Sample/Builds/Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)/DATAP`):

- **Textures** (`.tex.ps2`, `.img.ps2`) → PNG: decode correctly (identical format to THAW). `ps2tex models/arrow/arrow.tex.ps2` → 2 textures, 0 failed.
- **Simple meshes**: `mesh models/skater_male/cas_acc_gloves01.skin.ps2` → 1,384 tris, renders as a clean pair of gloves with red cuffs (glTF validator: 0/0/0).
- Conversions **run without error** and pass the glTF validator even when the geometry is wrong — validator-clean ≠ correct.

Project 8 mirrors THPG exactly: same skin version triple, same partial results.

---

## Remaining — needs work

### 🔶 Character skin meshes reconstruct with garbled strip topology
- Source: this session (2026-07-03).
- Evidence:
  - Skin header version triple is **`(1, 8, 8)`** (`01 00 00 00 08 00 00 00 08 00 00 00`), which is **not** one of the documented/handled triples (THPS4 `3,4,1`, THUG `5,6,1`, THUG2 `6,6,1`). The leading `1` matches the THAW pre-compiled `.skin.ps2` pattern (`memory/thaw_ps2_skin_format.md`), so this is most likely a **variant of the THAW VIF/DMA strip chain**, not a new base format.
  - `mesh models/peds/goal_peds/gped_anchorman/gped_anchorman_body.skin.ps2` → 985 tris, glTF-valid, but renders as **scrambled blocks** (exploded strips). `gped_bobburnquist.skin.ps2` → 4,370 tris: the **head reconstructs correctly** (recognizable face + hair) while the **body is scrambled** — classic strip-order / ADC-bit misinterpretation, localized to the higher-vertex body meshes.
  - Project 8: `gped_dustindollin.skin.ps2` (same `(1,8,8)` header) → 4,368 tris, same failure class.
- What's left:
  1. Determine how the `(1,8,8)` skins encode strip topology vs. the THAW `.skin.ps2` chain (`ThawPs2SkinFile.cs` + `ThawPs2SkinVifLayout.cs`). Likely a different VIF batch layout, ADC/output-address convention, or an extra pre-batch header. Diagnostic scaffolding exists for the THAW case: `tools/diagnostics/thaw_ps2_vif_walk.py`, `tools/diagnostics/thaw_ps2_strip_decode.py`.
  2. Cross-reference a **PC ground truth**: THPG/P8 have `.skin.wpc` equivalents (the THAW workflow used `skater_lasek` PC vs PS2 for numerical strip validation — see `memory/thaw_ps2_skin_format.md`). Extract the PC `.wpc` and diff triangle sets.
  3. Route the variant so simple meshes keep working (they already do — don't regress the gloves case).

### 🔴 Collision (`.col`) is a newer, unsupported version
- Source: this session (2026-07-03).
- Evidence: `col .../m_c1_demo.pak/6F980DC3.col` → *"0 supported, 1 unsupported — Unrecognized mesh format: .col"*. THPG `.col` begins `00 FF 00 FF 03 00 00 00` (a `0xFF00FF00` marker + a `3`), not the version `9`/`10` int32 the COL parser (`ColFile.cs`, `FormatProbeMesh.ProbeColFile`) expects. Project 8 `.col` also fails the 9/10 check.
- What's left: decode the newer COL container. It is **not** the THUG/THUG2/THAW v9/v10 layout — reverse the `0x00FF00FF`-prefixed structure (probably a chunked/marker-delimited variant). Reference: `io_thps_scene` collision importers may cover THPG.

---

## Not yet checked this session (unknowns — confirm before claiming support)

Only textures, skins, and `.col` were exercised. Status **unknown** for THPG/P8: `.mdl.ps2` object meshes, worldzone PAKs, `.ska` animations, `.pak` archive discovery, audio (`.vag`/streams), and `.bik` video (Bink — a distinct container, almost certainly unsupported). A session picking this up should sweep each format family and record pass/fail per family rather than assuming THAW parity.

## By design / won't-fix (⚪)

- `.bik` (Bink Video) — proprietary RAD Game Tools codec; out of scope unless a decoder dependency is acceptable. Note in output, don't attempt.
