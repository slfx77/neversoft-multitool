# Backlog — Mesh Reconstruction Fidelity

Created 2026-07-03; **sweep-verified 2026-07-03**. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Cross-ref:** `game-thpg-p8.md` (the `(1,8,8)` skin garble is a genuine, still-open strip-reconstruction bug — the one clearly-broken mesh path), `memory/thaw_worldzone_phase420_solved.md`.

> **⚠ The `CLAUDE.md` / `memory/` mesh-fidelity notes are STALE.** A 2026-07-03 conversion+render sweep (below) shows the character-mesh paths that those notes call "garbled / missing parts" now render **correctly** at HEAD. Trust the sweep, not the older prose. Corrected the same day I first (wrongly) transcribed the stale notes into this file — same lesson as the `README.md` "tested games" overstatement.

---

## Verified GOOD at HEAD (🟢 2026-07-03 sweep)

Conversion + `glb-render` inspection, current HEAD (post-v1.2.1 alpha fix `884d018`):

- **THAW PS2 character skins — whole corpus clean.** `mesh` over the full THAW `DATAP/models` tree: **332/332 files convert, 227,487 tris, 0 failures, 0 anomalies** (no 0-tri, no glTF errors). All 41 `skater_pro` skins convert with sane counts.
- **Chad Muska (the reported case) is FIXED.** Front/back/side renders show hair present + solid torso; **3,488 tris vs PC 3,491 = 99.9% recall.** The reported "missing hair + hole in torso" was the GS-alpha export regression (`884d018` / v1.2.1), **not** a geometry gap — both symptoms were the over-culled alpha mask.
- **The documented THAW worst-cases are resolved.** `skater_lasek` now **3,070/3,070 tris (100% recall**, up from the 2,930/95% in `memory/thaw_ps2_skin_format.md`) and renders clean. `pro_vallely_head` renders a complete head (the "PC Mesh 2 entirely absent" note is stale).
- **PSX (PS1) character models render correctly** — contradicting the `CLAUDE.md` "garbled / misaligned body parts" note. Spider-Man `blackcat`/`carnage` and THPS2 `burnq2`/`cab` all convert + render as complete, correct characters.

**Takeaway:** the meshes we *claim* to support are in good shape. The remaining real mesh work is (a) the genuinely-broken **THPG/P8 `(1,8,8)` skins** (see `game-thpg-p8.md` — a format we don't yet claim), and (b) turning "good on a broad sample" into "proven across the corpus" via the QA harness below.

---

## Remaining — needs work

### 🔴 Build a mesh-QA regression harness (the real remaining fidelity work)
- Source: this session — the fidelity story is currently "looks good when spot-checked," which is how the stale notes above went unnoticed.
- What's left: a repeatable sweep that, across every supported mesh format + game, (1) batch-converts and flags hard failures (0-tri, glTF-validator errors), (2) where a PC/`.wpc` or other ground truth exists, computes **triangle recall** and flags files below a threshold, and (3) emits a render **contact sheet** for eyeball review. Wire it as a `tools/diagnostics/` script (Python over the CLI + `glb-render`) so regressions like the alpha bug are caught mechanically, not by a user noticing a hole months later. This is the durable answer to "are the meshes we claim to support actually perfect."

### 🔶 THAW worldzone level geometry — "missing parts" (NOT re-verified this session)
- Source: `memory/thaw_worldzone_phase420_solved.md` (user visual feedback after phase-420: *"level layout is now there, looks accurate, mostly correct textures… still missing parts"*).
- Evidence: the level-MDL leaf sub-chunk format is solved and 3,977/3,977 leaves parse (z_bh: 49,935 tris, validator-clean). Known residual limits documented in that memory file:
  - **Triangle-efficiency cap** — Format-A leaves average ~12 drawn tris after ADC degenerate suppression; we can't extract more than the engine draws (⚪ by design).
  - **Billboards not camera-facing** — Format-B quads are axis-aligned, not view-facing (glTF limitation without `KHR_materials_unlit` + viewer support).
  - **5 small object MDLs skipped** in z_bh (`FindMdlVifStart` rejects 880–2,944-byte props as "not a PAK MDL").
- What's left: the "missing parts" is now **subjective** — needs the user to point at specific regions. The one cleanly-identifiable lead is the 5 skipped small object MDLs. Everything else may be the by-design tri cap. Reopen only with a specific visual target.

---

## Done (for reference) ✅

- ✅ THAW worldzone level-MDL leaf format (phase 420) — `memory/thaw_worldzone_phase420_solved.md`; K-offset derivation + per-leaf VIF slicing + per-batch GS context + billboards.
- ✅ THAW worldzone discovery inside DATAP.WAD — `memory/thaw_worldzone_archive_discovery.md` (v1.2.1).
- ✅ THAW worldzone billboards as camera-facing quads in `.blend` — `memory/thaw_worldzone_billboards.md` (the `.blend` path; glTF stays axis-aligned).

## By design / won't-fix ⚪

- ⚪ Worldzone triangle-efficiency cap (ADC degenerate suppression) — we render exactly what the engine rasterizes.
- ⚪ Camera-facing billboards in **glTF** — no static-glTF way to do view-facing quads; the `.blend` export handles it via Track-To constraints.
