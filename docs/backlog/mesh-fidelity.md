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

### 🔴 Blender 4.4/5.x glTF importer randomly rejects our PSX GLBs (upstream bug, S)
- Source: 2026-07-27 user-report investigation (Venom/Symbiote). `io_scene_gltf2` `mesh.py do_primitives`
  builds the custom-attribute name list as a Python **set** but appends the per-attribute data arrays in
  first-seen order, then zips them with `enumerate(set)`. With our TWO custom attributes
  (`_PSX_COLOR_0` VEC4 + `_PSX_FLAGS_0` VEC3) the set's hash-randomized iteration order mismatches the
  append order in ~half of Blender processes → `ValueError: … size 4 and … size 3` and the import aborts.
  Same GLB imports fine on the next attempt (per-process hash seed).
- What's left: report upstream to Blender; consider an exporter-side dodge (a single custom attribute —
  e.g. flags packed into a fourth component pair — would make `enumerate` order trivially correct). The
  `.blend` export path is unaffected and remains the sanctioned Blender route.

### ✅ PS1 lit-flagged faces now export neutral modulation (2026-07-27)
- Source: decomp reading (`thps2-psx-proto/src/M3D.cpp` ~line 1041): when `pModel->Flags & 4` (or item
  flag `0x80`) the PS1 engine loads the owner light + colour matrices and calls
  `M3dAsm_GetDynamicLighting(normals, numNormals)` — per-vertex colours are computed from normals and
  ProcessPolys draws face-flag-bit-2 faces from that table, bypassing their serialized colours. Same
  semantic as the v6 PC rule, but the PS1 engine applies it PER FACE (characters are mixed: venom
  394/414 lit, docock 371/539, torch 323/596 — the unlit remainder keeps authored colours).
