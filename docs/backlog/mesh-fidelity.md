# Backlog — Mesh Reconstruction Fidelity

Created 2026-07-03; **sweep-verified 2026-07-03**. **Re-verified 2026-07-26 vs HEAD (v1.3.4, 60d0b81) — full-domain audit.** See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Cross-ref:** `game-thpg-p8.md` (the whole-character `(1,8,8)` "skin garble" is RESOLVED — those wrapping files are unused old-scale legacy exports the game never loads; in-game CAS pieces decode exactly at 95.6% oracle), `memory/thaw_worldzone_phase420_solved.md`.

> **⚠ The `CLAUDE.md` / `memory/` mesh-fidelity notes are STALE.** A 2026-07-03 conversion+render sweep (below) shows the character-mesh paths that those notes call "garbled / missing parts" now render **correctly** at HEAD. Trust the sweep, not the older prose. Corrected the same day I first (wrongly) transcribed the stale notes into this file — same lesson as the `README.md` "tested games" overstatement.

---

## Verified GOOD at HEAD (🟢 2026-07-03 sweep; re-confirmed 2026-07-26 vs v1.3.4)

Conversion + `glb-render` inspection, current HEAD (post-v1.2.1 alpha fix `884d018`):

- **THAW PS2 character skins — whole corpus clean.** `mesh` over the full THAW `DATAP/models` tree: **332/332 files convert, 227,487 tris, 0 failures, 0 anomalies** (no 0-tri, no glTF errors). All 41 `skater_pro` skins convert with sane counts.
- **Chad Muska (the reported case) is FIXED.** Front/back/side renders show hair present + solid torso; **3,488 tris vs PC 3,491 = 99.9% recall.** The reported "missing hair + hole in torso" was the GS-alpha export regression (`884d018` / v1.2.1), **not** a geometry gap — both symptoms were the over-culled alpha mask.
- **The documented THAW worst-cases are resolved.** `skater_lasek` now **3,070/3,070 tris (100% recall**, up from the 2,930/95% in `memory/thaw_ps2_skin_format.md`) and renders clean. `pro_vallely_head` renders a complete head (the "PC Mesh 2 entirely absent" note is stale).
- **PSX (PS1) character models render correctly** — contradicting the `CLAUDE.md` "garbled / misaligned body parts" note (CLAUDE.md corrected 2026-07-07). Spider-Man `blackcat`/`carnage` and THPS2 `burnq2`/`cab` all convert + render as complete, correct characters. **Full-corpus confirmation (2026-07-07)**: five-build character sweep (Apocalypse 112, THPS1 63, THPS2 152, Spider-Man 60, SM2EE 103 = 490 converted) with zero real failures — every non-conversion is a texture-only costume file (`cost*`, `bits.psx`) correctly reporting "No mesh data". Era-spanning renders verified: Bruce (1998), Burnquist (1999), Hawk (2000), Spidey (2000). Remaining PS1-era work is animation-side, not meshes.

**Takeaway:** the meshes we *claim* to support are in good shape — **no genuinely-broken character-skin path remains at HEAD.** The old "broken Proving Ground (2007) character skins" framing is RETRACTED: THPG skins decode to complete characters (95.6% oracle via `ThpgPositionUnwrapper`), and the wrapping whole-character `(1,8,8)` files that once looked garbled are unused old-scale legacy exports the game never loads — in-game CAS pieces are piece-local Q4.12 and decode exactly (`game-thpg-p8.md`, GS-dump/savestate proof; `memory/thpg_skin_decode.md`). The remaining real mesh work is the genuinely-open items below — texture-stage/terrain reconstruction, filename-free PSX appendage discovery, and turning "good on a broad sample" into "proven across the corpus" via the QA harness.

---

## Remaining — needs work

### 🔶 Spider-Man PSX runtime appendages — replace asset-specific discovery
- Source: 2026-07-17 archive-backed audit after implementing animated Scorpion/Doc Ock spline reconstruction.
- Current state: controller chains are discovered structurally, but Doc Ock still loads the literal sibling `claw.psx`, assumes object/mesh zero, and Scorpion uses tip-mesh checksum `0xAF6C87FE` to reject the prototype Lizard's abandoned seven-controller rig. The generated tube also deliberately approximates the unknown runtime tessellation.
- Evidence: `l8a4_o.psx` mesh 8 has the same 28-vertex / 22-face claw topology and hidden UV-template records as standalone `claw.psx`; `l8a6_o.psx` carries a related tip variant. A corpus scan found the complete standalone appendage-kit signature to be structurally distinctive. No direct character-rig → payload reference has yet been identified in the PSX files or THPS2 decomp.
- What's left: enumerate sibling PSX assets, discover the unique appendage kit from drawable tip geometry + hidden STP UV template + unused square strip texture, carry discovered object/mesh indices through the writer, and fall back to tubes when discovery is ambiguous. Replace Scorpion's checksum with animation-aware controller-parentage and endpoint-tip validation so the Lizard rig remains rejected without an asset identifier. Cache discovery per archive/directory.

