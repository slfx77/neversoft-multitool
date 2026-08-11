# Backlog — Mesh Reconstruction Fidelity

Created 2026-07-03; **sweep-verified 2026-07-03**. **Re-verified 2026-08-10 against the current writers, validation tests, and retained corpus evidence.** See `BACKLOG_SUMMARY.md`.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

**Cross-ref:** `game-thpg-p8.md` (the whole-character `(1,8,8)` "skin garble" is RESOLVED — those wrapping files are unused old-scale legacy exports the game never loads; shipped piece-local CAS assets decode exactly, while legacy recovery reaches 95.6% against the P8 oracle), `memory/thaw_worldzone_phase420_solved.md`.

> **⚠ The `CLAUDE.md` / `memory/` mesh-fidelity notes are STALE.** A 2026-07-03 conversion+render sweep (below) shows the character-mesh paths that those notes call "garbled / missing parts" now render **correctly** at HEAD. Trust the sweep, not the older prose. Corrected the same day I first (wrongly) transcribed the stale notes into this file — same lesson as the `README.md` "tested games" overstatement.

---

## Verified GOOD at HEAD (🟢 2026-07-03 sweep; re-confirmed 2026-07-26 vs v1.3.4)

Conversion + `glb-render` inspection, current HEAD (post-v1.2.1 alpha fix `884d018`):

- **THAW PS2 character skins — whole corpus clean.** `mesh` over the full THAW `DATAP/models` tree: **332/332 files convert, 227,487 tris, 0 failures, 0 anomalies** (no 0-tri, no glTF errors). All 41 `skater_pro` skins convert with sane counts.
- **Chad Muska (the reported case) is FIXED.** Front/back/side renders show hair present + solid torso; **3,488 tris vs PC 3,491 = 99.9% recall.** The reported "missing hair + hole in torso" was the GS-alpha export regression (`884d018` / v1.2.1), **not** a geometry gap — both symptoms were the over-culled alpha mask.
- **The documented THAW worst-cases are resolved.** `skater_lasek` now **3,070/3,070 tris (100% recall**, up from the 2,930/95% in `memory/thaw_ps2_skin_format.md`) and renders clean. `pro_vallely_head` renders a complete head (the "PC Mesh 2 entirely absent" note is stale).
- **PSX (PS1) character models render correctly** — contradicting the `CLAUDE.md` "garbled / misaligned body parts" note (CLAUDE.md corrected 2026-07-07). Spider-Man `blackcat`/`carnage` and THPS2 `burnq2`/`cab` all convert + render as complete, correct characters. **Full-corpus confirmation (2026-07-07)**: five-build character sweep (Apocalypse 112, THPS1 63, THPS2 152, Spider-Man 60, SM2EE 103 = 490 converted) with zero real failures — every non-conversion is a texture-only costume file (`cost*`, `bits.psx`) correctly reporting "No mesh data". Era-spanning renders verified: Bruce (1998), Burnquist (1999), Hawk (2000), Spidey (2000). Remaining PS1-era work is animation-side, not meshes.

**Takeaway:** the meshes we *claim* to support are in good shape — **no genuinely-broken character-skin path remains at HEAD.** The old "broken Proving Ground (2007) character skins" framing is RETRACTED: the wrapping whole-character `(1,8,8)` files are unused old-scale legacy exports the game never loads; shipped piece-local CAS assets decode exactly (`game-thpg-p8.md`, GS-dump/savestate proof; `memory/thpg_skin_decode.md`). PC/Xbox multipass, ADD/SUB baking, PS2 worldzone submission ordering, the zone-TEX oracle investigation, THAW compact-prop QB instancing, filename-free PSX appendage discovery, the general Blender pose basis, and the all-format QA orchestrator have also closed since the earlier audit. The remaining mesh work is the narrower set of specific PSX visual/audit findings below.

---

## Completed in the 2026-08-10 re-audit

