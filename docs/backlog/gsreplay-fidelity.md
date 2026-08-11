# Backlog — THAW GS-Replay Render Fidelity (research stream)

Created 2026-07-03. Distilled from the `memory/gsdump_*` topic files. **Re-verified 2026-08-10 against the current replay, validation tools, committed oracle goldens, and SW-native baseline.** The original "Remaining" items below were superseded by later work; see the retractions inline. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**What this stream is:** the software GS replay engine (`Core/Formats/GsDump/`) that replays PCSX2 `.gs` dumps as a **validation reference** for THAW rendering. It is not a user-facing converter — it exists to prove our THAW texture/blend/mesh decoding matches real hardware. Progress is gated against PCSX2 **SW-native** captures and committed `.gsoracle.json` / `.texoracle.json` converter-adjudication goldens. This stream is deep, self-contained, and lower-urgency than the converters.

**Primary sources:** `memory/gsdump_shadow_streaks_143551.md`, `memory/gsdump_overbrightness_not_green_tint.md`, `memory/gsdump_replay_pcrtc_handoff.md`, plus the `memory/gsdump_*` dead-end notes (read those first — they record what NOT to reattempt).

**Tooling:** PCSX2 at `<pcsx2>/pcsx2-qt.exe`; runbook `docs/runbooks/thaw-worldzone-pcsx2.md`; SW-native sweep/gate in `tools/validation/gsdump/native_reference_sweep.py` and `native_gate.py`; committed metric baseline in `tools/validation/gsdump/native_baseline.json`; oracle regeneration in `tools/validation/gsdump/build_gsoracle.ps1`.

---

## Retracted at 2026-07-26 audit (were "Remaining", now closed)

### ⚪ Shadow-decal vertical streaks (canonical capture 143551) — RED HERRING, closed
- Source: `memory/gsdump_shadow_streaks_143551.md`.
- **Retraction:** the streaks are gone at HEAD. The character drop-shadow is a PSMT4 subtractive ground decal and it **renders correctly** — there is no re-projection/streak defect to fix. The queued "force-latch-magenta-palette experiment" and the PCSX2 `GSClut` dirty-gating port are **stale — do not attempt them.** Cosmetic CLUT palette-count accounting remains bookkeeping only, with no visible artifact, and is not scheduled as fidelity work.

### ⚪ Residual over-brightness / "0.7× compression ceiling as a render bug" — retracted
- Source: `memory/gsdump_overbrightness_not_green_tint.md`, `memory/gsdump_reference_bias_native.md`.
- **Retraction:** the register-level fix already shipped (SW-blend truncation via `MathF.Floor` + `ClampByte`, `e294636`). The residual `0.70·pcsx2 + 22` slope is **NOT a render bug** — it is bias in the embedded-HW-screenshot reference (embedded slope ~0.16 vs SW-native ~0.73). Re-baselined against PCSX2 **SW-native ≥45s**, the renderer sits ~at tone parity. Not reopenable as a fidelity defect.

### ⚪ Magenta SPECIAL meter / PCRTC-composition gap — SOLVED, retracted
- **Retraction:** the "magenta meter" / HUD holes were a **float32 Z-interp overshoot past 2^24** (GEQUAL depth holes), fixed via double-precision Z interp + clamp to vertex extremes (`74a603b`, `2c078f9`; see Done). The full PCRTC-sim refactor the old notes queued is **unnecessary** — do not reattempt.

### ✅ GsDump regression suite
- The current default suite passes. Earlier fixed-count summaries such as “13/30 fail” or “30/30 pass” are historical snapshots, not a durable status metric; use the current test executable and oracle ratchets.

---

## Current status — no established replay residual

All work in this stream is internal to the replay reference; none is a user-facing converter defect.

