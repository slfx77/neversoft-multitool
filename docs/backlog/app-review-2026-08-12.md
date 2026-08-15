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

### A2. ⚪ Pulse clock matches the PS1 runtime — NO SPEED CORRECTION 2026-08-12

The reported symptom was "pulse animations play too quickly", but the proposed 30 Hz correction is contradicted by the executable. The target `M3d_PreprocessPulsingColours` at `0x80097CD0` loads `XblanksNow` and `XblanksThen`, computes their unsigned subtraction, and advances every pulse by that delta; only a backwards/wrapped counter substitutes `1`. It does **not** add one per rendered frame. The matching decomp expresses the same rule as `dt = XblanksNow - XblanksThen`.

The viewer already implements that runtime cadence with `textureWibbleFrame + dt * 60`, and the synthetic viewer contract pins that exact shared-clock expression. A1's late-playhead phase corruption is the only demonstrated actionable defect associated with the original observation; halving the clock would now make pulse and UV-wibble timing diverge from the runtime. Re-open this item only with a post-A1 capture that demonstrates a different remaining timing error.

### A3. 🔶 Pulses too bright on some pickups (web cartridges) — LIKELY A1; ONE REAL DEFECT FIXED 2026-08-14

**The reported symptom is most likely A1 and is probably already gone.** Measured against the final build's `items.psx` after the A1 fix: all 14 channels publish (none is silently declined), and every packet key sits at or below its authored display value — the web cartridge's own two channels peak at 0.941, not saturation. In the viewer's PS1-fidelity path the packet is what is sampled, so nothing in the pulse data is overbright at any playhead. Before A1 the walk pinned `amount` to 1.0 and froze each channel on a key; a channel frozen on its bright key reads exactly as "too bright on some pickups", which also explains why only *some* were affected. Re-open with a post-A1 capture if it persists.

**A real, separate defect was found and fixed while measuring.** Channel `portableKeys` were published unclamped, reaching **4.41** on `items.psx`, because a PS1 textured modulation is relative to a 128-neutral texel and its linear form legitimately exceeds 1. The consumer writes an animated key into the **normalized unsigned-short `COLOR_0`** attribute by scaling by 65535 with no clamp, so anything above 1 wraps to an unrelated colour — while the *static* bake into that same carrier is clamped (`PsxOverbrightVertexColor1Texture1`). Animated keys are now clamped identically, so a pulsed corner cannot leave frame 0 for a colour the engine never showed. Latent in the app (PS1 files take the packet path, where `psxFlags.z` keeps `vColor` unused) but live for Blender packages and any external glTF consumer. Pinned by `Export_PortableChannelKeys_StayInsideTheNormalizedCarrier`, mutation-checked.

**Two adjacent findings recorded rather than changed blind.** (1) `packetUsesTexturedScale` (`PsxGeometryWriter.cs:678`) and `isPs1TexturedModulation` (`:663`) disagree for exactly `IsTextured && TextureHash == 0 && IsSemiTransparent` — the "?" marker's 14 faces — so their packet ships in the 128-neutral domain while the shader reads it as display bytes. (2) `psxFlags.x` is derived from whether a face *declares* a texture hash rather than whether one is *bound*, so a zero-hash face takes the shader's textured branch against the `texel5 = 31` white fallback. Both are narrow and provable, but they land on the "?" marker whose appearance was tuned by earlier visual work, so they want a visual A/B rather than a blind edit.

The channel builder already distinguishes the two colour domains per channel key — textured primitives modulate around 128, untextured ones use display RGB (`PsxColourPulseChannels.cs:160-166`, `key.UsesDisplayRgb`) — and channels are keyed by that usage, so a shared pulse index cannot mix domains. Frame 0 is also self-validated against the static bake, so the *endpoints* are right by construction.

That leaves the **interpolation domain** as the suspect: endpoints are transformed and then lerped, which the code itself flags as not frame-exact away from a key (`PsxColourPulseChannels.cs:140-145`, "portable interpolation is intentionally performed after endpoint transformation and can differ at a mid-interval playhead"). Overbright would then appear only *between* keys, which is consistent with "some pickups" rather than all.

**Repro to capture**: web cartridge pickup, note whether the overbright is continuous or only mid-interval; compare `_PSX_COLOR_0` (packet) against `COLOR_1` (portable) sampling for that primitive, and check the 255/128 textured-modulation scale on an additive/untextured pickup face.