### ✅ Blender 4.4/5.x PSX-glTF import crash — RESOLVED with lossless standard carriers
- Source: the Venom/Symbiote report. Blender's `io_scene_gltf2` importer builds custom-attribute names
  in a hash-randomized Python set but retains their arrays in discovery order. The old
  `_PSX_COLOR_0` VEC4 + `_PSX_FLAGS_0` VEC3 files failed in 5 of 12 fresh Blender 5.1 processes with
  a VEC4/VEC3 concatenation error. Equalizing shapes was rejected because it would replace the crash
  with silent semantic permutation.
- New PSX GLBs expose `_PSX_COLOR_0` as their **sole custom semantic**. Normalized-U16 `COLOR_1`
  carries the textured/Gouraud/packet-valid bits plus an exact one-byte pulse channel in alpha;
  `TEXCOORD_1..3` carry velocity, frequency + packed amplitude/phase nibbles, and texture size. The
  second component of each application UV is pre-flipped for Blender, and logical size `(0,0)` is the
  no-wibble sentinel. Mesh extras version the carrier so ordinary multi-colour/multi-UV glTFs are not
  misclassified. The viewer synthesizes its historical runtime attributes only for marked meshes;
  both it and `GlbModelLoader` retain legacy `_PSX_FLAGS_0` / `_PSX_UV_WIBBLE_*` read compatibility.
- The committed synthetic regression checks exact packet colour, flags, pulse, signed velocities,
  packed wave, dimensions, marker placement, and layer schemas in 12 fresh Blender 5.1 processes.
  A real Spider-Man `l1a1_g` export also passed 12/12: all 264 meshes were marked, 244 ordinary meshes
  retained one UV set, and the 20 wibble meshes retained four. Focused carrier/viewer tests, the full
  net10 suite, and the Windows build are green. The upstream Blender defect still exists, but current
  exports no longer enter it; direct `.blend` export was never affected.

---

## Remaining — needs work

### 🔶 THPS3/THPS4 PS1 visual reports — one screenshot-specific report remains

Three of the four reports from the newly-enabled late PS1 ports (`62b3113`/`56686aa`) are now
adjudicated and regression-pinned:

- ✅ **`lasek2` stretched chin is authored corruption, not a format rule.** THPS2 and THPS4 carry
  the same 64-vertex/87-face head and exact ordinary vertex 51 `(-2,89,-224,type0)`. THPS3 alone
  replaces it with `(0,39,0,type2)`, whose normal global stitch interpretation targets stomach
  mesh 10, displaces the point by more than 23 units, and stretches five faces. Its other four head
  references correctly target parent chest mesh 11, and the THPS3/4 executables use the same stitch
  transform as THPS2, ruling out an alternate index base. The missing coordinates cannot be recovered
  from THPS3 alone; a visual repair would require an external donor/asset override, not a parser
  heuristic. `PsxStitchCorpusRegressionTests` pins the three-game comparison.
- ✅ **THPS3 two-player bank over-placement was real and is fixed.** Six `AUTOEXEC2` scripts select
  reduced `aa<stem>2o` banks that were still hash-named `.dat`, so resolution fell back to `_o`.
  `HedDictionaryPart5` now names them and extracted-tree lookup accepts their exact CRC aliases.
  Five variants materially shrink; Airport's two banks are byte-identical. Canada (no AUTOEXEC2)
  and Rio (explicit full-bank selection) correctly retain full banks, while both New York variants
  already selected `SkNY_O2`. `LatePs1VariantBankTests` pins all ten late-port variants.
- ✅ **THPS4 College's sky gradient is authored and exported in the correct direction.** The raw
  `a1col_o` sky (mesh 5, 112 triangles) is dark blue at native top Y and more than 0.3 luminance
  brighter at the horizon; every coloured THPS4 main sky follows the same trend. Export performs
  the required Y-axis basis inversion while keeping each packet colour attached to its vertex and
  carries the TRG sky colour `#AEC0DD`. College also enables world fog, so omitting optional fog can
  make its pale horizon more conspicuous, but backgrounds are intentionally fog-free in-engine.
  `ThpsLatePs1PortTests` pins raw-to-emitted direction and colour.

