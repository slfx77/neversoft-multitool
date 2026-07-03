# Backlog — Mesh Reconstruction Fidelity

Created 2026-07-03. Distilled from `CLAUDE.md` (*Research & Improvements*) + `memory/` — **not re-verified this session**; confirm against HEAD before deep work. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Cross-ref:** `game-thpg-p8.md` (the `(1,8,8)` skin garble is the same class of strip-reconstruction bug), `memory/psx_mesh_format.md`, `memory/psx_mesh_blender_comparison.md`, `memory/thaw_worldzone_phase420_solved.md`.

---

## Remaining — needs work

### 🔶 PSX (PS1) character models produce wrong geometry
- Source: `CLAUDE.md` → *Research & Improvements* → "PSX mesh conversion".
- Evidence (per `CLAUDE.md`): the parser (`Core/Formats/Mesh/Psx/PsxMeshFile.cs`), glTF writer (`PsxGltfWriter.cs`), CLI (`CLI/PsxMeshCommand.cs`), and GUI tab exist. **Level geometry (`_g.psx`) works; character models render garbled / misaligned body parts.** No community tool implements a catch-all across the format variants.
- What's left: character models use the "Super" skeleton path (item flag bit `0x02`) and stitched/attachable vertex types (`memory/psx_mesh_format.md`: vertex types 0/1/2/16; face flag bits `0x0040`/`0x0080`). The known-missing piece per `memory/psx_mesh_blender_comparison.md` is **face-flag manipulation** not implemented in either our code or the Blender fork. Next step there: a diagnostic comparing our parse vs. the `io_ns_psxtools_v4_apoc` Blender fork on the same file. Decompilation ground truth: THPS2 PSX proto (`M3dInit_ParsePSX`, `M3dAsm_TransformAndOutcodeSuperVertices`).

### 🔶 THAW worldzone level geometry — "missing parts"
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