### A4. ✅ Surface animation needs its own toggle — FIXED 2026-08-12

Colour pulses and animated (UV-wibble) textures are now independently toggleable from a dedicated **Surface animations** group above the skeletal animation list. Both switches default on and retain their state across model loads and WebView initialization. Disabling an effect restores its authored display state — pulse frame zero or the base UVs — rather than freezing whatever animated frame happened to be visible. The shared 60 Hz clock continues whenever either enabled effect has work, so a pulse-only or wibble-only model still animates correctly. Synthetic viewer-contract tests pin the independent gates, restoration behavior, and exported host function without loading game files.

---

## B. N64 rendering fidelity

### B1. ✅ Transparency cutouts render as solid black on some meshes — FIXED AND ROM-VERIFIED 2026-08-13

`122_venom.psx.n64` rendered Venom's teeth against solid black while **`123_venom.psx.n64` was correct**. The two 581-triangle bundles expose the exact distinction: slot 122's 16-triangle teeth material binds 32x32 I8 texture `2501_psxtxt_755e2673` (332 zero, 206 partial, and 486 full-intensity texels), while slot 123's matching 16-triangle material binds CI4 texture `2503_psxtxt_8aa1d98c` (415 transparent and 609 opaque texels).

The decoder had treated both N64 intensity formats as opaque grayscale. Nintendo's texture contract is instead `R=G=B=A=I`: the intensity value is copied to alpha, specifically so zero backgrounds and filtered edge ramps can be transparent. `N64TexFile` now applies that rule to I4 and I8. More importantly, when the dictionary header's alpha-threshold byte at `+0x2E` is `FF`, its unaligned big-endian flag word at `+0x2F` is retained by the Spider-Man runtime and selects the actual RDP render class: low bits `0/2` emit `AA_ZB_OPA_SURF`, `1` emits `AA_ZB_TEX_TERR` (`CVG_X_ALPHA | ALPHA_CVG_SEL`), and `3` emits the forced-blend cloud/translucent path. Model classification now follows that authored state for I4/I8 rather than guessing from pixel content: opaque ignores physical art alpha, texture coverage maps every non-full alpha to a depth-writing `Mask`, and translucent forces `Blend` while retaining the real art profile. Nonzero-rate face blending and vertex-alpha blending still win; the existing rate-0 cutout rule remains. Non-intensity formats and the separate custom-threshold path retain the existing pixel-profile fallback pending a dedicated audit. Slot 122 sets texture coverage and therefore matches slot 123's known-good `Mask`; ordinary all-partial I modulation such as slot 357 sets opaque and stays opaque.

Byte-exact synthetic I4/I8 regressions pin intensity replication into all four RGBA channels, the four serialized render classes are classified explicitly, the verified `biglight` I4 golden was updated for the corrected alpha channel, and synthetic material rows pin opaque/texture-coverage/translucent behavior. `SpiderManVenomTeeth_IntensityAlphaMatchesTheCutoutControl` runs both reported bundles through the public model importer and pins both teeth materials as `Mask`; Spider-Man slot 150 is the non-regression control, keeping its 40 non-semitransparent triangles over constant-intensity I4 texture `psxtxt_00000002` opaque instead of turning the whole surface 2/3 transparent.

### B2. 🔶 Texture seams — HALF-TEXEL FIXED 2026-08-14; MIRROR STILL UNAVAILABLE

Two instances: a dark line down the middle of the spider emblem on `141.psx.n64` (Symbiote Spidey) and a thin black line along the tops of the skybox buildings in `211_LDA2_O.psx.n64`.

**Cause found and fixed: the N64 writer addressed texel EDGES, not centres.** Stored ST are integer texel indices in S10.5 — spans run 0..N−1 over an N-wide sheet, as `N64ModelWriter`'s own comment already recorded — so sending index `k` to `k/N` puts a linearly filtered sample on the texel boundary, where under REPEAT it blends with the opposite side of the sheet. The PS1 writer has always addressed centres for exactly this reason, and its comment describes the symptom verbatim: *"visible as the texture's bottom row at a model's top seam"* (`PsxGeometryHelpers.cs:283-293`). Half a texel is `+16` in S10.5. `ComputeN64TextureUv` now applies it, and a test pins the N64 and PS1 helpers to the same value for the same texel index so the two paths cannot drift apart again.

