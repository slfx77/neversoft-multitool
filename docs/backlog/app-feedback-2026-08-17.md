# App feedback — 2026-08-17

User feedback batch, categorized and triaged 2026-08-17. Linked from `BACKLOG_SUMMARY.md`'s stream table.

The batch splits cleanly into three categories: **A. viewer/GUI features and polish**, **B. THAW PS2 worldzone rendering fidelity** (all reported with P-key viewer poses that replay headlessly via `glb-render`), and **C. THAW PS2 model/cutscene fidelity**. Every pose below is verbatim from the report; the `#` comment names the source archive entry, so the repro is: convert that entry to GLB, then `glb-render <file.glb> <pose args> [--probe]`.

Each item records whether it was **confirmed against code** during triage or is **reported-only**. Reported-only items are real observations from the app; their stated cause (where one is suggested) is a hypothesis to test first, not a conclusion to implement against.

Status legend matches `BACKLOG_SUMMARY.md` (🔴 open · 🔶 partial · ✅ done · ⚪ blocked/by design).

---

## A. Viewer / GUI features and polish

### A1. 🔴 PS2-era day/night cycle + level lighting — reverse engineer and add a time-of-day slider

Feature request. **Not THAW-exclusive**: THUG2 and THAW have day/night cycles, and the user's "possibly
THUG too" is **confirmed against the THUG source** — the engine ships a real time-of-day system, so
the RE should target the shared THUG-family mechanism rather than a THAW-specific one. THAW (open
world, streamed zones) is still the richest case, and per-level lighting rides along. None of it is
currently reverse engineered or represented in the viewer. The ask is two-part:

1. **RE the PS2 lighting/time-of-day model across the THUG-family entries.** Triage anchors from
   `Sample/thug/Code/` (all confirmed present):
   - **Time of day is QB-script-driven**: the park editor stores a `tod_script` checksum and exposes
     `ScriptSetParkEditorTimeOfDay` / `ScriptGetParkEditorTimeOfDayScript`
     (`Sk/ParkEditor2/ParkEd.cpp:3164-3180`, `SetTimeOfDayScript` at :1389) — so the authored cycle
     states live in the QB corpus, which the tool **already decompiles**; a survey of `tod`-named
     scripts/structs across THUG/THUG2/THAW QBs is the cheap first step.
   - **Engine side applies a scene-level colour**: `Gfx/NGPS/p_nxmiscfx.cpp:1054-1055` reads a
     "Time of Day color" via `Nx::CEngine::sGetMainScene()->GetMajorityColor()` and pushes it onto
     geometry — i.e. at least part of the cycle is a scene colour modulation, a shape a viewer
     slider can reproduce directly.
   - Ancillary swaps exist too: `ScriptReplaceCarTextures` (`Sk/Scripting/skfuncs.cpp:956-959`) is
     explicitly "for time-of-day stuff" (car headlight textures), so a full cycle also involves
     texture swaps, not just lighting.
2. **Viewer: a time-of-day slider**, which can be borrowed from the Bethesda tool at
   `C:\Users\mmc99\source\repos\Xbox360MemoryCarver` (same author-pattern as the Settings drawer,
   which was already borrowed from Bethesda Multitool per `CLAUDE.md`).

This is the largest item in the batch — an RE investigation plus a viewer feature, not a bug fix. Note the
house rule from the PSX engine-light work applies here too: export/viewer lighting must come from **proven**
engine state, not an inferred rig.

### A2. 🔴 Mesh list: filename filter — confirmed against code

The Meshes & Characters file table has no way to filter by filename (the only filter in
`App/Tabs/MeshConverterTab.xaml` is the animations dropdown, `AnimFilterCombo` at :469, which filters
animation slots, not files). Add a filter button / text field over the mesh list. Game trees run to
thousands of entries, so this is a cheap, high-leverage usability win.

### A3. 🔴 Mesh list: group archive-contained meshes under expandable container rows — reported-only

THAW meshes live inside containers (`DATAP.WAD::worlds/worldzones/z_bh/z_bh.pak.ps2::…`), but the mesh
list presents a flat table. The ask: list each container as a row and make it expandable to its member
meshes. The Audio tab already has exactly this parent/child expandable-row pattern (bank → samples,
XA → channels — see `app-review-2026-08-12.md` C1-C3 for its control conventions), so the UI precedent
and its solved gotchas exist in-repo. Composes with A2: the filter should match both container and
member names.

