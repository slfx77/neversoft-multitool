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
