# Validation tools

This tree contains reproducible checks that exercise the application against an
independent runtime, renderer, corpus, or UI surface.

- `gsdump/`: PCSX2/native GS replay capture and comparison.
- `mesh/`: glTF inspection/rendering and PS2 texture regressions.
- `thaw-zone-texture/`: slim archive/provenance triage CLI used when a texture
  oracle ratchet fails.
- `video/`: optional VID1 coefficient comparison.
- `discs/`, `gui/`, and `support/`: disc, GUI-smoke, and corpus-support checks.

Generated captures, renders, and reports belong under `TestOutput/`.