**Still open, and not actionable as code.** The tile's clamp/mirror/wrap state is never parsed — and could not be expressed if it were: `ModelTextureWrap` has only `Repeat` and `ClampToEdge`, and the exporter maps only those two, so glTF `MIRRORED_REPEAT` is unreachable. No tile descriptor exists in the display-list token stream either; the only lead is that **6 of the group descriptor's 12 bytes (+4..5, +8..11) are read by no code at all**. That is a research task. Two further contributors were identified but deliberately not changed without a measurement: the viewer gives N64 materials linear filtering *with generated mipmaps* (nearest is applied only to PS1 packet materials), and `N64TexFile` keeps the stored RGB of fully transparent texels with no premultiply or edge-dilation pass anywhere, so transparent BLACK darkens every neighbour it is filtered against. Re-check the two reported models before pursuing either.

### B3. ✅ Untextured geometry renders too bright — FIXED 2026-08-14 (and the torch is not a bug)

Two instances that turned out to be one measured cause and one misreading: `145.psx.n64` (Symbiote) had untextured parts far brighter than the rest of the mesh, and `247_torch.psx.n64` rendered the fire **solid white where the PS1 version is yellow**.

**Cause: N64 vertex colour was gamma-encoded twice.** The RSP does its lighting arithmetic in the console's 8-bit display domain and an unlit vertex's bytes are emitted verbatim, but glTF `COLOR_0` is a LINEAR multiplier applied to an sRGB-decoded texture. The N64 writer wrote the normalized byte straight through — `DisplayRgbToLinear` appeared nowhere under `Mesh/N64/`, while both PS1 writers call it — so ambient 70/255 displayed near 144. `ComputeN64VertexColour` now converts, using the plain sRGB branch rather than the PS1 textured-modulation one, because F3DEX2's combiner neutral is 255 where the PS1 packet's is 128. Alpha passes through untouched, since light shafts fade through real vertex alpha.

This explains 145 exactly. Its bank is **unlit** (8 groups, all bit-set) with authored purple, so its untextured primitives show raw `COLOR_0` with no texture to modulate it, while textured primitives are perceptually dominated by their sheets — "untextured parts far brighter than the rest". It now renders as one coherent figure. Existing lighting pins were re-derived in the exported domain (95/255 leaves as 0.1144, not 0.3725), which is precisely the assertion a doubly-encoded export satisfied.

**The torch's fire cannot be yellow, and that is faithful.** Measured from the carved bank: slot 247 is **14 groups, every one lit and textured**. A lit face's colour is the light alone — the authored bytes are normals — and each ROM uploads exactly one monochrome grey `Lights1`, so the N64 port has no coloured light to give it. The PS1 sibling gets its yellow from authored vertex colour that the port replaced with normals. What was wrong was the brightness, not the absence of hue: the fire was washing toward white and now sits at the console's own 70–165/255 shade band over its texture.

**Two candidate causes were ruled out by measurement rather than argument.** The rig is *not* null for these models — the torch's exported colours span 0.2745–0.6492 in the old domain, exactly the Spider-Man rig's [70,175]/255 envelope, so the `hasNormals && rig == null` white path is not being taken. And the per-node OR of the lighting bit is faithful: walking all four ROMs' render banks finds **0 of 41,905 nodes mixing `kind & 0x0400`** (80,533 display-list groups + 2,071 non-DL = the documented 82,604 descriptors), so moving the verdict per group would be a no-op and is not worth the risk of plumbing a per-triangle flag through a per-node vertex pool.

### B4. ✅ Bundle-name mapping is misaligned in places — FIXED AND ROM-VERIFIED 2026-08-13

Two examples: `168_L1A2_O` appears to actually be JJJViewer, and `L1A2a_L` appears to be a `_G` file for a training map. The user supplies a decisive invariant: **`_L` files are texture libraries and never contain geometry.**

This fits the naming mechanism's known failure mode. `N64BundleNameResolver` maps a case-insensitively sorted TRG family list one-for-one onto a *contiguous run* of slots, deliberately including 24-byte stubs, anchored on a content-named slot (`N64BundleNameResolver.cs:10-46`). Any family member with no slot at all — or any slot the TRG does not name — shifts everything after it by one, which produces exactly this: an `_O` name landing on a character model and an `_L` name landing on geometry.

**Guard and verification**: family alignment now requires every `_L` name to land on an authored-empty 24-byte N64 shell with zero objects. If it lands on geometry or malformed bytes, the entire candidate run is declined and other anchors may still be considered. Existing content-derived checks continue to validate the `_O`/`_G` slots for which a content name is available. Synthetic shifted/valid runs pin the post-condition without depending on a ROM; this deliberately prefers a numeric slot over a confidently wrong name.

