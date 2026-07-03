# Backlog — Unimplemented / Deferred Formats

Created 2026-07-03. Distilled from `CLAUDE.md` (*Deferred Items* / *Not Yet Implemented*) + `memory/` — **not re-verified this session**. See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔴 THAW `.tex.ps2` scene texture metadata (NOT the same as THUG TEX)
- Source: `CLAUDE.md` → *Not Yet Implemented*.
- Evidence: 328 files, each a companion to the same-named `.skin.ps2`. Header = model checksum + per-texture entries with GS register values (TEX0, MIPTBP), dimensions, and texture checksums. The PC equivalent (`.tex.wpc`) uses `0xABADD00D` magic with DXT-compressed pixel data. Internal texture checksums are **not** QbKey hashes.
- What's left: parse the metadata (currently the worldzone/skin paths resolve textures by TBP/CBP or via the companion TEX pool rather than this per-model metadata file). Low urgency — textures already resolve for skins/worldzones through other paths; this would add explicit per-model texture binding.

### 🔶 STR (PS1 MDEC) video — VLC drift on longer streams
- Source: `memory/str_mdec_decoder_status.md`.
- Evidence: IDCT, YCbCr→RGB, block ordering, and the VLC table are all verified identical to jpsxdec — but **VLC decompression drifts after ~600 blocks / ~2617 codes**. Notably both our code AND jpsxdec's standalone `makeV2` fail the same way; only jpsxdec's full-disc pipeline succeeds, suggesting the bug is in stream framing / sector assembly rather than the codec core.
- What's left: diff our sector/stream assembly against jpsxdec's full pipeline (`Sample/jpsxdec_v2.0/`, source compiled under `Sample/jpsxdec/`). STR is listed as a supported format and converts short clips; this is a correctness gap on longer content, not a total failure.

### 🔴 PPV runtime container (Spider-Man PSX prototype)
- Source: `CLAUDE.md` → *Deferred Items* → *Unsupported Game Asset Formats*.
- Evidence: `BVmC` magic; 14 files under `WTC/SOUNDS` in *Spider-Man (2000-2-4, PSX — Prototype)*. Appears to be a real runtime media container (audio-first), not a tooling artifact. No in-repo or open-source parser reference yet.
- What's left: research the `BVmC` container (treat as audio-first). Deferred pending a reference or a decision it's worth the reverse-engineering effort. Low priority (14 proto-only files).

### 🔴 THPG / Project 8 `.col` (newer collision version)
- Cross-ref: `game-thpg-p8.md` (full evidence there). Newer `.col` container (`0x00FF00FF`-prefixed) not decoded. Listed here too because it's a format gap, not just a per-game gap.

---

## Done (for reference) ✅

- ✅ GS-alpha export scaling (128=opaque → PNG 255=opaque) — `memory/ps2_alpha_export_scale.md` (v1.2.1). `DecodePixels(rawGsAlpha)`: export scales ×255/128, GS replay keeps raw.
- ✅ VID1 (THAW GameCube movie container) → MP4 — shipped (`vid` CLI command + Video Converter tab); the old `CLAUDE.md` "Deferred > VID" note predates it.

## By design / won't-fix ⚪

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums (`CLAUDE.md` → QBKey section; `tools/ghidra/thps2-psx-proto/output/psx_decompiled.c`). GHIDRA string extraction found 0 texture matches across 15 executables. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).
