# Backlog — Skeletal Animation

Created 2026-07-03. Distilled from `memory/` + `CLAUDE.md` + `docs/thps3-ska-animation-correctness-handoff.md`. See `BACKLOG_SUMMARY.md`.

**Re-verified 2026-08-10 against the current tree, retained PSX corpus, and the THPS3 correctness handoff.**

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔶 THPS3 (RW DFF) SKA animation — COMPOSITION FIDELITY, not an unimplemented path
- Source: `memory/thps3_ska_format_notes.md`, `memory/thps3_ska_animation_correctness_handoff.md`, `docs/thps3-ska-animation-correctness-handoff.md`.
- Current state (rechecked 2026-08-09): this is **not** a bind-pose-only path and **not** a parser blocker. `SkaThps3Parser` reproduces the captured runtime Q-track linearization, and the overlay is wired into RenderWare DFF export. Validator-clean output proves container validity, not pose correctness: the remaining defect is the final transform convention after the runtime Q/T buffers combine with bind/model/skinning matrices.
- What's left: capture or locate the final 29-bone runtime matrix palette, then compare it against the handoff's `bind-raw`, `bind-raw-rawt`, and required matrix-space variants. Keep `bind-raw` as the production default unless that final-matrix evidence proves another composition. More SKA field-order permutations are specifically not the next step.

### 🔶 PSX (PS1) animation — character and proven traffic-snapshot paths solved
- Source: `memory/psx_anim_status.md`, MEMORY.md index.
- Evidence: PS1 character animation is now solved across the whole lineage (Apocalypse → THPS1 → THPS2 → Spider-Man → SM2EE). The old "THPS1-proto garbled / garbled character meshes" claim is **false at HEAD** (490 character-class files, 0 real failures). The v1.2.2 "THUG/COP/Spider-Man anims broken" release feedback was fixed by `e60d4aa` (rotation-matrix transpose + flat-skeleton for HIER+v1) + `c94e2c3` (v2-codec bit-packed segment-endpoint bug). Translation channels, tween-interval expansion, and CycleAnim wrap all shipped.
- Shipped 2026-08-10: a narrow placed-traffic path resolves D5–DA BADDYs to their separate `CSuper` sources, attaches the embedded loop to instance-unique skeletons, and preserves each first-road-node world transform in both GLB and Blender. The seven directly script-reachable corpus placements are offered as a default-disabled **Possible scripted traffic snapshot** group: three taxis in final Downtown, one van plus two cable cars in San Francisco, and the prototype Downtown taxi. The Burnside node has no demonstrated inbound trigger and remains absent. Per-source output is transactional and fail-open.
- What's left: reproduce command timing, repeated creation, suspension, and road-route translation if a faithful runtime simulation is wanted. Other placed skeletal object families require a named corpus fixture and runtime binding evidence. The previous instructions to enumerate animation streams inside `skdown.psx` were wrong—the 836-object level file has no animation chunk—and no animated-door fixture was found; do not use rigid door-bank overlays as evidence for this skeletal path.
- Matching-decomp ground truth is on WSL (`\\wsl.localhost\Ubuntu\home\slfx77\thps2-psx-proto\`).

### ~~🔴 RW DFF (THPS3) skinned models export in bind pose only~~ — CORRECTED 2026-07-26
- Source: `CLAUDE.md` → *Not Yet Implemented* → "RW DFF / THPS3 animations".
- **Retracted**: THPS3 does **not** export bind-pose/T-pose-only. THPS3 animation is Neversoft SKA (not RenderWare-native chunks), the parser reproduces the runtime Q-track split (`SkaThps3Parser.cs`), and the overlay is wired into the RW DFF export path (`MeshModelParser.cs:718-726`). The only remaining work is pose-composition fidelity, already tracked by the "THPS3 (RW DFF) SKA animation — COMPOSITION FIDELITY" item above. This item is closed as a duplicate.

## Done (for reference) ✅

- ✅ **`.blend` skinned-character pose basis generalized** — completed 2026-08-09. Edit bones now carry rigid bind translation + rotation, and absolute IR translation/rotation channels are solved into Blender `matrix_basis` for every rigid skinned source rather than through a PSX-only subtraction. `BlendPoseBasisRegressionTests` pins a translated/rotated non-PSX hierarchy with mixed weights and animated scale, then compares every pose bone of the real THPS4 `Ped_F_Walk` rig against GLB at an authored key. The existing `PsxBlendExportRegressionTests` remains green. Non-rigid bind matrices now fail clearly instead of being approximated; the pinned PSX, PS2/SKA, and surveyed THPS3/RW binds are rigid.
- ✅ **PSX pulsing-colour playback** — shipped 2026-08-07 (`a9d7c1a`). Frame zero stays baked as the portable fallback; pre-transformed channel keys ship in GLB scene extras (`neversoftColourPulseChannels`), and current marked PSX meshes carry the exact pulse byte in standard `COLOR_1` alpha so `_PSX_COLOR_0` remains the sole custom semantic. The in-app viewer evaluates the channels on its 60 Hz timeline and still reads legacy `_PSX_FLAGS_0` files. `PsxColourPulseEvaluatorTests`, `PsxColourPulseExportTests`, and the viewer-contract tests pin the path. Native `.blend` pulse playback was not part of this closed viewer/export item.
- ✅ SKA animation import (THPS4/THUG/THUG2) — mesh deforms correctly; 1,892/1,892 THPS4 files parse, 1,279 GLBs, validator-clean. `memory/ska_animation_handoff.md`. Any historical shoulder/pectoral-jitter observation must be reproduced against the corrected narrow-quaternion decoder before it is treated as a remaining defect.
- ✅ THUG bare-CUT INTERMEDIATE/full-float SKA inspection — shipped 2026-08-10. `SkaIntermediateParser` consumes all 194 authoring members exactly (4,588,265 Q and 6,079,925 T keys), preserves their embedded hierarchy and raw/source rotations, and writes JSON without advertising an unproven skeletal/glTF route. Their paired compiled members provide the format oracle, but compiler-side root prerotation and signed-16 translation range handling keep direct animation export out of scope.
- ✅ Compressed byte-width quaternion components now follow the retained engine's unsigned-byte promotion. A 194-pair Rosetta regression covers 99,562 affected high-bit components and reaches a maximum comparable XYZ delta of `6.8784e-5`; the signed-16, table, translation, and THAW paths remain unchanged. Re-evaluate any old visual-jitter report against this corrected decoder before treating it as an intentional residual.
- ✅ THPS4 V1 skeleton bind pose from `default.ska.ps2` — native, no cross-game substitution. `memory/thps4_v1_skeleton_bind.md`.
- ✅ Q48/T48 compress-table auto-discovery (fixes identity-snap stutter). `memory/ska_animation_handoff.md`.
- ✅ Animated-GIF + animated-GLB renderers (`glb-gif`, `psx-anim-export`); SLERP-correct evaluation in the software rasterizer.

## By design / won't-fix ⚪

- ⚪ Historical minor shoulder/pectoral jitter on THPS4 SKA — not currently actionable without a fresh failing fixture. The byte-width quaternion sign bug has since been corrected, so the old visual observation is no longer evidence that the remaining data is clean or that interpolation is the cause.
