# Backlog — Skeletal Animation

Created 2026-07-03. Distilled from `memory/` + `CLAUDE.md` + `docs/thps3-ska-animation-correctness-handoff.md` — **not re-verified this session**. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔶 THPS3 SKA animation is visually wrong (spasms, not motion)
- Source: `memory/thps3_ska_format_notes.md`, `memory/thps3_ska_animation_correctness_handoff.md`, `docs/thps3-ska-animation-correctness-handoff.md`.
- Evidence: the SKA parser is done — the pipeline emits **646/648 validator-clean GLBs with textures** for THPS3 DFF-skinned models — but applied animation **spasms** rather than producing subtle idle/breathing motion. Root cause not yet isolated.
- What's left: work the handoff doc's test matrix + bone-level diagnostics. The RW DFF applier (`SkaPoseEvaluator` / `Thps3SkaPoseApplier`) and pose composition are the suspect surfaces. Contrast against the working THPS4/THUG SKA path (below), which shares the evaluator.

### 🔶 PSX (PS1) character animation — per-game partial correctness
- Source: `memory/psx_anim_status.md`, MEMORY.md index (committed 3-way `ad7ac17` / `2d03896` / `11f63d5`).
- Evidence: parsing/codec proven (798 files, 57 tests). **THPS2 / Apocalypse / Spider-Man render plausibly; THPS1-proto is garbled.** Open sub-items: translation channels, PrototypeSparse entries, tween-interval expansion.
- What's left: the THPS1-proto garble + the three named decode gaps. Matching-decomp ground truth is on WSL (`\\wsl.localhost\Ubuntu\home\slfx77\thps2-psx-proto\`).

### 🔴 RW DFF (THPS3) skinned models export in bind pose only
- Source: `CLAUDE.md` → *Not Yet Implemented* → "RW DFF / THPS3 animations".
- Evidence: no animation support for the RenderWare path beyond the SKA overlay work above — skinned DFF models export in **bind pose (T-pose)**. THPS3 likely uses RenderWare animation chunks or a custom Neversoft format distinct from the THPS4+ SKA format.
- What's left: identify the THPS3 animation container (RW HAnim/ANM chunks vs. a Neversoft custom format) and wire it into the DFF export path. Note this overlaps the "THPS3 SKA spasms" item — confirm whether THPS3 animations are SKA (Neversoft) or RenderWare-native before starting.

---

## Done (for reference) ✅

- ✅ SKA animation import (THPS4/THUG/THUG2) — mesh deforms correctly; 1,892/1,892 THPS4 files parse, 1,279 GLBs, validator-clean. `memory/ska_animation_handoff.md`. Residual minor shoulder/pectoral jitter deemed not worth chasing.
- ✅ THPS4 V1 skeleton bind pose from `default.ska.ps2` — native, no cross-game substitution. `memory/thps4_v1_skeleton_bind.md`.
- ✅ Q48/T48 compress-table auto-discovery (fixes identity-snap stutter). `memory/ska_animation_handoff.md`.
- ✅ Animated-GIF + animated-GLB renderers (`glb-gif`, `psx-anim-export`); SLERP-correct evaluation in the software rasterizer.

## By design / won't-fix ⚪

- ⚪ Minor shoulder/pectoral jitter on THPS4 SKA — data verified clean; likely viewer NLERP-vs-SLERP or genuinely sparse keys. Not worth chasing.
