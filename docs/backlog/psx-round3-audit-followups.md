# PSX round-3 audit — open follow-ups

Branch `feat/psx-round3-fidelity`. Round-3 feedback shipped as `73e5999`; an adversarial review
(17 agents) found 8 real defects behind a fully green suite, and two verification passes (16 + 21
agents) found more in those fixes. 76 unique findings total — mostly verification quality, a
minority real defects.

Revised 2026-08-10. Several items in the previous revision are now CLOSED and are listed as such so
nobody re-opens them from a stale doc.

## Closed

| item | outcome |
| --- | --- |
| "fully lit = front-end prop" premise | Superseded. The rig is runtime context (`mpLight`, assigned only by game code) and is now never inferred — `PsxEngineLight.Presets` is caller-selected via `mesh --psx-light`. |
| `emitPacket` stripping packets | Fixed in `b18990f`. Gating it on the lit state made an all-lit file emit no packet at all, killing the viewer's PS1-fidelity path; restored to "always emit for PS1". |
| control.psx flat shading | Fixed in `b18990f`. The lit-face neutral rule had been extended from v6 to PS1, collapsing 11 authored colours to one; scoped back to `version == 0x06 && UsesDynamicLighting`. User-confirmed correct. |
| Clipper convexity precondition | Fixed in `5995cf0`. Clips the renderer's two triangles instead of a strip-order perimeter that can be concave or self-intersecting; result clamped to a valid proportion. |
| Apparition shown by default | Fixed in `3006a97`. The placing node is a proximity trigger (`C_WAIT_FOR_COLLISION` / `C_DIE_QUIETLY`), so the group is default-off. |
| Vacuous SKB2 guard | Fixed in `6ecbeec`. `FindSemiTransparentLayerSteps` returns zero entries there, so the `foreach` asserted nothing; now asserts the outcome. |
| Unreproducible figures | Corrected in CLAUDE.md, the detector comments and the test comments. |

## Open

### 1. `BakeEngineLight` uses a different normal than the writer exports — CLOSED 2026-08-05

Fixed: a skinned overload of `BakeEngineLight` performs the same attachment resolution as the
writer's normal export, so a stitched corner's baked light agrees with its shipped NORMAL. Pinned
by `PsxEngineLightTests.BakedLight_IsAFunctionOfTheExportedNormal_OnAStitchedCharacter` (hamhead:
zero same-normal colour conflicts; the raw-mesh overload fails it). Original finding below.

#### (original) `BakeEngineLight` uses a different normal than the writer exports

`PsxGeometryHelpers.BakeEngineLight` computes intensity from
`ComputePsxVertexNormal(mesh, face, vertexIndex)`, while `PsxSkinnedGeometryWriter.cs:377` exports
`ComputePsxVertexNormal(normalMesh, face, normalVertexIndex)` — the attachment-resolved mesh. For a
stitched vertex the baked light and the shipped NORMAL therefore disagree. Reported at ~24% of
vertices in the affected files, 20–35% off (hamhead mean 33.6%). Now only reachable when a
`--psx-light` preset is selected, so it is no longer a default-path defect, but it is still wrong.

### 2. `IsFullyEngineLit` is dead code — CLOSED 2026-08-05

Deleted from both `PsxMesh` and `PsxMeshFile` (the discredited "whole file is lit" signal;
nothing referenced it since the lit gate became per-face). Build + full suite green.

### 3. Layer-step tearing — CLOSED 2026-08-11

The disputed DC SKPH case is now pinned from its actual face-instance topology, without changing
production behavior. Final `SKPH.PSX` has 279 semi-transparent faces. The detector assigns step 2 to
exactly 30 faces: every face, and only faces, using texture `0xA89F675A`. Its step-1 partner texture
`0x71E2F16A` also has exactly 30 faces, with 30 exact whole-face corner matches between the layers.
Across every non-vacuous same-texture face pair sharing an authored world-space corner, the effective
step is identical, so no connected face within either layer tears. Final PSX `skware.psx` has zero
step-2 faces, ruling out this mechanism as the source of its reported seams. The 0.25 difference is
only between the two complete SKPH layers it is intended to separate.

