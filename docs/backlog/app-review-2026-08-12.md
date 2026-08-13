# App review — 2026-08-12

Items from a hands-on review of the shipped GUI. Kept as its own stream file because the working tree held 554 uncommitted files from a parallel session when this was written — including `BACKLOG_SUMMARY.md` and three other backlog docs — so the items were recorded here rather than merged into files another session had open. Linked from `BACKLOG_SUMMARY.md`'s stream table.

⚠️ **Line references in section B may have moved.** `N64BundleNameResolver.cs`, `N64BundleNames.cs`, `N64LightRig.cs`, `N64ModelWriter.cs`, and `N64RenderBankFile.cs` all carried uncommitted modifications from that parallel session at the time of writing, so both the cited lines and the current behaviour may already differ — re-read before acting on B1-B4. The files behind section A (colour pulses) and section C (GUI) were clean, so those pointers are against committed code.

Status legend matches `BACKLOG_SUMMARY.md` (🔴 open · 🔶 partial · ✅ done · ⚪ blocked/by design).

Each item records whether it was **confirmed against code** during triage or is **reported-only**. Reported-only items are real observations from the app; they simply have not been reproduced in a debugger yet, so their stated cause is a hypothesis to test first, not a conclusion to implement against.

---

## A. PSX colour pulses (Spider-Man)

### A1. ✅ Pulses freeze after a few seconds of playback — FIXED 2026-08-12

Reported as "pulse animations eventually stop". **This was a real bug; cause identified and fixed.**

Both playhead walks advance by subtracting one key interval per loop iteration, under a fixed 256-iteration guard, while `time` grows with the absolute frame counter:

- `Core/Formats/Mesh/Psx/PsxColourPulseEvaluator.cs:49-56` (`WalkGuard = 256`)
- `Assets/mesh-viewer.html:1066-1071` (`for (let guard = 0; guard < 256; guard++)`) — this is the one the user sees

Once `initialAccumulator + frame` exceeds the sum of the next 256 intervals, the loop exits early with `time` still ≥ `interval`, so `amount = clamp(time / interval)` pins to `1.0` forever and the channel stops moving. With byte intervals averaging ~2-8 frames that is roughly **4-35 seconds** of viewing before every pulse freezes.

**Fix**: wrap by cycle length before walking — the C# evaluator *already does exactly this* for negative time at `PsxColourPulseEvaluator.cs:41-47` (`time = (time % cycle + cycle) % cycle` using `CycleFrames`). Apply it unconditionally, then the walk is bounded by the key count and the guard becomes a malformed-data backstop rather than a playback limit. Mirror the same change in the viewer's `psxColourPulseSample`.

**Do not "fix" `PsxColourPulseChannels.EvaluateFrameZero` (:110)** — it walks from `InitialAccumulator` only (a byte, ≤ 255), so it is correctly bounded, and it is the self-validation the export path depends on.

**Why the green suite missed it**: `PsxColourPulseExportTests.Export_Frame0MatchesTheBake_AcrossEveryPulsedPsxFile` validates **frame zero only**, across the whole corpus. Nothing exercised a late playhead.

**Fix as shipped**: both walks now fold the playhead into one cycle first (the walk is periodic in the cycle length, so this is equivalent wherever the old walk actually terminated, and frame-0 agreement with the bake is unchanged — the corpus sweep stays green). Regressions added: four late-playhead cases plus a short-interval sweep and an explicit "still animates late in playback" assertion in `PsxColourPulseEvaluatorTests`, and an ordering pin in `PsxColourPulseViewerContractTests` for the untestable viewer JS. Mutation-checked: four of the five new C# cases fail against the pre-fix evaluator.

### A2. 🔴 Pulses run at the wrong speed — REPORTED, needs one decomp check

Reported as "pulse animations play too quickly". The viewer advances the shared surface clock at 60 Hz (`mesh-viewer.html:1136`, `textureWibbleFrame + dt * 60`), which matches the documented contract in `PsxColourPulseEvaluator`'s summary ("the engine advances each pulse's playhead by the frame delta `XblanksNow - XblanksThen` … intervals are therefore in 60 Hz frames").

So either that contract is wrong, or the perceived speed comes from A1's phase corruption. **Check first**: confirm in the decomp whether `M3d_PreprocessPulsingColours` advances by the xblank *delta* (60 Hz wall clock, current assumption) or by a fixed +1 per rendered frame — the latter would run at the game's frame rate and make our playback exactly 2× fast on a 30 fps title. Re-evaluate this item *after* A1 lands, since a frozen/clamped playhead changes what "too quick" looks like.

### A3. 🔴 Pulses too bright on some pickups (web cartridges) — REPORTED