The current Spider-Man ROM was then carved through the same `N64AssetCarver` path that supplies archive and GUI rows. Slot 168 now stays numeric as `models/168/168.psx.n64` rather than the false `168_L1A2_O`; slot 169 is correctly `169_l1a2a_g.psx.n64`; slot 170 also stays numeric because it is geometry; and no `L1A2a_L` path is emitted. Both slots 014 and 168 share the same ambiguous `jameson|jjviewer` content key and therefore truthfully remain numeric, while the observed 40 names ending in terminal `_L` all own recognized authored-empty 24-byte shells. `SpiderMan_L1A2ShiftedRunFailsClosedAndEveryNamedLibraryIsAuthoredEmpty` permanently pins the reported slot outcomes and the terminal-`_L` invariant as a focused single-ROM corpus regression. The GUI performs no later name transformation, so this end-to-end carve is also the authoritative UI-label verification.

---

## C. GUI / UX

### C1. ✅ Expand/collapse controls read as sort buttons (Audio) — FIXED 2026-08-12

Previously, the Expand-all / Collapse-all buttons were **chevron icons sitting inside the header row itself**, immediately left of the sortable column-header buttons. A chevron pair in a row of sort headers read as ascending/descending — the user's diagnosis was correct and was a direct consequence of the layout.

**Fix as shipped**: the actions now live in a right-aligned control row above the table with explicit "Expand All" / "Collapse All" labels, separate from the sortable headers.

### C2. ✅ Expanded child rows are indented backwards (Audio) — FIXED 2026-08-12

Expanded samples now begin inside the filename column with an additional 16-pixel inset, so the parent filename is the visual root and its children read as children.

### C3. ✅ File size is shown on children but is not a column (Audio) — FIXED 2026-08-12

The table now has a numeric, sortable Size column. Filesystem rows use file metadata and archive rows use the declared entry size, while child rows retain the per-sample/channel data sizes reported by the format enumerators.

### C4. ✅ Show audio duration in the file table (Audio) — SHIPPED 2026-08-12

The Audio table now has a numeric, sortable Duration column for both stream rows and expandable sample/cue/channel rows. Metadata resolves sequentially off the UI thread from the same byte-backed source used by conversion, so filesystem and archive entries share one contract; rescans cancel stale work, malformed/unreadable leaves stay blank, and XA reuses its duration read for channel discovery instead of reading the entry twice.

Duration follows the decoded output timeline rather than a file-size estimate: ADX/VAG/Xbox PCM/THUG2 SND/PSS show their single stream, raw XA shows its stereo stream, sectored XA uses the longest concurrently multiplexed channel, and multi-track VID uses the longest alternative language track. XA children show their own channel lengths. VAB/KAT parents deliberately stay blank because a sound bank is a set of independent sounds, while raw and SFX-resolved children use each sample's exact decoded frame count and authored/effective cue rate; unsupported encodings remain blank. Display uses the shared playback time format while sorting retains the underlying sub-second numeric value.

### C5. ✅ SFX cue sheets are listed as if they were audio (Audio) — FIXED 2026-08-12

SFX files are **cue sheets for KAT/VAB banks**, not standalone audio rows. The scanner now suppresses every SFX parent and assigns same-directory, same-stem sheets to one unambiguous bank, preferring KAT over VAB when both exist. An otherwise unowned sheet can also inherit a bank from an already exact-anchored sibling sheet in the same directory, using the existing cue-layout score only when the best distinct bank scores at most 24 and leads the runner-up by at least 8. Multiple anchors for one bank collapse to that bank's best score; malformed/unreadable sheets, close or tied scores, different-directory candidates, and ambiguous exact-stem bank sets stay unowned rather than being guessed. Archive identity and alias scope use the entry's full path rather than a flat basename, and scan status reports exact and cross-stem pairings separately.

A bank expands to true resolved cue rows carrying both the authored cue index and actual bank-sample index; preview reads that exact sheet and bank. When no attached sheet yields true cues, invalid/unreadable exact sheets and the conservative KAT full-bank resolution fall back to the raw bank once instead of fabricating cue rows or hiding the audio. Batch conversion now applies the same truth boundary: zero true sheets extracts the raw bank once, one preserves the legacy `<bank>/<cue>.wav` layout, and multiple true sheets export every one beneath deterministic collision-safe `<bank>/<sheet>/<cue>.wav` directories. Malformed and full-bank-fallback sheets do not suppress valid siblings, repeated cue indices cannot overwrite across sheets, and reported sample counts aggregate across all emitted sheets. Unpaired or ambiguous SFX sheets are omitted and counted in scan status.