### A4. 🔴 Display settings: wireframe toggle — confirmed against code

No wireframe toggle exists anywhere in the app (`grep -i wireframe` over `src/` hits only the vendored
three.js bundle and an unrelated QbKey name). three.js materials support `wireframe: true` natively, so
the viewer side is small; the toggle belongs in the Display Settings pane beside the existing
PS1-fidelity/lighting switches.

### A5. 🔴 Surface-animation toggles appear for games that have no surface animations — confirmed against code

The **Colour pulses** / **Texture wibbles** toggles (`MeshConverterTab.xaml:344-380`) are static content
in the Animations pane with no per-model gating — they show for THAW PS2 and every other format, even
though pulses/wibbles are a PSX-family feature (plus the GC VC/UV wibble headers, which the viewer does
not animate). The viewer already knows whether the loaded model has pulse/wibble bindings (the shared
60 Hz clock only advances when either exists — see the 2026-08-10 pulse-only clock correction), so the
gating signal exists; the toggles should hide (or disable with a tooltip, matching the Blender-missing
pattern) when the loaded model carries neither.

### A6. 🔴 "Colour" → "Color": UI labels should use American English — confirmed against code

The app's UI uses American English; the toggle label and its automation name say "Colour pulses"
(`MeshConverterTab.xaml:358` and `:367`). Fix the **user-facing strings only**. Do NOT rename code/data
identifiers: the GLB extras (`neversoftColourPulseChannels`, `neversoftColourPulse`), class names
(`PsxColourPulseChannels`, `PsxColourPulseEvaluator`, …), and manifest keys are a shipped compatibility
surface pinned by tests and consumed by the Blender importer — renaming them is out of scope for a
label fix.

---

## B. THAW PS2 worldzone rendering fidelity

All reported-only, all with replayable poses. Context that likely bears on several items:

- **PS2 worldzone multipass draw ordering shipped** (`Ps2WorldzoneGeometryWriter`, `3b66842`): authored
  vertices unchanged, GS submission order carried as `DrawIndex`/`PassIndex`/`OverlapGroup` metadata,
  applied in the viewer via `renderOrder` + LEQUAL. The shadow/window z-fighting and layering items
  below are candidate residuals of that path — first question for each is whether the metadata is
  wrong, missing for the mesh in question, or not honored by the headless renderer vs the app.
- **The headless `glb-render` alpha-tests instead of blending** — a pose that shows a wrong *blend*
  needs the in-app viewer for appearance judgments; the poses settle geometry/ordering questions
  (`--probe` lists every surface along the centre ray with gaps — a `+0` gap is a z-fight diagnosis).
- **Blend-mode export rules** for PS2 materials are documented in `CLAUDE.md` (GS ALPHA register
  classes; additive = luminance-to-alpha, subtractive = black + luminance-alpha; the THAW-PS2
  AREF ≤ 1 regression history). Several items below smell like a blend class not being honored
  (opaque shadows, opaque fake-light meshes, missing mud overlay) rather than geometry defects.

### B1. 🔴 z_bh: completely opaque shadow

Shadows render fully opaque (they should be a dark translucent blend — on PS2 typically a
subtractive/source-alpha blend over the ground).

```
# DATAP.WAD::worlds/worldzones/z_bh/z_bh.pak.ps2
--camera-eye=-13499.33,-95.7167,2535.359 --camera-yaw=-95.81 --camera-pitch=-25.02 --camera-fov=45 --camera-size=1237x1105

# DATAP.WAD::worlds/worldzones/z_bh/z_bh.pak.ps2
--camera-eye=-14848.24,-51.625,9086.718 --camera-yaw=-135.86 --camera-pitch=-26.73 --camera-fov=45 --camera-size=1237x1105
```

### B2. 🔴 z_bh: z-fighting shadow

Distinct from B1 — here the shadow fights the surface under it.

```
# DATAP.WAD::worlds/worldzones/z_bh/z_bh.pak.ps2
--camera-eye=-14979.76,-78.1875,3203.322 --camera-yaw=157.45 --camera-pitch=11.6 --camera-fov=45 --camera-size=1237x1105
```

