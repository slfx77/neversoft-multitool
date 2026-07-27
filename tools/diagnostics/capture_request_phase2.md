# Phase-2 capture request — THAW GS oracle coverage

Six PCSX2 capture targets to widen the GS-oracle's texture/blend coverage beyond
the existing 13 THAW dumps. Each capture feeds the zone-TEX ground-truth census
and the worldzone blend/draw-order adjudication (Phase 3).

## Setup (same for every capture)

- **Binary:** `C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\pcsx2-qt.exe`
  (the newer v2.3.218 cannot load THAW `.gs` freeze data — do not use it).
- **Renderer: Software** (Settings → Graphics → Renderer = Software) — REQUIRED,
  so the dump's embedded screenshot is the SW-presented frame (unbiased reference).
- Game: THAW Collector's Edition (SLUS-21295), same disc image as the existing dumps.
- Stand still at the described spot (no camera drift), then press the GS-dump
  hotkey (F9-family — writes `.gs` into `Documents\PCSX2\snaps\`) AND take a
  screenshot in the same pose.
- Afterwards, note which capture is which (timestamp → description) — a one-line
  list is enough; I'll ingest and tag them.

## Targets

1. **z_bh main street, daytime, wide view** — canonical worldzone; terrain
   compositing + stagger adjudication.
2. **z_bh vegetation-heavy spot** (trees/bushes filling the frame) — MASK/afail
   coverage.
3. **z_dt at night with lit signage** (neon/glow in frame) — additive-blend
   coverage (a=0,b=2,d=1 draws) + NightOverlay layer.
4. **z_sr or z_ho, wide view** — a second zone-TEX corpus file (PSMT4/PSMT8 +
   CLUT variants beyond z_bh's).
5. **Terrain decal close-up, low camera angle** (road markings/stains on
   pavement) — coplanar BLEND pass ordering, the depth-bias/stagger case.
6. *(Optional)* **z_lv casino interior** — PSMCT16 direct + TEXA coverage.

## After capture (my side, not yours)

`tools/diagnostics/capture_all_native.ps1` (≥45 s per capture, SW per-draw RT
dumps) + the oracle regen script rebuild the goldens; nothing else needed from
the capture session.