`PsxCoplanarOverlayDetectorTests.Thps2DcSkph_ExtraStepsSeparateWholeLayersWithoutTearingEitherLayer`
pins the 279/30/30 populations, the actual face keys and corners, all 30 layer pairings, the
same-texture shared-corner invariant, and the skware negative control.

### 4. Does the semi-transparent lift always land in front? — unresolved

The early-out gives no draw-order flag to any coplanar pair with a semi-transparent member because
that member lifts 0.25 geometrically. Whether the lift always carries it to the FRONT of what it
covers is not established. A first pass measured 14 `l7a2_g` sheets plus one each in `skny`/`SKNY`
lifting behind or along the wall, but those pairs could not be reproduced through the detector's own
plane bucketing, and the speculative orientation fix was reverted rather than shipped unverified.
Re-open with an independent oracle.

### 4b. Coincident-geometry residue — CLOSED 2026-08-10

The independent oracle requested by items 4 and 5 reads exported GLB world-space geometry and
never calls the detector, so it cannot agree with it by construction. That census corrected four
flaws that had inflated every earlier count:

| correction | effect |
| --- | --- |
| centroid proximity → exact clipped shared area | adjacent triangles of one surface were counted as overlapping (l2a1 3555 → 135 pairs) |
| classify appearance before alpha mode | MASK pairs painting identical pixels ranked as actionable |
| credit `neversoftDrawIndex` | overlays ship as draw-order metadata, NOT a lift, so a resolved pair legitimately has gap 0.0 |
| credit backface culling | back-to-back single-sided walls cannot fight: 69/104 skschl, 111/135 l2a1 |

Residue after those corrections: **skschl 11 actionable pairs, l2a1 4** — not the 80 previously
reported. The 2026-08-04 conclusion that every same-file pair reached `ClassifyPair` was wrong: its
"detector plane key" was recomputed from each emitted GLB triangle, while the then-current production
code assigned a single primary-triangle key to each source face.

Ruled out by measurement, so nobody re-tests them: semi-transparency (no residue face has flag
0x40), the appearance twin rule (their colours differ), bounds overlap (penetration is hundreds of
units on two axes), and interior overlap (shared area 0.13–1.00 of the smaller face).

The production diagnostic reports the selected overlay or exact `ClassifyPair` decline, separates a
decline from a pair production never compared, and exposes both rendered triangle planes for quads.
The 14 same-file oracle pairs collapse to eight source-face pairs. Before the fix all eight reported
`DifferentPlaneBuckets`; focused tests pin that attribution as well as every classifier decline.

The misses had three measured causes:

- coincident faces could quantize to adjacent plane-distance buckets (the key differed by exactly one);
- a quad's secondary writer-emitted triangle could match another face while production bucketed the
  whole quad by its primary triangle; and
- sprite faces were compared from raw anchor/offset fields even though the writer expands them to
  their actual rendered corners.

The same-file behavior is now fixed. Opaque discovery uses both writer-emitted quad triangles and the
same `PsxSpriteVertexResolver` corners as export, and compares adjacent distance buckets only when the
unquantized gap is at most **0.005** (the six measured non-sprite pairs span 0..0.0048828125). A
secondary admission must establish shared area on the two matched triangles, not the whole warped
quad. This rejects three Marseille slivers whose whole faces overlap 4.8..6.9% but admitted triangles
overlap only 0.23..0.47%, and a neighbouring 0.0073242188 plane gap stays undiscovered. The
semi-transparent layer detector deliberately retains its separately calibrated raw/primary path.

All eight motivating same-file source pairs now select the independently expected overlay. The pinned
nine-file census changes only at explained sites: `l2a1_g` 77→80 (one secondary-triangle pair and two
sprite cards), PSX/DC Marseille +2 each (distance seams), and DC Philly 27→41 (six nested foliage
panels and eight segmented pole strips); the other fixtures are unchanged.

The fifteenth pair — `school_outerwall05` vs `obj_gym_door` — is fixed without broadening the
classifier. `PsxPlacedCoplanarOverlayResolver` assembles writer-equivalent level geometry plus the
bank placements that survive What-If/items/sky/hidden-object filtering, then delegates to the same
detector. Assignments include the placement index, so a face is split only at the transform where it
overlaps; exact duplicate transforms are classified once and expanded back to their emitted nodes.
The synthetic appearance identity uses resolved per-file colours, authoritative widened UVs,
colour-pulse identity, and structural texture-wibble identity, avoiding raw palette-index aliases.
Discovery is optional and fail-open: an unexpected face cannot suppress bank geometry.

