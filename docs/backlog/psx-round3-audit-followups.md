# PSX round-3 audit — open follow-ups

Branch `feat/psx-round3-fidelity`. Round-3 feedback shipped as `73e5999`; an adversarial review
(17 agents) found 8 real defects behind a fully green suite, and two verification passes (16 + 21
agents) found more in those fixes. 76 unique findings total — mostly verification quality, a
minority real defects.

Revised 2026-08-03. Several items in the previous revision are now CLOSED and are listed as such so
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

### 3. Layer-step tearing (disputed)

`FindSemiTransparentLayerSteps` gives step 2 to some faces and step 1 to their edge-neighbours, so a
shared corner exports 0.25 apart; claimed to make DC SKPH worse than baseline. A second auditor
showed the related skware "torn corners" are intended double-sided separation. Measure with an A/B
against the mechanism disabled before acting — that method already disproved one tearing claim
(identical counts with and without).

### 4. Does the semi-transparent lift always land in front? — unresolved

The early-out gives no draw-order flag to any coplanar pair with a semi-transparent member because
that member lifts 0.25 geometrically. Whether the lift always carries it to the FRONT of what it
covers is not established. A first pass measured 14 `l7a2_g` sheets plus one each in `skny`/`SKNY`
lifting behind or along the wall, but those pairs could not be reproduced through the detector's own
plane bucketing, and the speculative orientation fix was reverted rather than shipped unverified.
Re-open with an independent oracle.

### 4b. Coincident-geometry residue — measured 2026-08-04, cause not yet isolated

`tools/diagnostics/glb_coincident_census.py` is the independent oracle items 4 and 5 asked for. It
reads EXPORTED GLB world-space geometry and never calls the detector, so it cannot agree with it by
construction. Building it corrected four flaws that had inflated every earlier count:

| correction | effect |
| --- | --- |
| centroid proximity → exact clipped shared area | adjacent triangles of one surface were counted as overlapping (l2a1 3555 → 135 pairs) |
| classify appearance before alpha mode | MASK pairs painting identical pixels ranked as actionable |
| credit `neversoftDrawIndex` | overlays ship as draw-order metadata, NOT a lift, so a resolved pair legitimately has gap 0.0 |
| credit backface culling | back-to-back single-sided walls cannot fight: 69/104 skschl, 111/135 l2a1 |

Residue after those corrections: **skschl 11 actionable pairs, l2a1 4** — not the 80 previously
reported. Every one lands in the SAME detector plane bucket, so bucketing is NOT the cause and all
were compared and declined by rule.

Ruled out by measurement, so nobody re-tests them: semi-transparency (no residue face has flag
0x40), the appearance twin rule (their colours differ), bounds overlap (penetration is hundreds of
units on two axes), and interior overlap (shared area 0.13–1.00 of the smaller face).

Confirmed for exactly ONE pair — `school_outerwall05` vs `obj_gym_door`: the detector runs on a
single `PsxMeshFile`, so a bank object PLACED into a level is never compared against level
geometry. That is a structural scope gap, not a rule gap.

The other 14 are level-vs-level and cannot be attributed from outside the detector. The next step is
to make the decline reason observable from the shipped code (an out-parameter on `ClassifyPair`,
not a parallel reimplementation in a diagnostic — that discrepancy invalidated an earlier
investigation). Deliberately NOT fixed speculatively at the end of round 5: the round-3 rule was
"do not ship a behaviour change that cannot be demonstrated firing on the case that motivated it".

### 5. Remaining coverage gaps

- Nothing pins the viewer's collection-reset location; `node --check` passes with the bug restored.
  The whole of `mesh-viewer.html` has no test.
- The What-If corpus guard is inert: green whether or not the fix is present, and none of the 60
  nodes the fix releases is pinned anywhere.
- The overlay census pins are characterization snapshots. The geometry unit tests now cover the
  shared-area semantics, but `l7a2_g = 538` is decided by float32 rounding of the plane key and
  flips to 531 in double precision.

### 6. Spider-Man's light rigs — CLOSED 2026-08-03

Traced with `tools/diagnostics/psx_xref_scan.py`. The binary makes exactly two
`mpLight` (offset 0x38) assignments: the item constructor at 0x80058D90 using
`M3d_DefaultLight` (0x800A6214), and 0x80048214 using **0x80098F1C**, whose
constructor (0x80047DF8) is called from two sites that each allocate a
0x1020-byte object — the game's largest entity, the structural analogue of
THPS2's CBruce. Shipped as the `spiderman-player` preset.

0x80098E00 and 0x80098E40 are referenced by nothing at all and are NOT exposed.

Still open from this thread: which rig the controller-config screen uses. It is
not one of the two above, so it is set somewhere the scan did not reach (a
front-end path assigning through a different offset or a copied struct).

## Needs the user, not code

- Visual pass over the rest of the round-2/3 list: sky domes, sprite billboards (trees, antennas),
  DC chain-link fences, skmar/skware decal z-fighting, pickup heights. control.psx and the l1a2
  apparition are confirmed done.
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