The CLI's explicit single-SFX route remains unchanged; the ownership and multi-sheet behavior above apply to the Audio tab's bank-oriented folder/archive workflow.

### C6. ✅ Script Decompiler archive and empty-state inputs — FIXED 2026-08-12

The lower input row already had file and folder actions; the empty state was the part missing direct file selection. Both locations now offer **Select file…**, **Select archive…**, and **Browse folder…**. Archive selection uses `ArchiveFileSystem` across every enumerable root type and walks bounded nested containers for `.qb`, `.sqb`, and `.trg` entries, including platform-qualified names. Each entry keeps its complete virtual identity so duplicate basenames remain distinct. Script bytes are buffered once before archive handles close, then the same byte-based parsers drive metadata, preview, and export without temporary extraction. Export naming now treats the final `::` as an archive path boundary and retains deterministic collision disambiguation. The catalog, parser, lifecycle, duplicate-name, and virtual-output cases are covered by synthetic in-memory tests.

### C7. ✅ N64 fullscreen images have an owning archive picker — FIXED 2026-08-12

The existing carver/decoder contract already distinguishes fullscreen `.img.n64` assets structurally: a bounded three-part CI8 record with a 28-byte header and magic `0x00080410`. The Bitmap tab now owns those records, treats their dimensions as self-described, previews/converts them through `N64TexFile`, and accepts `.z64` archives. The Texture tab continues to own `.tex.n64` dictionaries but no longer duplicates fullscreen images. Audio and Video deliberately do not receive `.z64`: neither tab currently has a playable carved-N64 format or a truthful rate/container route, so adding the picker there would only open a ROM and report zero entries. Synthetic tests pin exact RGBA decode, case-insensitive routing, malformed failure, and overflow-safe stride validation.

---

## D. Testing

### D1. ✅ Default test cost reduced and remeasured — FIXED 2026-08-13

Measured on this machine, 2026-08-12:

| Run | Tests | Result | Wall clock |
|---|---|---|---|
| default | 2,055 | 1,849 passed / 206 skipped | **1m 52s** |
| `--explicit on` | 2,042 | 1,956 passed / 86 skipped | **7m 52s** |
| default after cleanup | 2,836 | 2,137 passed / 699 skipped | **46.97s** |

The explicit run is dominated by a **single** test: `PsxColourPulseExportTests.Export_Frame0MatchesTheBake_AcrossEveryPulsedPsxFile` was still running at 7m 50s of the 7m 52s total, i.e. it essentially *is* the sweep's runtime.

The user's hypothesis is broadly right: the default run does read real sample data. That is the documented design — bounded `[Fact]` tests locate real files through `TestPaths.FindSampleFile`, while unbounded enumeration is gated behind `[CorpusFact]`. So the gate is working; the cost is in the bounded tests.

**Actions**: (1) profile the default run and convert the heaviest real-file tests to synthetic fixtures where the file's *content* is not the thing under test; (2) audit for bounded tests that are effectively sweeps and should carry `[CorpusFact]`; (3) note that A1 shows the pulse sweep is also the least informative long test — it only checks frame zero, so making it cheaper and adding a late-playhead case improves both runtime and coverage.

**Cleanup and result**: the heavy Xbox TEX/IMG, N64 model/animation, AnimationDiscovery, VID1, MDEC, PAK, ERZ, N64-audio, PSX alternate-leaf, THAW worldzone-QB, and QB-section archive cases now use explicit corpus attributes. Five real compressed-PRE checks are also corpus-gated, while seven generated rows retain v2/v3 detection, listing, CRC, LZSS extraction, callback, and `.pre`/`.prd` naming coverage in the default run. Existing synthetic parser, malformed-input, late-playhead, and path-policy coverage remains ordinary. The serialized 2026-08-13 default run completed all 2,836 discovered cases with 2,137 passed, 699 explicit/corpus skips, and zero failures in 46.97 seconds wall time—65.03 seconds, or 58%, faster than the prior 1m52 baseline. The full pulse corpus sweep remains deliberately explicit; its missing late-playhead coverage is now supplied by the focused synthetic A1 regressions.