The channel builder already distinguishes the two colour domains per channel key — textured primitives modulate around 128, untextured ones use display RGB (`PsxColourPulseChannels.cs:160-166`, `key.UsesDisplayRgb`) — and channels are keyed by that usage, so a shared pulse index cannot mix domains. Frame 0 is also self-validated against the static bake, so the *endpoints* are right by construction.

That leaves the **interpolation domain** as the suspect: endpoints are transformed and then lerped, which the code itself flags as not frame-exact away from a key (`PsxColourPulseChannels.cs:140-145`, "portable interpolation is intentionally performed after endpoint transformation and can differ at a mid-interval playhead"). Overbright would then appear only *between* keys, which is consistent with "some pickups" rather than all.

**Repro to capture**: web cartridge pickup, note whether the overbright is continuous or only mid-interval; compare `_PSX_COLOR_0` (packet) against `COLOR_1` (portable) sampling for that primitive, and check the 255/128 textured-modulation scale on an additive/untextured pickup face.

### A4. 🔴 Surface animation needs its own toggle — FEATURE

Colour pulses and animated (UV-wibble) textures should be toggleable from the Animations panel, **separate from the skeletal animation list** — they are per-surface effects, not clips, and today they run unconditionally. Panel lives at `App/Tabs/MeshConverterTabAnimationPanel.cs`; the viewer already keeps the two collections independent (`colourPulseMeshes` / `textureWibbleMeshes`, `mesh-viewer.html:175-176`), and the shared clock at `:1130-1137` is already written to tolerate either being empty, so two booleans through the existing viewer message path should be enough.

---

## B. N64 rendering fidelity

### B1. 🔴 Transparency cutouts render as solid black on some meshes — REPORTED

`122_venom.psx.n64` renders Venom's teeth against solid black; **`123_venom.psx.n64` is correct**, which makes this a per-file classification difference rather than a broken path.

Likely mechanism: `ResolveBlendState` (`N64ModelWriter.cs:706-720`) returns `Mask` only when the bound texture actually carries one-bit alpha, else `Opaque`. A CI4 texture's transparent slots are the `A=0` palette entries whose **colour bytes are garbage by design** — so if a cutout texture is classified opaque, those texels paint as (typically black) garbage instead of being discarded. That is precisely "solid black background".

**Investigate**: dump the bound texture format + alpha histogram for the teeth material in 122 vs 123 (they are probably different formats, e.g. CI4 vs RGBA16); the classifier likely misses one of them.

### B2. 🔴 Texture seams — REPORTED

Two instances: a dark line down the middle of the spider emblem on `141.psx.n64` (Symbiote Spidey) and a thin black line along the tops of the skybox buildings in `211_LDA2_O.psx.n64`.

A seam down the *middle* of a symmetric emblem points at a mirrored-UV boundary, which suggests the tile's clamp/mirror/wrap state is not being honoured — the decode currently assumes `G_SETTILESIZE` covers the full texture with scale 1.0 / shift 0 and normalises `s/32/width`. **Check** whether the display list's tile clamp/mirror/wrap flags are parsed at all, and whether a half-texel inset is needed at the edge.

### B3. 🔴 Untextured geometry ignores vertex shading / renders too bright — REPORTED

Two instances that are probably one bug: `145.psx.n64` (Symbiote) has untextured parts far brighter than the rest of the mesh, and `247_torch.psx.n64` renders the fire **solid white where the PS1 version is yellow**.

Both look like authored vertex colour being discarded in favour of the ROM's light rig. The lighting bit is per group and **active low** — `kind & 0x0400` clear means lighting ON and the pool's trailing four bytes are a normal; set means lighting OFF and they are RGBA (`N64RenderBankFile.cs:47-52, 80, 285-286`). The mono grey rig can only produce grey in [70,175] / [95,215] and can never produce yellow, so a group wrongly classified as lit will read as washed-out white-grey — exactly the reported symptom, and exactly the failure class that was already retracted once for over-selecting normals (`N64RenderBankFile.cs:67-76`).

**Investigate**: for the torch fire and the 145 untextured parts specifically, dump `kind`, the resolved lighting flag, and the trailing four bytes. Note whether an untextured material additionally forces a white base colour, which would compound it.

### B4. 🔴 Bundle-name mapping is misaligned in places — REPORTED, with a strong self-check available

Two examples: `168_L1A2_O` appears to actually be JJJViewer, and `L1A2a_L` appears to be a `_G` file for a training map. The user supplies a decisive invariant: **`_L` files are texture libraries and never contain geometry.**

This fits the naming mechanism's known failure mode. `N64BundleNameResolver` maps a case-insensitively sorted TRG family list one-for-one onto a *contiguous run* of slots, deliberately including 24-byte stubs, anchored on a content-named slot (`N64BundleNameResolver.cs:10-46`). Any family member with no slot at all — or any slot the TRG does not name — shifts everything after it by one, which produces exactly this: an `_O` name landing on a character model and an `_L` name landing on geometry.

