# Backlog — Skeletal Animation

Created 2026-07-03. Distilled from `memory/` + `CLAUDE.md` + `docs/thps3-ska-animation-correctness-handoff.md`. See `BACKLOG_SUMMARY.md`.

**Re-verified 2026-07-26 vs HEAD (v1.3.4, 60d0b81) — full-domain audit.**

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔶 THPS3 (RW DFF) SKA animation — COMPOSITION FIDELITY, not an unimplemented path
- Source: `memory/thps3_ska_format_notes.md`, `memory/thps3_ska_animation_correctness_handoff.md`, `docs/thps3-ska-animation-correctness-handoff.md`.
- Reframed 2026-07-26: this is **not** "bind-pose / T-pose only" and **not** a parser blocker. The THPS3 SKA parser is done (`SkaThps3Parser.cs` reproduces the runtime Q-track split), and the SKA overlay **is wired into the RenderWare DFF export path** (`Core/Formats/Mesh/Conversion/MeshModelParser.cs:718-726`). The pipeline emits **646/648 validator-clean GLBs with textures** for THPS3 DFF-skinned models. What remains is purely a **pose-composition fidelity bug**: applied animation **spasms** rather than producing subtle idle/breathing motion.
- What's left: work the handoff doc's test matrix + bone-level diagnostics against `docs/thps3-ska-animation-correctness-handoff.md`. The RW DFF applier (`SkaPoseEvaluator`) and rotation/translation composition semantics are the suspect surfaces. Contrast against the working THPS4/THUG SKA path (below), which shares the evaluator.

### 🟢 PSX (PS1) character animation — SOLVED across the lineage (v1.3.x)
- Source: `memory/psx_anim_status.md`, MEMORY.md index.
- Evidence: PS1 character animation is now solved across the whole lineage (Apocalypse → THPS1 → THPS2 → Spider-Man → SM2EE). The old "THPS1-proto garbled / garbled character meshes" claim is **false at HEAD** (490 character-class files, 0 real failures). The v1.2.2 "THUG/COP/Spider-Man anims broken" release feedback was fixed by `e60d4aa` (rotation-matrix transpose + flat-skeleton for HIER+v1) + `c94e2c3` (v2-codec bit-packed segment-endpoint bug). Translation channels, tween-interval expansion, and CycleAnim wrap all shipped.
- What's left (narrow, distinct gaps — not core character animation):
  - **PSX pulsing-colour PLAYBACK** (S-M, user-facing): only the initial phase is exported; `pColourPulseData` should animate like the UV wibble path rather than baking one playhead.
  - **PSX placed-object SKELETAL animation** (M-L, user-facing): scripted level objects (traffic cars, doors) export static.
- Matching-decomp ground truth is on WSL (`\\wsl.localhost\Ubuntu\home\slfx77\thps2-psx-proto\`).

### ~~🔴 RW DFF (THPS3) skinned models export in bind pose only~~ — CORRECTED 2026-07-26
- Source: `CLAUDE.md` → *Not Yet Implemented* → "RW DFF / THPS3 animations".
- **Retracted**: THPS3 does **not** export bind-pose/T-pose-only. THPS3 animation is Neversoft SKA (not RenderWare-native chunks), the parser reproduces the runtime Q-track split (`SkaThps3Parser.cs`), and the overlay is wired into the RW DFF export path (`MeshModelParser.cs:718-726`). The only remaining work is pose-composition fidelity, already tracked by the "THPS3 (RW DFF) SKA animation — COMPOSITION FIDELITY" item above. This item is closed as a duplicate.

### 🔶 `.blend` skinned-character limb-stretch (double-translation) — fixed for PSX, latent elsewhere
- Source: 2026-07-26 audit; v1.3.4 `.blend` limb-stretch fix.
- Evidence: the v1.3.4 `.blend` limb-stretch fix was gated `SourceKind == "Psx"`. THAW/PS2 and THPS3 skinned characters share the **same latent double-translation** stretch in `.blend` export — the PSX fix does not reach them because of that gate.
- What's left: generalize the fix to the `matrix_basis` form for all skinned sources and validate against a real THAW/PS2 + THPS3 rig (the guard was PSX-only for want of a non-PSX test rig).

---

## Done (for reference) ✅

- ✅ SKA animation import (THPS4/THUG/THUG2) — mesh deforms correctly; 1,892/1,892 THPS4 files parse, 1,279 GLBs, validator-clean. `memory/ska_animation_handoff.md`. Residual minor shoulder/pectoral jitter deemed not worth chasing.
- ✅ THPS4 V1 skeleton bind pose from `default.ska.ps2` — native, no cross-game substitution. `memory/thps4_v1_skeleton_bind.md`.
- ✅ Q48/T48 compress-table auto-discovery (fixes identity-snap stutter). `memory/ska_animation_handoff.md`.
- ✅ Animated-GIF + animated-GLB renderers (`glb-gif`, `psx-anim-export`); SLERP-correct evaluation in the software rasterizer.

## By design / won't-fix ⚪

- ⚪ Minor shoulder/pectoral jitter on THPS4 SKA — data verified clean; likely viewer NLERP-vs-SLERP or genuinely sparse keys. Not worth chasing.