### B3. 🔴 z_bh: z-fighting windows

```
# DATAP.WAD::worlds/worldzones/z_bh/z_bh.pak.ps2
--camera-eye=-14502.16,-88.9497,7109.001 --camera-yaw=147.82 --camera-pitch=30.68 --camera-fov=45 --camera-size=1237x1105
```

Note for B2/B3: the PSX side solved its shadow/window z-fight class this month (the lift clearance
rule, `b1ab5ab`) — the *diagnostic method* (probe for `+0` gaps, classify which mechanism owns each
face) transfers, but the PSX fix itself does not; THAW worldzones resolve draw order by metadata, not
vertex lifts.

### B4. 🔴 z_bhsm: file not fully rendering

`z_bhsm.pak.ps2` renders incompletely. User identifies it as **Zone Beverly Hills → Santa Monica**, a
streaming *transition* zone (THAW's open world streams between zones through these). No pose given.
Triage should start with whether the transition-zone paks have a structural difference (partial level
MDL, references into the two neighbour zones' resources) that the phase-420 level-MDL path or the
QB instancing resolver declines. Related to B10 (another zone class that under-renders).

### B5. 🔴 z_dn: road renders over glass

Draw-order/transparency inversion — an opaque road surface paints over a transparent pane in front
of it.

```
# DATAP.WAD::worlds/worldzones/z_dn/z_dn.pak.ps2
--camera-eye=5247.889,1492.144,-899.9779 --camera-yaw=13.17 --camera-pitch=-15.22 --camera-fov=45 --camera-size=1237x1105
```

### B6. 🔴 z_dn: misaligned windows (missing blending?)

User's own hypothesis: the misalignment may actually be a missing blend (a window layer that should
composite over another reading as offset geometry when drawn opaque).

```
# DATAP.WAD::worlds/worldzones/z_dn/z_dn.pak.ps2
--camera-eye=1798.427,1759.998,-3394.864 --camera-yaw=89.59 --camera-pitch=-6.45 --camera-fov=45 --camera-size=1237x1105
```

### B7. 🔴 z_ho: chairs sunken into ground

Placed props sit below the floor. Candidate owner: the QB-driven prop instancing path (exact
named-MQB → MDL → MCOL triads with direct XYZ + `Rx*Ry*Rz` authored transforms — see
`mesh-fidelity.md`'s compact-prop entry) — a wrong pivot/basis there would sink every instance of one
resource. **Measure before assuming a defect**: the PSX l1a3 chair taught that some furniture is
*authored* embedded into the floor and reads correctly in-game; the diagnostic (decoded base-Y vs a
downward floor query over the render mesh) is documented in `CLAUDE.md` and transfers directly.

```
# DATAP.WAD::worlds/worldzones/z_ho/z_ho.pak.ps2
--camera-eye=1034.087,66.023,1683.757 --camera-yaw=132.87 --camera-pitch=-19.69 --camera-fov=45 --camera-size=1237x1105
```

### B8. 🔴 z_ho: chainlink fence missing alpha

Chainlink renders without its cutout. The THAW-PS2 alpha ladder has a long regression history
(AREF ≤ 1 is the always-pass default, not a mask — `884d018` saga), so this wants the specific
material's blend/ATE state read out rather than a rule tweak. Compare the DC chainlink precedent
(2026-07-28: authored-alpha cutouts vs the ABR-0 wash) for how the fix was scoped there.

```
# DATAP.WAD::worlds/worldzones/z_ho/z_ho.pak.ps2
--camera-eye=-2384.156,-36.8161,1116.824 --camera-yaw=130.81 --camera-pitch=-8.17 --camera-fov=45 --camera-size=1237x1105
```

### B9. 🔴 z_sr: should have MANY visibility groups but has none; many texturing issues

Two distinct reports on one zone:

1. **Visibility groups.** z_sr (Skate Ranch) is the game's evolving hub — the engine shows/hides large
   object sets as the story progresses, so the user expects many toggleable groups. The converter
   currently emits none. Directly relevant shipped evidence: the QB placement census pins z_sr's
   **nine script-created references that lack `CreatedAtStart` resolving intentionally empty**
   (`mesh-fidelity.md`, compact-prop entry) — i.e. we already *find* script-created content and
   deliberately drop it. The PSX side's answer to the same situation was default-off visibility
   groups (`__ghost`, "What If?" content, scripted-traffic snapshots); the ask is the THAW
   equivalent: emit script-created/story-gated object sets behind default-off groups instead of
   omitting them.
2. **Texturing issues**, most visible at:

```
# DATAP.WAD::worlds/worldzones/z_sr/z_sr.pak.ps2
--camera-eye=-28431.75,1358.962,10570.58 --camera-yaw=48.23 --camera-pitch=-54.66 --camera-fov=45 --camera-size=1237x1105

# DATAP.WAD::worlds/worldzones/z_sr/z_sr.pak.ps2
--camera-eye=-24596.87,2169.769,9789.653 --camera-yaw=2.37 --camera-pitch=-74.6 --camera-fov=45 --camera-size=1237x1105
```

Note: the zone-TEX oracle (`ThawZoneTexOracleTests`) records zero proven *decode* divergences, so
suspect texture **assignment** (TEX0/VRAM mapping, streamed-content collisions) before re-opening the
decoder. See also B12/C2, which may share a cause.

### B10. 🔴 z_testlevel: only renders objects (level geometry missing)

Some worldzones don't render fully; `z_testlevel.pak.ps2` renders only its placed objects, no level
mesh. First question: does its level MDL exist and get declined by the phase-420 leaf path, or does
the pak genuinely ship no level geometry (a test shell)? Related to B4.

### B11. 🔴 z_ms: fake light meshes render opaque

Light-shaft/glow card meshes (authored additive geometry) render as solid. Almost certainly a blend
class not honored — the N64 corpus hit the identical symptom ("warehouse shafts exported solid white
and occluded the level", fixed by honoring vertex alpha / additive classes). Check the material's GS
ALPHA register class against the documented additive rules (0x48/0x68 → luminance-to-alpha).

```
# DATAP.WAD::worlds/worldzones/z_ms/z_ms.pak.ps2
--camera-eye=-57.504,-2289.129,2035.655 --camera-yaw=-177.97 --camera-pitch=9.8 --camera-fov=45 --camera-size=1237x1105
```

### B12. 🔴 z_lv: many texturing errors in one shot

Vegas zone, dense repro pose for the texturing class (see B9 note on assignment-vs-decode):

```
# DATAP.WAD::worlds/worldzones/z_lv/z_lv.pak.ps2
--camera-eye=17609.02,1666.168,4852.116 --camera-yaw=5.47 --camera-pitch=-44 --camera-fov=45 --camera-size=1237x1105
```

### B13. 🔴 z_sm_net: improperly blended geometry (four instances)

One zone, four blend-class failures — a good single-file testbed for the whole B-series blend work:

Shadow (cf. B1):

```
# DATAP.WAD::worlds/worldzones/z_sm/z_sm_net.pak.ps2
--camera-eye=-10121.3,-164.574,16950.64 --camera-yaw=17.64 --camera-pitch=-27.5 --camera-fov=45 --camera-size=1237x1105
```

Grass — should have a mud overlay (multipass overlay missing or drawn wrong; cf. the shipped
`XbxPassCompositor` overlay work on PC/Xbox — the PS2 worldzone path has no equivalent bake):

```
# DATAP.WAD::worlds/worldzones/z_sm/z_sm_net.pak.ps2
--camera-eye=-12373.79,417.5575,15750.3 --camera-yaw=-4.19 --camera-pitch=-89.9 --camera-fov=45 --camera-size=1237x1105
```

Window:

```
# DATAP.WAD::worlds/worldzones/z_sm/z_sm_net.pak.ps2
--camera-eye=-12506.92,588.5135,15521.07 --camera-yaw=-4.19 --camera-pitch=0.86 --camera-fov=45 --camera-size=1237x1105
```

Asphalt — should be dark gray / black (reads as a missing darkening pass — likely the same
multipass/blend family, e.g. a subtractive or modulate layer skipped):

```
# DATAP.WAD::worlds/worldzones/z_sm/z_sm_net.pak.ps2
--camera-eye=-12861.95,-122.9364,19591.78 --camera-yaw=-178.9 --camera-pitch=-69.1 --camera-fov=45 --camera-size=1237x1105
```

---

## C. THAW PS2 model / cutscene fidelity

### C1. 🔴 sec_jimbo_xen: cel-shading outline meshes drawn on top of the character

The mesh uses an inverted-hull-style outline technique; the outline meshes should render *behind* the
character (they only show at silhouette edges when backfaces are culled / depth-ordered correctly),
but almost all draw on top. Hypotheses to test: outline shells exported double-sided or with wrong
winding (so backface culling never hides them), or their pass/draw order lost. Reported-only.

```
# DATAP.WAD::models/skater_secret/sec_jimbo_xen.skin.ps2
--camera-eye=-0.6902,53.3501,138.8087 --camera-yaw=-2.93 --camera-pitch=-3.26 --camera-fov=45 --camera-size=1237x1105
```

### C2. 🔴 Completely corrupted textures on two standalone MDLs

Both are offset-named (keyless) MDL entries converted directly from inside their paks — not
worldzone level geometry — so the texture resolution path in play is the standalone MDL one
(TEX0/VRAM simulation and companion-TEX joins), not the zone-TEX path:

```
# DATAP.WAD::worlds/createapark/cap_shell3/cap_shell3.pak.ps2::0003C8C0.mdl
--camera-eye=-2719.192,447.7169,-3587.685 --camera-yaw=88.4 --camera-pitch=-15.15 --camera-fov=45 --camera-size=1237x1105

# DATAP.WAD::worlds/worldzones/z_mainmenu/z_mainmenu.pak.ps2::001A4670.mdl
--camera-eye=111.5497,693.3531,4014.061 --camera-yaw=-0.65 --camera-pitch=-0.65 --camera-fov=45 --camera-size=1237x1105
```

### C3. 🔴 Cutscene MDL renders incomplete and "front down"

A cutscene-pak MDL renders with missing geometry and pitched face-down — the orientation suggests a
basis/root-transform difference on the cutscene path (cutscene assets bind to animated rigs whose
rest transform the standalone converter may not apply), and the incompleteness a partially-declined
chain. Reported-only.

```
# DATAP.WAD::cutscenes/sm_levelevent/ps2/sm_levelevent_main/sm_levelevent_main.pak.ps2::00034A60.mdl
--camera-eye=3205.802,-106.8174,3247.081 --camera-yaw=44.63 --camera-pitch=1.38 --camera-fov=45 --camera-size=1237x1105
```

---

## Triage 2026-08-17 — every B/C item classified

Executed the same day the batch landed. Evidence lives under `TestOutput\triage\` (GLBs for every
zone in Day AND All time-of-day variants, `--probe` transcripts for all 23 poses in `probes\`,
renders in `renders\`) and `TestOutput\triage\debug\` (the new `--worldzone-debug-dir` output:
`<zone>.rejections.csv` + `<zone>.materials.csv` per zone). The A6/A5/A4/A2 GUI items shipped the
same day (`d74e8ff`, `93aad94`, `ec2606d`, `5676351`) along with the triage diagnostics
(`66bd0b6`: rejection logging + per-leaf GS-state CSV, `mesh --worldzone-debug-dir`).

**Cross-cutting finding #1 — day/night duplicate stacks are the dominant defect family.** THAW
authors many surfaces TWICE (day + night variant, coincident). Only ADDITIVE night overlays are
classified NightOverlay and dropped from Day exports (z_bh drops 444 leaves, z_lv 435, z_dn 178,
z_sm_net 150, z_sr 107, z_ms 35, z_ho 30 — the Day/All triangle deltas match exactly); NON-additive
night variants (window panes, lit-sign faces) ship into Day exports coincident with their day twins.
B2/B3/B5/B9.2/B12/B13a/B13c are all this class: probes show exact `+0` two-to-four-layer
MASK/OPAQUE/BLEND stacks (e.g. B5's pane pairs leaf_01491/01492 — consecutive leaves, both
standard-blend, same overlap group, pass 0/1, DIFFERENT textures = the day/night pair).

**Cross-cutting finding #2 — the draw-order metadata is CORRECT for every probed pair.** B2's
shadow (leaf 3521) draws after its ground (leaf 3090), same overlap group, pass 1; B13b's mud layer
(dest-alpha-synthesized OPAQUE, leaf 1683) draws after its grass (leaf 2797). The headless renderer
ignores the metadata by design, so probe `+0` fights are expected THERE; the app viewer resolves
these via `renderOrder` since `3b66842`. **The user's reported build may predate that work** — the
z-fight items need one in-app check against the current build before any further fix (Phase 4,
still pending).

Per-item verdicts:

- **B1 (opaque shadow)** 🔶 — at the pose: subtractive-FIX shadow leaf 3556 (A2 B0 C2 D1, FIX=44 →
  baked at ~0.10 alpha) + standard-blend layers over OPAQUE ground at +0.06. State is coherent;
  whether the current build renders it acceptably is a Phase-4 in-app question.
- **B2 (z-fight shadow)** 🔶 — textbook coplanar pair at exact +0, metadata CORRECT (see above).
  In-app check against current build decides: fixed-by-3b66842 vs live viewer defect.
- **B3 (z-fight windows)** 🔶 — centre ray clean; same day/night-stack class as B5.
- **B4 (z_bhsm not fully rendering)** 🔶 **cause confirmed** — MDL 0001C7D0 emits 17 leaves while
  **18 are eaten by the geometric quarantine** (`ShouldSkipWorldzoneLeaf`): the transition
  corridor's road/tunnel segments are large, origin-centred, normal-less strips — exactly the
  junk-heuristic's shape. Follow-up: refine the quarantine without re-admitting the junk it was
  built for. (258 triangles total exported; rejections.csv rows carry every quarantined bbox.)
- **B5 (road over glass)** 🔶 **cause confirmed** — three +0 day/night pane pairs (family above).
  Follow-up: TOD variant-pair selection beyond the additive-only NightOverlay rule.
- **B6 (misaligned windows)** 🔶 — window MASK over wall OPAQUE at +0.359 (not coplanar);
  blend-appearance question for Phase 4.
- **B7 (sunken chairs)** 🔴 **cause confirmed 2026-08-17 (second pass)** — measured every QB
  placement in z_ho against a floor query over the exported level mesh
  (`ThawTriageProbeTests.B7_ZHo_QbPlacedProps_EmbedDepthAgainstTheLevelFloor`, report
  `b7_z_ho_embed.csv`): all 39 chairs embed 19.0–24.5 units, all 13 tables 13.0–18.4, all 8
  barricades 19.1–37.9 — while two plants seat within 0.1 of the floor. All four prop models are
  **vertically CENTER-pivoted** (chair ±23.06, table ±17.19, barricade ±28.8, plant ±56.7) and the
  conversion places each model origin exactly at the authored node Y (verified faithful to the QB
  data). The authored node heights are INCONSISTENT relative to the floor — plants sit a
  half-height above it (and seat correctly), chairs/tables/barricades sit AT floor level (and sink
  by ~half their height). So the engine evidently normalizes seating at spawn (the PSX
  `CPowerUp`/ground-query lesson again); the exporter needs the equivalent bottom-anchor/floor-seat
  for QB props. The **Y↔Z basis hypothesis is REFUTED** — XZ placement is correct (the probe hits
  chairs at the reported pose). Decisive oracle for the fix: the GameCube z_ho scene's baked world
  placements, or the THAW gameobject spawn path (the THUG source has no gameobject ground-snap —
  only skater physics — so this is THAW-side).
- **B8 (chainlink missing alpha)** 🔴 **cause confirmed, fully evidenced** — fence leaf 3538
  (z_ho): engine state is standard source-alpha BLEND; texture 711D88D6's cutout is INTACT (84.6%
  of texels below half-alpha, holes at α≈2 — verified from the debug texture dump); the bimodal
  de-escalation (`Ps2MaterialWriter.ClassifyPs2GeomEffectiveAlphaMode` :204-210) converts BLEND→MASK,
  and the cutoff guard (:163-169) only exempts AREF=0 — this leaf's engine-default AREF=1 slips
  through to `ComputeAlphaMaskCutoff` = 1/128 ≈ 0.0078, which the α≈2 hole texels PASS → solid
  fence. Fix shape: bimodal-branch MASK should use the 0.5 cutoff (as the guard already does for
  texture-classified MASK), or the AREF exemption should cover ≤1 — either needs the 884d018-style
  corpus gate before shipping.
- **B9.1 (z_sr visibility groups)** 🔴 — feature follow-up as scoped; mechanism is format-agnostic,
  drop point known, nine non-CreatedAtStart refs corpus-pinned.
- **B9.2/B12 (texturing errors)** 🔶 **reframed** — worldzone texture-resolution tags are HEALTHY
  (19,213 rows: 18,987 `entry_material_group_exact`, 221 `entry_exact`, **5 unresolved**), so these
  are NOT assignment failures: the probes show 3-4-layer coincident stacks at both z_sr poses and
  the z_lv pose — the day/night duplicate family again.
- **B10 (z_testlevel only objects)** 🔶 **cause confirmed** — level MDL 00050D70 parses but **21 of
  its 23 leaves decode to zero vertices** (`parse/empty_batch`); only 2 emit while the two object
  MDLs emit 12 each. Follow-up: decode why this zone's level-MDL batches come back empty (likely a
  layout variant the phase-420 slicer declines).
- **B11 (fake lights opaque)** 🔴 **cause confirmed, two mechanisms** — (1) the glow sheets are
  billboard∧additive leaves forced to MASK by the billboard early-return
  (`Ps2GeomRenderSemantics.ClassifyWorldzoneAlphaMode` :42-43) — corpus-wide: z_sm_net 149, z_lv
  139, z_bh 130, z_sr 107, z_dn 13 such leaves — AND dropped from Day exports where non-billboard
  (the z_ms pose gains two MASK glow overlays only in the All GLB); (2) the cones themselves
  (leaves 881/887) are destination-alpha blends (A0 B1 **C1** D1) that fall to the OPAQUE branch
  when dest-alpha synthesis finds no mask candidate. Follow-ups: register-read-before-billboard
  (the gated one-liner — census now exists) + a dest-alpha fallback better than opaque + the
  viewer's name-gated additive path (PS2 materials never match `__st[13]`).
- **B13 (z_sm_net blending, 4 poses)** 🔶 — shadow/window/asphalt are the day/night-stack family;
  **mud (B13b) cause confirmed**: the mud layer EXISTS in both TODs as a dest-alpha-synthesized
  OPAQUE leaf (1683, synthetic=1) correctly ordered after its grass (2797) — current-build in-app
  should show mud; the user's build likely predates the renderOrder work. Only 4
  `redundant_blend_layer` skips fired in the whole zone (the over-drop hypothesis did NOT hold for
  this pose).
- **C1 (cel-shade outlines)** 🔴 **cause confirmed** — probe at the chest: 4 hits all
  `group_7601435B`, all OPAQUE, **all 2-sided, first hit back-facing 0.255 units in FRONT of the
  front-facing body** — the inverted-hull shell is never culled because `RenderMaterial.DoubleSided`
  defaults true and nothing PS2-side clears it. Render = full black silhouette. Fix candidate
  (DoubleSided=false for PS2 skins) still gated on the THUG-source cull check + corpus render-diff;
  skinned meshes also carry no draw-order extras (second cause).
- **C2 (corrupted textures)** 🔴 **cause-class confirmed** — renders show per-material subsets
  garbage while most materials are correct (assignment, not decode — consistent with the oracle).
  **C2a** cap_shell3: exactly ONE `.tex` precedes the MDL, so companion selection is right and the
  corruption is WITHIN-source (standalone path has no TEXA-aware resolver and no per-MDL hint,
  unlike the worldzone path). **C2b** z_mainmenu: the pak carries MANY texture entries (2 .stex +
  a dozen .img) while the standalone path consults exactly ONE nearest-preceding entry — the
  multi-source loss is structural. Follow-up: TEX0/VRAM-aware standalone companion resolution
  pooling all pak texture entries (reuse ZoneTextureCatalog). Note: z_mainmenu.pak.ps2 itself does
  not route as a worldzone ("Not a recognized THAW PS2 worldzone PAK" — no placement entry).
- **C3 (front-down cutscene MDL)** 🔴 **both halves resolved 2026-08-17 (second pass)** —
  (1) "Incomplete": the pak holds **42 MDL entries** (+5 skins, 4 ske, 1 ska, 47 stex) — one
  cutscene scene split across entries; `00034A60.mdl` itself parses 13 of 14 leaves (single parse
  rejection; census in `ThawTriageProbeTests.C3_CutsceneMdl_DeclineCensusAndBasisFacts`), so the
  fragment-look is per-entry conversion of a multi-entry scene, not a decode failure. The real
  feature is whole-pak cutscene-scene assembly. (2) "Front down": the piece is the Santa Monica
  pool-dig deck plane (998 × 2997 × 912 — a flat deck standing on its end in the render) exported
  at identity — and it carries **no bone preamble** (bones=0, records=10), so there is no in-file
  transform to borrow: the placement lives in the cutscene's companion data (its ske/ska/QB), which
  is where the fix must read from. The earlier "just add the axis swap" note is superseded — a
  blanket swap has no per-file evidence to stand on for these.

**Still pending from the campaign plan**: Phase 4 in-app verification (decides the B2/B5/B13-family
"fixed by current build?" question) and the C2 GUI-parity harness. B7's embed measurement and C3's
decline census landed in the second pass (`ThawTriageProbeTests`, `[CorpusFact]`-gated,
reports under the test TestOutput's `triage-harness/`); the rest of the Phase-2 harness was
obviated by extracted-directory listings and the debug CSVs.

## Follow-ups spawned by triage

1. **TOD variant-pair selection** — Day exports must drop non-additive night duplicates (B5, B3,
   B9.2, B12, B13a/c). Largest-impact item.
2. **Billboard blend classification** — read registers before the billboard early-return; census
   exists in the materials CSVs (B11 glow sheets; gated one-liner candidate).
3. **Dest-alpha OPAQUE fallback** — cones/light shafts need something better than opaque when
   synthesis finds no mask (B11 cones, likely other glow geometry).
4. **Viewer additive gating by metadata** — PS2 worldzone materials never match the PSX `__st[13]`
   name convention, so even correct additive bakes composite as source-alpha in-app.
5. **Bimodal-MASK cutoff** — 0.5 for the bimodal de-escalation branch or AREF≤1 exemption, corpus-
   gated (B8).
6. **Geometric quarantine refinement** for transition zones (B4).
7. **z_testlevel level-MDL empty-batch decode** (B10).
8. **Standalone-MDL texture pooling** — TEX0/VRAM-aware multi-source resolution (C2a/C2b).
9. **Cutscene-scene assembly** (C3) — assemble a cutscene pak's full MDL set into one scene with
   placements read from its companion ske/ska/QB data (a blanket axis swap has no per-file
   evidence; the MDLs carry no bone preamble).
9b. **QB prop seating** (B7) — bottom-anchor/floor-seat center-pivoted QB props; oracle = GameCube
   scene baked placements or the THAW gameobject spawn path.
10. **PS2 skin backface culling** (C1) — gated on THUG-source cull check + corpus render-diff; plus
    skinned-mesh draw-order extras.
11. **THAW script-created-content visibility groups** (B9.1) — as originally scoped.
12. **Phase 4 in-app verification pass** — settles every "fixed by current build?" item above.

## Suggested working order

1. **A6 + A5** — label fix and toggle gating: small, self-contained, no format risk.
2. **A2 → A4 → A3** — mesh-list filter, wireframe toggle, then container grouping (largest of the three).
3. **B-series blend classes as one investigation** — B1/B11/B13 (opaque shadow, fake lights, mud/asphalt
   overlays) likely share a GS blend/multipass root; z_sm_net (B13) exercises four symptoms in one file.
   Then B5/B6 (ordering vs blending on transparencies), then B2/B3 with `--probe`.
4. **B9.1 visibility groups** — design piece (THAW analogue of the PSX default-off content groups),
   builds on the existing placement census.
5. **B4/B10** — under-rendering zone classes (structural triage first).
6. **B9.2/B12 + C2** — texturing: assignment-layer investigation across both worldzone and standalone
   MDL paths.
7. **C1, C3** — per-model draw-order/basis cases.
8. **A1** — day/night RE: schedule as its own investigation once the blend/lighting groundwork from
   the B-series is understood (they will share GS-state knowledge).
