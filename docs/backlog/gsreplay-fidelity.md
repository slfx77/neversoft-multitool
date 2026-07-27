# Backlog — THAW GS-Replay Render Fidelity (research stream)

Created 2026-07-03. Distilled from the `memory/gsdump_*` topic files. **Re-verified 2026-07-26 vs HEAD (v1.3.4, 60d0b81) — full-domain audit.** The original "Remaining" items below were superseded by later `memory/gsdump_*` notes; see the retractions inline. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**What this stream is:** the software GS replay engine (`Core/Formats/GsDump/`) that replays PCSX2 `.gs` dumps as a **validation reference** for THAW rendering. It is not a user-facing converter — it exists to prove our THAW texture/blend/mesh decoding matches real hardware. Progress is measured as **MAE against PCSX2 screenshots** across a 14-capture sweep. This stream is deep, self-contained, and lower-urgency than the converters.

**Primary sources:** `memory/gsdump_shadow_streaks_143551.md`, `memory/gsdump_overbrightness_not_green_tint.md`, `memory/gsdump_replay_pcrtc_handoff.md`, plus the `memory/gsdump_*` dead-end notes (read those first — they record what NOT to reattempt).

**Tooling:** PCSX2 at `C:/Users/mmc99/Downloads/pcsx2-v2.3.218-...`; runbook `tools/diagnostics/thaw_pcsx2_runbook.md`; sweep `tools/diagnostics/sweep_compare.py`; baseline in `TestOutput/baseline` (mean MAE ~9.68 as of 2026-07-02).

---

## Retracted at 2026-07-26 audit (were "Remaining", now closed)

### ⚪ Shadow-decal vertical streaks (canonical capture 143551) — RED HERRING, closed
- Source: `memory/gsdump_shadow_streaks_143551.md`.
- **Retraction:** the streaks are gone at HEAD. The character drop-shadow is a PSMT4 subtractive ground decal and it **renders correctly** — there is no re-projection/streak defect to fix. The queued "force-latch-magenta-palette experiment" and the PCSX2 `GSClut` dirty-gating port are **stale — do not attempt them.** All that remains is cosmetic CLUT palette-count accounting (bookkeeping only, no visible artifact); tracked under Remaining below.

### ⚪ Residual over-brightness / "0.7× compression ceiling as a render bug" — retracted
- Source: `memory/gsdump_overbrightness_not_green_tint.md`, `memory/gsdump_reference_bias_native.md`.
- **Retraction:** the register-level fix already shipped (SW-blend truncation via `MathF.Floor` + `ClampByte`, `e294636`). The residual `0.70·pcsx2 + 22` slope is **NOT a render bug** — it is bias in the embedded-HW-screenshot reference (embedded slope ~0.16 vs SW-native ~0.73). Re-baselined against PCSX2 **SW-native ≥45s**, the renderer sits ~at tone parity. Not reopenable as a fidelity defect.

### ⚪ Magenta SPECIAL meter / PCRTC-composition gap — SOLVED, retracted
- **Retraction:** the "magenta meter" / HUD holes were a **float32 Z-interp overshoot past 2^24** (GEQUAL depth holes), fixed via double-precision Z interp + clamp to vertex extremes (`74a603b`, `2c078f9`; see Done). The full PCRTC-sim refactor the old notes queued is **unnecessary** — do not reattempt.

### 🟢 GsDump test suite
- **30/30 pass at HEAD** (any earlier "13/30 fail" reading is stale).

---

## Remaining — needs work (internal, LOW urgency)

All items here are internal to the replay reference — none are user-facing converter defects.

### 🔶 FBW-aliased bloom (PSMCT16/16S)
- Source: `memory/gsdump_bloom_left_smear_tbw.md`, `memory/gsdump_z_swizzle_sweep_residual.md`.
- Evidence: bloom compose still lacks **per-(FBP,FBW) separation** for PSMCT16/16S buffers, so same-FBP-different-FBW targets alias. The TBW-preferred RT-cache compose (`f4b8a80`) removed the phantom left-haze, but the underlying FBW aliasing gap remains.
- What's left: per-(FBP,FBW) buffer separation for the 16-bit bloom path (per the PCRTC handoff phases B/C/D).

### 🔶 Biased reference metric (not a trustworthy scorecard)
- Source: `memory/gsdump_reference_bias_native.md`.
- Evidence: the 14-capture sweep MAE is measured against embedded-HW screenshots whose tone is compressed (slope ~0.16), so the number does not track true render error. Re-baseline every comparison against PCSX2 **SW-native ≥45s** captures before trusting a delta.
- What's left: a fixed, SW-native metric so regressions/wins are measurable.

### 🔴 No programmatic oracle→converter coupling (most useful gap)
- Evidence: the replay is **human-read** — nothing imports `Core/Formats/GsDump/` into the converters or tests. So GS-verified facts (blend modes, swizzle, alpha) cannot automatically confirm or refute a converter's output.
- What's left: wire the replay as a programmatic oracle (e.g. converter output ↔ replay pixel checks) plus the fixed SW-native metric above — together these are what would let the **THAW blend-mode claims be VERIFIED, not just implemented**.

---

## Done (for reference) ✅

- ✅ Per-(FBP,FBW,PSM) render-target buffers + PCRTC dual-circuit composition (`d86f183`).
- ✅ Spec-correct Z swizzle PSMZ32/24/16/16S; depth persistence to VRAM; depth-as-texture feedback (`8615bfc`, `f88114c`).
- ✅ Double-precision Z interp + clamp to vertex extremes — killed HUD "screen-door" holes (`74a603b`, `2c078f9`; `memory/gsdump_orb_holes_bezel_cutout.md`).
- ✅ GS blend/combine truncation (kill +0.5/layer over-brightness) (`e294636`).
- ✅ TBW-preferred RT-cache compose — killed phantom bloom left-haze (`f4b8a80`; `memory/gsdump_bloom_left_smear_tbw.md`).
- ✅ GS on-chip CLUT buffer model / TEX2 CLD semantics (`05d685e`).
- ✅ GS-alpha export-scaling split — see `formats-todo.md` / `memory/ps2_alpha_export_scale.md` (v1.2.1; the replay path keeps raw GS alpha).

## Proven dead-ends — do NOT reattempt ⚪

- ⚪ **PSMCT16 RT-compose** — net regression, reverted (`memory/gsdump_psmct16_compose_dead_end.md`). The "vertical bands" it targeted are 5-bit quantization.
- ⚪ **PSM-aware upload cache** — MAE-neutral / byte-identical; the VRAM-stomping hypothesis was disproven (`memory/gsdump_upload_cache_dead_end.md`). Do not reattempt without a PSM-aware re-decode design.
- ⚪ "Image shifted up" — PCSX2 16:9 widescreen letterbox, not a PCRTC bug (`memory/gsdump_pcsx2_16x9_letterbox.md`).
- ⚪ **Framebuffer-feedback triage** — closed, no fixes needed; the 0290 scramble is genuine game-side aliasing (`memory/gsdump_framebuffer_feedback_triage.md`).
- ⚪ "0.7× over-brightness compression ceiling" — a biased embedded-screenshot reference, NOT a register bug (see retraction above); the fixable SW-blend truncation already shipped (`e294636`).