### 🔴 PC/Xbox THAW multi-pass texture-stage baking (M, user-facing)
- Source: 2026-07-26 audit. Matches CLAUDE.md's Not-Yet-Implemented entry (still current/correct).
- Current state: `XbxGeometryWriter` exports `Passes[0]` only; pass-k (k≥1) stage-blend overlays are dropped — e.g. `ped_boone_full`'s spider tattoo `Cut_MBF_Boone_Tat01` is referenced only as pass 1 of material `0x71894AA9` and never reaches the GLB.
- What's left: composite pass-k PNG over pass-0 by pass-k alpha and register under a synthetic checksum (same pattern as `Ps2GeomDestinationAlphaSynthesis` synthetic textures). PS2 already handles more of this than PC/Xbox.

### 🔴 PC/Xbox THAW additive/subtractive blend not approximated (S, user-facing)
- Source: 2026-07-26 audit. The PS2 path already classifies + luminance-approximates additive/subtractive (`Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode`, baked in both `GltfModelExporter.cs:546-578` and `import_package.py`); the PC/Xbox scene path renders those materials as plain source-over (washed/dark).
- What's left: reuse the PS2 luminance-to-alpha approximation in the Xbox/PC material path. Small — the algorithm exists.

### 🔴 PS2 worldzone multi-pass terrain not reconstructed (L, user-facing)
- Source: 2026-07-26 audit — the sharper form of the older "missing parts" worldzone note.
- Current state: overlapping GS terrain passes (multi-layer blends) z-fight or get suppressed rather than being composited/separated; level layout is present but multi-pass surfaces don't reconstruct faithfully.
- What's left: reconstruct the multi-pass terrain draw order (per-pass GS context already parsed) so overlapping passes composite instead of colliding. The GS software-replay oracle helps most here — but is not yet programmatically coupled to the converter (see the GS-renderer notes; `gsreplay-fidelity.md`).

### 🔴 THAW PS2 zone-TEX wrong swizzle/offset for some subgroups (L, user-facing)
- Source: 2026-07-26 audit; `memory/thaw_zone_tex_debugging.md`.
- Current state: some worldzone-texture subgroups decode with the wrong swizzle/offset → garbled level textures.
- What's left: PC-baselined per-subgroup swizzle survey; the GS oracle is the strongest ground truth here.

### 🔴 Build a mesh-QA regression harness (the real remaining fidelity work)
- Source: this session — the fidelity story is currently "looks good when spot-checked," which is how the stale notes above went unnoticed.
- What's left: a repeatable sweep that, across every supported mesh format + game, (1) batch-converts and flags hard failures (0-tri, glTF-validator errors), (2) where a PC/`.wpc` or other ground truth exists, computes **triangle recall** and flags files below a threshold, and (3) emits a render **contact sheet** for eyeball review. Wire it as a `tools/diagnostics/` script (Python over the CLI + `glb-render`) so regressions like the alpha bug are caught mechanically, not by a user noticing a hole months later. This is the durable answer to "are the meshes we claim to support actually perfect."

### 🔶 THAW worldzone level geometry — "missing parts" (NOT re-verified this session)
- Source: `memory/thaw_worldzone_phase420_solved.md` (user visual feedback after phase-420: *"level layout is now there, looks accurate, mostly correct textures… still missing parts"*).
- Evidence: the level-MDL leaf sub-chunk format is solved and 3,977/3,977 leaves parse (z_bh: 49,935 tris, validator-clean). Known residual limits documented in that memory file:
  - **Triangle-efficiency cap** — Format-A leaves average ~12 drawn tris after ADC degenerate suppression; we can't extract more than the engine draws (⚪ by design).
  - **Billboards not camera-facing** — Format-B quads are axis-aligned, not view-facing (glTF limitation without `KHR_materials_unlit` + viewer support).
  - **5 small object MDLs skipped** in z_bh (`FindMdlVifStart` rejects 880–2,944-byte props as "not a PAK MDL").
- What's left: the "missing parts" is now **subjective** — needs the user to point at specific regions. The one cleanly-identifiable lead is the 5 skipped small object MDLs. Everything else may be the by-design tri cap. Reopen only with a specific visual target.

### 🔶 Cross-cutting: `.blend` limb-stretch fix is PSX-only (needs generalizing)
- Source: 2026-07-26 audit. The v1.3.4 `.blend` limb-stretch fix was gated `SourceKind == "Psx"`.
- Current state: THAW/PS2 **and** THPS3 skinned characters share the SAME latent double-translation `.blend` stretch that the PSX fix addressed — they just aren't covered by the PSX-gated path.
- What's left: rework the fix into the general `matrix_basis` form (not PSX-gated) and validate against a real THAW/PS2 or THPS3 rig before shipping.

---

## Done (for reference) ✅

- ✅ THAW worldzone level-MDL leaf format (phase 420) — `memory/thaw_worldzone_phase420_solved.md`; K-offset derivation + per-leaf VIF slicing + per-batch GS context + billboards.
- ✅ THAW worldzone discovery inside DATAP.WAD — `memory/thaw_worldzone_archive_discovery.md` (v1.2.1).
- ✅ THAW worldzone billboards as camera-facing quads in `.blend` — `memory/thaw_worldzone_billboards.md` (the `.blend` path; glTF stays axis-aligned).

## By design / won't-fix ⚪

- ⚪ Worldzone triangle-efficiency cap (ADC degenerate suppression) — we render exactly what the engine rasterizes.
- ⚪ Camera-facing billboards in **glTF** — no static-glTF way to do view-facing quads; the `.blend` export handles it via Track-To constraints.
