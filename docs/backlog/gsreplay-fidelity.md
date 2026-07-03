# Backlog — THAW GS-Replay Render Fidelity (research stream)

Created 2026-07-03. Distilled from the `memory/gsdump_*` topic files — **not re-verified this session**. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**What this stream is:** the software GS replay engine (`Core/Formats/GsDump/`) that replays PCSX2 `.gs` dumps as a **validation reference** for THAW rendering. It is not a user-facing converter — it exists to prove our THAW texture/blend/mesh decoding matches real hardware. Progress is measured as **MAE against PCSX2 screenshots** across a 14-capture sweep. This stream is deep, self-contained, and lower-urgency than the converters.

**Primary sources:** `memory/gsdump_shadow_streaks_143551.md`, `memory/gsdump_overbrightness_not_green_tint.md`, `memory/gsdump_replay_pcrtc_handoff.md`, plus the `memory/gsdump_*` dead-end notes (read those first — they record what NOT to reattempt).

**Tooling:** PCSX2 at `C:/Users/mmc99/Downloads/pcsx2-v2.3.218-...`; runbook `tools/diagnostics/thaw_pcsx2_runbook.md`; sweep `tools/diagnostics/sweep_compare.py`; baseline in `TestOutput/baseline` (mean MAE ~9.68 as of 2026-07-02).

---

## Remaining — needs work

### 🔶 Shadow-decal vertical streaks (canonical capture 143551)
- Source: `memory/gsdump_shadow_streaks_143551.md`.
- Evidence: the character drop-shadow is the mesh re-projected onto the ground, sampling the PSMT8@`0x2BC0` jeans texture through the `0x3590–0x359F` palette pool, alpha-keyed (ATST GEQUAL/1), blend=Cs (paints opaque). The **on-chip CLUT buffer model shipped** (`05d685e`: CLD 0–5, TEX2 register, ClutGeneration cache key) and is sweep-neutral + 30/30 tests — but streaks persist because our load-at-TEX0 snapshot captures the *darkish* palette re-uploaded at the same draw, while PCSX2 keeps the *earlier magenta* one via GSClut dirty-gating.
- What's left (from the memory file): (1) force-latch-magenta-palette experiment to confirm the hypothesis, then (2) port PCSX2's `GSClut` dirty / draw-dirty gating (CLD=1 skips reload when fields unchanged + storage "clean"; draw-writes set dirty=2 with early-return). Ruled out (don't reattempt): cache staleness, CT32↔PSMT8 VRAM visibility, CSM1 permutation, path loss.

### 🔶 Residual over-brightness (compression ceiling)
- Source: `memory/gsdump_overbrightness_not_green_tint.md`.
- Evidence: the dominant fixable cause was **fixed** — PCSX2 SW blend truncates (`mul16hs`) where we rounded (`MathF.Round`), a +0.5/blend lift; fix routed through `MathF.Floor` + `ClampByte`, sweep mean MAE −0.62. Residual `0.70·pcsx2 + 22` compression is characterized as an **irreducible content/quantization ceiling**, not a register bug. Bloom theory disproven; PABE never active; COLCLAMP-wrap regressed (don't reattempt).
- What's left: likely nothing register-level. Reopen only if a specific capture shows a non-content-ceiling delta.

---

## Done (for reference) ✅

- ✅ Per-(FBP,FBW,PSM) render-target buffers + PCRTC dual-circuit composition (`d86f183`).
- ✅ Spec-correct Z swizzle PSMZ32/24/16/16S; depth persistence to VRAM; depth-as-texture feedback (`8615bfc`, `f88114c`).
- ✅ Double-precision Z interp + clamp to vertex extremes — killed HUD "screen-door" holes (`74a603b`, `2c078f9`; `memory/gsdump_orb_holes_bezel_cutout.md`).
- ✅ GS blend/combine truncation (kill +0.5/layer over-brightness) (`e294636`).
- ✅ TBW-preferred RT-cache compose — killed phantom bloom left-haze (`f4b8a80`; `memory/gsdump_bloom_left_smear_tbw.md`).
- ✅ GS on-chip CLUT buffer model / TEX2 CLD semantics (`05d685e`).
- ✅ GS-alpha export-scaling split — see `formats-todo.md` / `memory/ps2_alpha_export_scale.md` (v1.2.1; the replay path keeps raw GS alpha).

## By design / won't-fix ⚪

- ⚪ "Image shifted up" — PCSX2 16:9 widescreen letterbox, not a PCRTC bug (`memory/gsdump_pcsx2_16x9_letterbox.md`).
- ⚪ Residual over-brightness compression ceiling — content/quantization, not a register bug.
- ⚪ Upload-cache / VRAM-stomping approaches — disproven dead ends (`memory/gsdump_upload_cache_dead_end.md`, `memory/gsdump_psmct16_compose_dead_end.md`). Do not reattempt without a new hypothesis.