- Shipped: `ComputePsxFaceColors` neutralizes `mesh.UsesDynamicLighting && (face.Flags & 0x0004)` faces
  for every version (the v6 per-mesh loader-derived bypass is unchanged above it). Corpus census via the
  new `tools/PsxAnalyzer lit-census`: lit faces are characters/FE props ONLY — every `_g`/bare-stem
  level, `_o` bank (except l8a5_o's 23 faces), and items.psx/pickup file has ZERO lit faces, so baked
  level lighting and the user-validated pulse pickups are untouched (byte-identical GLBs verified for
  l1a1_g/items/symbiote; venom 469 verts 0.83→0.95 neutral with 44 authored kept; spidey's authored
  255-flat — previously CLAMPED to 1.0 in GLB and exported as 1.99 raw overbright into .blend — now
  neutral 0.95 in both).

### 🔶 `.blend` vs GLB first-frame channel anchoring residual (S)
- Source: 2026-07-27 verification sweep. After the out-of-order-skeleton fix, spidey `anim_20`/`anim_30`
  still differ from the GLB by 6.7–9.5% in posed extent at frame 0 ONLY (mid-frames exact to 4 decimals;
  venom/docock/symbiote exact everywhere). Frame ranges are identical in both files, so this is a
  first-key anchoring difference between the C# GLB channel writer and the .blend package writer, not an
  importer bug.
- What's left: diff `PsxAnimationChannelWriter` vs `BlendPackageWriter` first-key emission for clips whose
  streams start after t=0.

---

## Done (for reference) ✅

- ✅ **THPS2-DC feedback batch (2026-07-28)** — seven PSX rendering reports resolved in one pass:
  - **THPS pickups half-buried** — `CPowerUp::DoPhysics` snaps only on the `mDropping` path (matched decomp);
    THPS/Apocalypse pickups now render at authored TRG Y (SKPH: 19/19 mapped POWERUP nodes at dY=0.00).
  - **Sky domes z-clipping into levels (SKMAR/SKBUL/Skate Heaven)** — `PsxSkyDomeClassifier` geometric
    tagging (≥0.7× level footprint both axes + centroid + dome height) → `sky__` prefix + `neversoftSky`
    extras; viewer draws sky first with depthWrite off, excludes it from framing/walk-ground; Blender
    NeversoftSky collection. Resolved names confirm precision: `sky01`, `bul_sky`, `bak_sphr01`; zero
    false positives across 8 DC levels + Spider-Man.
  - **SKB2 water z-fight** — both scrolling sheets share one plane; deterministic layer lift steps
    (sole-animated on top, else PS1 OT insertion order).
  - **Webdome physical cracks** — the per-face semi-trans lift tore curved connected surfaces; lifts now
    move along position-averaged directions (0 torn pairs post-fix, exact corner coincidence restored).
  - **SKBUL sky-dome shading bands** — 32 texture-band primitives each shaded as a facet island
    (exporter smoother is per-primitive); `PsxNormalWelder` welds 60°-thresholded normals across
    positions for meshes without per-vertex normals.
  - **DC chain-link fences too transparent** — markerless 16-bit binary-cutout textures keep authored
    alpha instead of the uniform ABR-0 50% wash.
  - **control.psx stick colors** — all 892 faces are lit-flagged; the PS1 loader ORs face bit2 into
    SModel.Flags (decomp §5.3), so the lit-face neutral rule (2026-07-27, uncommitted) is the fix;
    verified 2126/2126 vertex colors at exact neutral.
  - **"Missing" leaves/antennas/rail posts — RETRACTED as a converter bug**: 10/10 PS1 binaries carry the
    identical unconditional bit7 loader XOR + 0xC0 renderer skip (byte-level scan,
    `tools/diagnostics/psx_bit7_loader_scan.py`); TRG cross-ref proves the dropped whole meshes are
    COMMANDPOINT trigger volumes / GapPolyHit detectors / camera zones (skph 155/162 COMMANDPOINTs bind
    dropped meshes; l2a1 37/47; the verified-hidden THPS1 skdown control shows the same 130/132 pattern).
    The visible trees/towers are separate meshes the converter already renders. Remaining follow-ups below.

- ✅ **`.blend` out-of-order PSX skeletons mis-rooted (Venom stretched face/arm/tongue)** — 2026-07-27.
  `import_package.py _make_armatures` chained world binds in one array-ordered pass (`parent_index < i`);
  PSX HIER skeletons store children before parents (venom: 9/21 forward refs), so those bones' rest heads
  landed at their bare local offset (tongue/jaw/forearm up to 0.45× model extent off) and every rotating
  pose pivoted about the wrong head. Fixed with memoized recursive resolution (cycle-guarded). Venom:
  17/21 mismatched heads → 0; posed/bind extents now match the GLB to 4 decimals on every sampled clip;
  spidey (also silently affected) 14/16 samples exact. Regression test hardened to a forward-ParentIndex
  skeleton + rotating clip (fails pre-fix at 2.2× bind size, passes post-fix).
- ✅ **`.blend` "vertex colors too dark" (Symbiote)** — 2026-07-27. NOT a data bug: .blend loop colours
  byte-match the GLB COLOR_0 at all 1,002 loops, and the unlit-emission node math reproduces the PS1
  display-domain modulation. Two real contributors fixed/explained: (1) exported scenes now save with the
  **Standard** view transform — factory AgX is a scene-referred photographic tonemap that crushed the dark
  authored gouraud bake by a measured 2.3× mid-tone luminance; (2) the symbiote's darkness is authored
  (0/229 lit faces → the engine draws its serialized RGBs verbatim; no colour pulses) — the app viewer
  merely brightens it with its lit hemisphere rig.

- ✅ THAW worldzone level-MDL leaf format (phase 420) — `memory/thaw_worldzone_phase420_solved.md`; K-offset derivation + per-leaf VIF slicing + per-batch GS context + billboards.
- ✅ THAW worldzone discovery inside DATAP.WAD — `memory/thaw_worldzone_archive_discovery.md` (v1.2.1).
- ✅ THAW worldzone billboards as camera-facing quads in `.blend` — `memory/thaw_worldzone_billboards.md` (the `.blend` path; glTF stays axis-aligned).

## By design / won't-fix ⚪

- ⚪ Worldzone triangle-efficiency cap (ADC degenerate suppression) — we render exactly what the engine rasterizes.
- ⚪ Camera-facing billboards in **glTF** — no static-glTF way to do view-facing quads; the `.blend` export handles it via Track-To constraints.
