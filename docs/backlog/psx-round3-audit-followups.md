# PSX round-3 audit — open follow-ups

Round-3 feedback shipped as `73e5999`, followed by `5995cf0` (clipper fix), on
`feat/psx-round3-fidelity`. An adversarial review (17 agents) found 8 real defects behind a
fully green suite; two verification passes (16 and 21 agents) then found defects in those
fixes. 76 unique findings total — most about verification quality, a minority real code
defects. This file records only what is still OPEN.

Full triage + reviewer checklist: <https://claude.ai/code/artifact/e4018698-5cad-494c-9d87-52f928408f07>

## 1. The fully-lit premise is wrong about characters — decide this first

`PsxGeometryHelpers` bakes the FE preview light into *fully* engine-lit files, documented as
"fully-lit = FE prop; characters are mixed (venom 394/414)". Measured with
`PsxAnalyzer lit-census`:

| file | lit faces | branch taken |
| --- | --- | --- |
| `spidey.psx` | 372/372 | **bake** |
| `blackcat.psx` | 338/338 | **bake** |
| `venom.psx` | 394/414 | neutral |
| `docock.psx` | 371/539 | neutral |

So Spider-Man and Black Cat get a *front-end preview* light baked into their in-level vertex
colours — the opposite of what the rule's rationale claims to protect. Everything else in the
FE-light cluster is built on this premise, so re-scope it before refining anything downstream.
Needs a visual call (see the checklist) plus, ideally, RE of whether the engine lights these
characters from the level rig in-level.

## 2. `BakeFeLight` uses a different normal than the writer exports

The bake computes intensity from `ComputePsxVertexNormal(mesh, face, vertexIndex)`, but
`PsxSkinnedGeometryWriter` exports the attachment-resolved normal from the source mesh. For a
stitched vertex (`vertexIndex >= mesh.VertexCount`) the bake falls back to the flat face normal,
so the vertex is shaded for geometry it does not ship with. Reported ~24% of vertices in the 16
newly-restored files off by 20–35% (hamhead mean 33.6%, sandman 107/455, mgun 46/283);
correlates exactly with attachment count — `control.psx` has 0 attachments and is exact.

## 3. `emitPacket` can disable the PS1-fidelity path entirely

`PsxSkinnedGeometryWriter` gate `isPs1 && (bakeEngineLight || !IsEngineLitFace(...))` makes an
all-lit `ModelMesh` emit no packet at all; `GltfModelExporter` then downgrades the vertex type
and the GLB loses **both** `_PSX_COLOR_0` and `_PSX_FLAGS_0`. Reported on 9 corpus GLBs.

## 4. `FindSemiTransparentLayerSteps` step mismatch (disputed)

Assigns step 2 to some faces and step 1 to their edge-neighbours, so shared corners export 0.25
apart; claimed to make DC `SKPH` tearing worse than the pre-commit baseline. **Disputed** — a
second auditor showed the related skware "torn corners" are back-to-back double-sided sheets
separating as intended. Measure before acting; an A/B with the mechanism disabled is the test
(that method already disproved one tearing claim: identical counts with and without).

## 5. Test coverage gaps

- No test references `IsFullyEngineLit`, `IsEngineLitFace` or `BakeFeLight` — the whole
  FE-light cluster can regress silently.
- Nothing pins the viewer's collection-reset location; `node --check` passes with the bug back.
- The What-If corpus guard is inert: green whether or not the fix is present, and no test pins
  any of the 60 nodes the fix releases.
- `Thps2DcSkb2_DoesNotStackThePatchworkWaterTiles` is **vacuous** —
  `FindSemiTransparentLayerSteps(SKB2.PSX)` returns zero entries, so its loop body never runs.

## 6. The census pins are characterization snapshots

`PsxCoplanarOverlayCensusTests` catches gross breakage (removing the gate fails 9 of 13) but not
semantic drift — a wrong denominator or halved threshold stays green, and the clipper
correctness fix in `5995cf0` changed none of the 9 counts. The `l7a2_g = 538` pin is decided by
float32 rounding of the plane key and flips to 531 in double precision. The geometry unit tests
are the real guard; consider deriving pins from an independent oracle instead.

## 7. Unresolved: does the semi-transparent lift always land in front?

The early-out gives no draw-order flag to any coplanar pair with a semi-transparent member,
because that member lifts 0.25 geometrically. Whether that lift always carries the sheet to the
FRONT of what it covers is **not established**. A first pass measured 14 `l7a2_g` sheets plus
one each in `skny`/`SKNY` lifting behind or along the wall, but those pairs could not be
reproduced through the detector's own plane bucketing, and the speculative orientation fix was
reverted rather than shipped unverified. Re-open with an independent oracle.

## Method notes worth keeping

- Corpus sweeps check **validity**, not **semantics** — every defect above produced perfectly
  valid GLBs with 0 validator errors.
- Mutation-test every new guard: delete the fix, confirm red. Two guards that looked reasonable
  pinned nothing until this was applied.
- Measure with the **same** pairing/bucketing the shipped code uses, never a parallel
  implementation in a diagnostic — that discrepancy invalidated a whole investigation.
- Do not ship a behaviour change that cannot be demonstrated firing on the case that motivated it.