**Highest-value fix is a post-condition, not a re-derivation**: after aligning a run, assert that every slot receiving an `_L` name carries no geometry, and (weaker) that `_O`/`_G` names land on bundles whose content matches. On violation, decline the run rather than emitting a wrong name — the resolver's existing philosophy is already "return a name only when it is TRUE of the content". This is the same self-validating-exporter pattern that caught three defects in the colour-pulse work.

---

## C. GUI / UX

### C1. 🔴 Expand/collapse controls read as sort buttons (Audio) — CONFIRMED IN CODE

The Expand-all / Collapse-all buttons exist, but they are **chevron icons sitting inside the header row itself**, in column 0, immediately left of the sortable column-header buttons (`AudioConverterTab.xaml:177-200`, glyphs `E70D`/`E70E`, beside "File Name"/"Folder"/"Format"/"Samples"). A chevron pair in a row of sort headers reads as ascending/descending — the user's diagnosis is correct and is a direct consequence of the layout.

**Fix**: move them out of the header grid into a control row **above** the table, right-aligned, with actual text labels ("Expand All" / "Collapse All").

### C2. 🔴 Expanded child rows are indented backwards (Audio)

For expanded containers (e.g. SFX), the parent filename is indented *further right* than its own children. Should be the other way round. Template pair: `AudioConverterTab.xaml` `ParentTemplate` (:14-72) / `ChildTemplate` (:75-108).

### C3. 🔴 File size is shown on children but is not a column (Audio)

Expanded entries display a size, but there is no sortable Size column and the container's own size is never shown. Add a sortable size column (`SortableColumnHeaderButtonStyle` + `FileTableBehavior.SortProperty`, as the existing headers do) and populate it for containers as well as children.

### C4. 🔴 Show audio duration in the file table (Audio) — FEATURE

Track length would be useful in the table alongside format/samples.

### C5. 🔴 SFX cue sheets are listed as if they were audio (Audio)

SFX files are **cue sheets for the VAB banks**, not audio. Listing both side by side is misleading. Preferred shape: list the **VAB** (which holds the actual samples) and use its paired SFX to resolve and display the contents. The engine-exact VAB tone-table walk already exists, so the pairing data is available; this is a presentation change in the audio scanner.

### C6. 🔴 Script Decompiler cannot open archives, and is missing "Select file…"

Confirmed: `ScriptDecompilerTab.xaml:163` offers only "Browse folder…", where the comparable panel at `:404-409` offers both "Select file…" and "Browse folder…". Add the missing button, and wire archive support so `.qb`/`.trg` can be read from inside containers — every other content tab already goes through `ArchiveFileSystem`.

### C7. 🔴 `.z64` missing from archive pickers outside the Texture tab

Confirmed: `.z64` is present **only** in `TextureTab.xaml.cs:13`. `AudioConverterTab` (:16), `BitmapConverterTab` (:13), and `VideoConverterTab` (:16) all use the same list without it.

The underlying question the user raised — N64 full-screen images are currently treated as textures, so they surface in the Texture tab rather than with other images — is a **classification** decision worth settling before adding extensions blindly: decide whether `.img.n64` full-screen art belongs in the Bitmap tab, and if so whether it can be told apart from ordinary textures structurally (the 3-part CI record with its 28-byte header, magic `0x00080410`, is a candidate discriminator). Add `.z64` to whichever pickers end up owning that content.

---

## D. Testing

### D1. 🔶 Default test run is slow, and the corpus gate hides one dominant test

Measured on this machine, 2026-08-12:

| Run | Tests | Result | Wall clock |
|---|---|---|---|
| default | 2,055 | 1,849 passed / 206 skipped | **1m 52s** |
| `--explicit on` | 2,042 | 1,956 passed / 86 skipped | **7m 52s** |

The explicit run is dominated by a **single** test: `PsxColourPulseExportTests.Export_Frame0MatchesTheBake_AcrossEveryPulsedPsxFile` was still running at 7m 50s of the 7m 52s total, i.e. it essentially *is* the sweep's runtime.

The user's hypothesis is broadly right: the default run does read real sample data. That is the documented design — bounded `[Fact]` tests locate real files through `TestPaths.FindSampleFile`, while unbounded enumeration is gated behind `[CorpusFact]`. So the gate is working; the cost is in the bounded tests.

**Actions**: (1) profile the default run and convert the heaviest real-file tests to synthetic fixtures where the file's *content* is not the thing under test; (2) audit for bounded tests that are effectively sweeps and should carry `[CorpusFact]`; (3) note that A1 shows the pulse sweep is also the least informative long test — it only checks frame zero, so making it cheaper and adding a late-playhead case improves both runtime and coverage.