### ✅ FBW-aware PSMCT16/16S render-target composition — completed 2026-08-10
- Source: `memory/gsdump_bloom_left_smear_tbw.md`, `memory/gsdump_z_swizzle_sweep_residual.md`.
- `GsRenderTargetCache.TryComposeSample` now uses the GS's 64×64 page rows for PSMCT16/16S, ceiling-covers partial pages, and preserves the established TBW-preferred then any-FBW fallback. PSMCT16 and PSMCT16S never cross-compose because their within-page block permutations differ.
- `GsRenderTargetCacheTests` supplies a non-zero `Ps2GsVram` placement oracle for both layouts and pins partial pages, FBW preference, and cross-layout rejection. The focused cache suite passes 8/8; the existing GS audit suite passes 30/30 and the three committed oracle ratchets remain green.
- All 17 audited captures replay successfully. The authoritative PCSX2 SW-native gate passes all 13 baseline captures (maximum slope delta 0.0002; maximum MAE delta +0.008, well inside its bands). The old embedded-reference score moves only trivially and is not the acceptance oracle. No quantization or vertical-band behavior changed.

---

## Done (for reference) ✅

- ✅ **SW-native metric gate** — `tools/validation/gsdump/native_reference_sweep.py`, `native_gate.py`, and committed `native_baseline.json` replace the biased embedded-HW screenshot scorecard (`c696daf`). The gate tracks per-capture slope/MAE bands and an aggregate baseline against PCSX2 SW-native output.
- ✅ **Programmatic GS→converter oracle coupling** — `GsDumpAuditRunner` builds a `GsTextureOracleComparer` and emits `.gsoracle.json` / `.texoracle.json`; committed goldens are consumed by `WorldzoneOracleTests` and `ThawZoneTexOracleTests` (`c696daf`, `36fda07`). The latter proves zero zone-TEX decode divergences across 17 captures after adjudicating the final two attribution artifacts.
- ✅ Per-(FBP,FBW,PSM) render-target buffers + PCRTC dual-circuit composition (`d86f183`).
- ✅ Spec-correct Z swizzle PSMZ32/24/16/16S; depth persistence to VRAM; depth-as-texture feedback (`8615bfc`, `f88114c`).
- ✅ Double-precision Z interp + clamp to vertex extremes — killed HUD "screen-door" holes (`74a603b`, `2c078f9`; `memory/gsdump_orb_holes_bezel_cutout.md`).
- ✅ GS blend/combine truncation (kill +0.5/layer over-brightness) (`e294636`).
- ✅ TBW-preferred RT-cache compose — killed phantom bloom left-haze (`f4b8a80`; `memory/gsdump_bloom_left_smear_tbw.md`).
- ✅ GS on-chip CLUT buffer model / TEX2 CLD semantics (`05d685e`).
- ✅ GS-alpha export-scaling split — see `formats-todo.md` / `memory/ps2_alpha_export_scale.md` (v1.2.1; the replay path keeps raw GS alpha).

## Proven dead-ends — do NOT reattempt ⚪

- ⚪ **Old PSMCT16 vertical-band experiment** — net regression, reverted (`memory/gsdump_psmct16_compose_dead_end.md`). It targeted 5-bit quantization and is distinct from the completed FBW/page-placement composer above.
- ⚪ **PSM-aware upload cache** — MAE-neutral / byte-identical; the VRAM-stomping hypothesis was disproven (`memory/gsdump_upload_cache_dead_end.md`). Do not reattempt without a PSM-aware re-decode design.
- ⚪ "Image shifted up" — PCSX2 16:9 widescreen letterbox, not a PCRTC bug (`memory/gsdump_pcsx2_16x9_letterbox.md`).
- ⚪ **Framebuffer-feedback triage** — closed, no fixes needed; the 0290 scramble is genuine game-side aliasing (`memory/gsdump_framebuffer_feedback_triage.md`).
- ⚪ "0.7× over-brightness compression ceiling" — a biased embedded-screenshot reference, NOT a register bug (see retraction above); the fixable SW-blend truncation already shipped (`e294636`).