The final School fixture yields exactly one cross-file pair: level object 200 face 0 against bank
object 4 placement 0 face 1, with the bank face selected. Its coincident PLATFORM node 215 replaces
the bank home slot, so exactly one door instance receives draw-order metadata. A rotated duplicate
and far-repeat regression proves placement isolation; final Downhill's 936 level objects plus 23
bank placements complete the same scan in under one second on the audited machine and find no pair.
`PsxPlacedCoplanarOverlayResolverTests` pins the adapter, production parser output, colour/UV/animation
provenance, and a five-second upper guard for that large scope.

### 5. Remaining coverage gap

Closed 2026-08-10: the What-If guard now has exact real-corpus identity pins, not only aggregate
placement counts. Final Spider-Man `l1a3` node 322 and `l5a3` nodes 192/196/198 are asserted by
node/object/mesh/hash to disappear in normal play and return only under the opt-in group. Enter
Electro `e1m2` node 316 and `e3m3` nodes 3/306 pin the opposite `else`-branch case: their normal-play
models remain present and are not misclassified as What-If-only content. The synthetic grammar tests
remain alongside these corpus anchors in `PsxWhatIfContentGateTests`.

- The overlay census pins are characterization snapshots. Geometry tests now cover shared-area and
  plane-seam semantics, but the underlying `l7a2_g` candidate population still has a known seven-face
  float32-vs-double plane-key sensitivity that has not been given a format-grounded precision rule.

Closed 2026-08-09: `PsxColourPulseViewerContractTests` extracts the actual viewer functions and pins
that append-only material/wibble/pulse collections reset only in `unloadCurrent`, not once per loaded
GLB root. Restoring the leak now fails the contract test even though `node --check` still passes.

### 6. Spider-Man's light rigs — CLOSED 2026-08-03

The shipped-binary xref trace found exactly two
`mpLight` (offset 0x38) assignments: the item constructor at 0x80058D90 using
`M3d_DefaultLight` (0x800A6214), and 0x80048214 using **0x80098F1C**, whose
constructor (0x80047DF8) is called from two sites that each allocate a
0x1020-byte object — the game's largest entity, the structural analogue of
THPS2's CBruce. Shipped as the `spiderman-player` preset in `PsxEngineLight`,
with preset coverage in `PsxEngineLightTests`.

0x80098E00 and 0x80098E40 are referenced by nothing at all and are NOT exposed.

### 6b. Controller-config light rig — unresolved

Which rig the controller-config screen uses remains open. It is not one of the
two assignments above, so it is set somewhere the scan did not reach (a front-end
path assigning through a different offset or a copied struct).

## Needs the user, not code

- Visual pass over what is genuinely left of the round-2/3 list: sprite billboards (trees,
  antennas) and skmar/skware decal z-fighting. Pruned 2026-08-16 — sky domes, DC chain-link
  fences and pickup heights were still listed here although `mesh-fidelity.md` records all three
  as fixed and pinned in the 2026-07-28 THPS2-DC batch; control.psx and the l1a2 apparition were
  already noted as confirmed.
- Dreamcast emulator screenshots (Flycast, Philly trees) — the bit7 "missing geometry" conclusion
  rests on disassembly plus TRG cross-reference across 10 binaries, with no in-engine check on the
  one platform never tested.

## Method notes worth keeping

- Corpus sweeps check **validity**, not **semantics** — every defect above produced valid GLBs with
  0 validator errors.
- Mutation-test every new guard: delete the fix, confirm red. Three guards that looked reasonable
  pinned nothing until this was applied.
- Measure with the **same** pairing/bucketing the shipped code uses, never a parallel implementation
  in a diagnostic — that discrepancy invalidated a whole investigation.
- Do not ship a behaviour change that cannot be demonstrated firing on the case that motivated it.
- Hand-compute test expectations from the source data, not from what the code prints. That caught a
  real error in the light maths (the skater rig's 1.25 saturates).