Still open:

- **skny: wrong texture wins on coplanar storefront layers** (screenshot: Pinky's Loans pawnbroker
  sign truncated) — a stable paint-order report, not z-fighting. The same-file overlay candidate path
  now covers adjacent plane buckets, quad secondary triangles, and writer-expanded sprites; re-render
  the reported storefront and identify the exact source faces before changing ranking. Do not infer
  success from the broader `skny` census alone.

---

## Done (for reference) ✅

- ✅ **Spider-Man PSX runtime appendage discovery generalized** — completed 2026-08-09. `PsxSplineClawLocator` enumerates sibling `.psx` assets without a literal filename or mesh checksum, ranks a unique self-contained kit before hidden-STP-template and conservative compact legacy bank candidates, caches the result per filesystem/archive directory, and preserves the discovered object/mesh indices instead of repackaging to zero. The legacy 2/18 bank tip remains supported; ambiguous candidates fall back to tubes. One-chain activation likewise has no Scorpion identifier: the complete embedded bank must prove a unique drawable object whose translations exactly mirror controller seven while only the tip carries rotation. This accepts PSX object 16 and PC object 0 and rejects the prototype Lizard rig. The generated tube tessellation remains a deliberate approximation until runtime evidence exists.
- ✅ **`.blend` skinned-character pose basis generalized** — completed 2026-08-09. Edit bones carry rigid bind translation + rotation, and absolute IR translation/rotation channels are solved into Blender `matrix_basis` without a source-kind gate. `BlendPoseBasisRegressionTests` pins a rotated/translated non-PSX hierarchy with animated root/child scale and mixed weights, then compares every pose bone of the real THPS4 `Ped_F_Walk` rig against GLB at an authored key. `PsxBlendExportRegressionTests` remains green. Non-rigid bind matrices fail explicitly rather than being silently approximated.
- ✅ **Spider-Man `.blend`/GLB frame-zero residual retracted as oracle pollution** — the direct `.blend` and Blender-imported GLB contain matching `anim_20`/`anim_30` FCurve sets, key times, and values (maximum observed translation rounding about `1.9e-6`), and their bone poses agree. The historical extent probe counted Blender's `Icosphere` armature custom-shape helper as render geometry; at frame zero that helper supplied the artificial maximum, while later character poses extended beyond it and hid the error. Filtering to mesh objects with an Armature modifier yields exact normalized extents (`anim_20` 0.469179; `anim_30` 0.468643) in both exports. Future cross-export oracles must exclude helper/custom-shape meshes.
- ✅ **PS1 authored-colour fallback restored; engine-light bake is explicit** — the 2026-07-27 rule that neutralized every lit-flagged PS1 face was reverted by `b18990f`. It discarded real serialized colours and flattened `control.psx` from 11 packet colours to one neutral value. Current `ComputePsxFaceColors` neutralizes only the PC/DC v6 whole-mesh dynamic-lighting path; PS1 returns authored colour unless the caller explicitly selects a proven `PsxEngineLight` preset, in which case `BakeEngineLight` computes the runtime light from normals. The default engine-light bake was subsequently turned off (`c01fff3`) because the file does not identify the game-code-selected rig.
- ✅ **PC/Xbox THAW multipass + ADD/SUBTRACT export** — `XbxPassCompositor` composites eligible pass-k overlays over pass 0, assigns synthetic texture identities, and bakes portable pass-0 ADD/SUB approximations. `XbxGeometryWriterBlendTests` covers overlay composition, framebuffer recipes, fixed-alpha scaling, and synthetic checksum separation (`25d3283`).
- ✅ **PS2 worldzone multipass draw ordering** — `Ps2WorldzoneGeometryWriter` preserves authored positions and publishes `DrawIndex`, `PassIndex`, `OverlapGroup`, plus the Blender separation vector. The in-app viewer applies GS submission order via `renderOrder` + LEQUAL. `Ps2WorldzoneDrawOrderTests` pins the metadata and ordering (`3b66842`).
- ✅ **THAW PS2 zone-TEX swizzle/offset lead retracted** — content-based GS-oracle attribution reduced the original worklist to two rows; both proved to be attribution artifacts (one streamed-content TEX0 collision, one cross-game THPG mapping). `ThawZoneTexOracleTests` records zero proven decode divergences across 17 committed captures and fails on any new unallowlisted divergence (`0a20830`).
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
  - **control.psx stick colours** — the attempted all-neutral PS1 fallback was later proven wrong and
    reverted by `b18990f`; preserving its 11 authored packet colours is the correct default. A runtime
    light bake remains available only when the caller explicitly chooses a proven game-code rig.
  - **"Missing" leaves/antennas/rail posts — RETRACTED as a converter bug**: a byte-level scan found
    the identical unconditional bit7 loader XOR + 0xC0 renderer skip in 10/10 PS1 binaries; TRG
    cross-reference proves the dropped whole meshes are
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
- ✅ **THAW compact object-MDL chains and QB-driven instancing** — bounded chain decoding recovers all 145 unique triangles for the five `z_bh` props, exact against PC/GameCube, plus the proven trailing chains for metal barrel (18 triangles), barricade (28), and little table (20). Across all 128 worldzones, all 60/60 object entries are readable PAK MDLs; the seven expanded-layout candidates are seven exact structural matches comprising three distinct payloads and four duplicates, recover 62 aggregate resource triangles / 244 placed triangles, and leave zero parse regressions. A generic conservative resolver accepts only exact named-MQB → CRC-zero MDL → CRC-zero MCOL triads, resolves inherited or direct-inline `gameobject` NodeArray structs, and applies direct XYZ plus `Rx*Ry*Rz` authored transforms. `z_bh` now emits 17/2/4/8/5 = **36 instances and 956 triangles**, with no origin singleton. The real `z_sr` guard pins three exact MDL offsets whose nine references lack `CreatedAtStart`: their non-empty resources resolve intentionally empty and emit no legacy origin geometry. The placement census pins 24 triads/resources, 161 eligible instances, and **3,318 GameCube-reference = 3,318 emitted triangles** (579.5 ms resolver, 29.9 ms compact writer, 1.02 s wall in the recorded run). Synthetic tests cover ownership near-misses, template inheritance/override/conflict/cycle/depth, duplicate/invalid targets, resolved-empty/network gates, direct-inline nodes, noncommuting `Rx*Ry*Rz`, and ThreadStatic lighting cleanup.
- ✅ All-format mesh-QA regression harness — `tools/validation/mesh/mesh_qa.py` provides isolated manifest-driven conversion, required-output and nonzero/finite GLB structural gates, Khronos JSON error validation with explicit degraded mode, built-in five-view review renders, triangle-recall oracles, stable JSON/HTML, and strict schema-v1 baseline coverage/drift/update rules with exit codes 0/1/2. The committed THUG2 Windows/PS2 pigeon Rosetta pair pins 1 GLB, 46 vertices, and 45 triangles on each path, 1 PS2 skin, exact recall 1.0, zero Khronos issues, and 5 review PNGs per case; the stdlib fake-tool self-test passes 53/53.
- ✅ THAW worldzone discovery inside DATAP.WAD — `memory/thaw_worldzone_archive_discovery.md` (v1.2.1).
- ✅ THAW worldzone billboards as camera-facing quads in `.blend` — `memory/thaw_worldzone_billboards.md` (the `.blend` path; glTF stays axis-aligned).

## By design / won't-fix ⚪

- ⚪ Worldzone triangle-efficiency cap (ADC degenerate suppression) — we render exactly what the engine rasterizes.
- ⚪ Camera-facing billboards in **glTF** — no static-glTF way to do view-facing quads; the `.blend` export handles it via Track-To constraints.
