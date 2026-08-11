# Mesh validation

This directory contains the manifest-driven mesh QA gate, reusable GLB
inspectors, material snapshot diffs, Blender render checks, and the THAW PS2
texture regression harness. Generated GLBs, reports, and renders belong under
`TestOutput/`.

## All-format mesh QA

`mesh_qa.py` runs each manifest case in an isolated output directory and never
uses converter console prose as an oracle. It requires the declared GLB output
count, parses each GLB itself, and gates:

- mode-aware triangle counts for TRIANGLES, TRIANGLE_STRIP, and TRIANGLE_FAN;
- structural counts, readable POSITION accessors, finite vertex/node values,
  finite aggregate bounds, and nonzero triangles unless explicitly allowed;
- Khronos validator JSON error counts;
- five built-in `glb-render --preset object-review` review images per GLB; and
- optional triangle recall against another manifest case.

Blender review renders are advisory and opt-in (`blenderReview: true`). The
runner does not parse human-facing Spectre output.

Run the checked-in Windows/PS2 THUG2 Rosetta pair from the repository root:

```powershell
python tools/validation/mesh/mesh_qa.py `
  --cli src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.exe `
  --validator tools/vendor/gltf-validator/gltf_validator.exe `
  --out TestOutput/mesh_qa
```

The corpus root defaults to `Sample/Builds`. Override it with
`--root sampleBuilds=C:\corpus\Builds` or `NEVERSOFT_SAMPLE_BUILDS`. The CLI can
also be supplied through `NEVERSOFT_MULTITOOL_CLI`; the validator can be
supplied through `NEVERSOFT_GLTF_VALIDATOR`. Auto-discovery checks the normal
Release/Debug CLI outputs and `tools/vendor/gltf-validator` before `PATH`.

Khronos validation is required for a complete pass. If the validator is not
installed, the default run exits 2. For a deliberate local structural-only run:

```powershell
python tools/validation/mesh/mesh_qa.py --allow-degraded --validator off
```

An otherwise successful degraded run exits 0 but reports `PASS-DEGRADED` and
`complete: false`; it cannot update the baseline. `--no-render` has the same
explicit-degradation requirement. Blender is optional: pass `--blender PATH`
or set `NEVERSOFT_BLENDER_HELPER`; absence/failure remains an advisory.

### Manifest schema v1

`mesh_qa_manifest.json` is the smallest real cross-format suite currently
checked in. A manifest declares named roots, shared defaults, and exact cases.
Each case has an `id` and `input`, and may add:

- `meshArgs` for explicit converter companions such as `--tex` and `--ske`;
- `companions`, whose content is included in the fixture fingerprint;
- `expect.glbs`, `expect.allowZeroTriangles`, and
  `expect.reviewImagesPerGlb`;
- `oracle.triangleReference`, `minRecall`, and `maxRecall`;
- tags, timeouts, built-in render settings, and optional Blender review.

Unknown fields and unsafe attempts to override the orchestrator's output or
format arguments are configuration errors. Use `--case ID` or `--tag TAG` for
a diagnostic subset. Recall candidates automatically bring in their reference
case.

The checked-in cases are a 12 KiB source/companion pair:

- THUG2 Windows `Anl_Pigeon.skin.xbx`: 1 GLB, 46 emitted vertices, 45
  triangles, 1 node, 1 mesh/primitive/material/texture/image, and 0 skins;
- THUG2 PS2 `anl_pigeon.iskin.ps2`: the same emitted geometry and material
  counts, 6 nodes, 1 skin, and exact triangle recall 1.0 against Windows.

Both baseline fixtures pass Khronos with 0 errors, warnings, infos, or hints.
The PS2 case deliberately names `.iskin.ps2`, its texture, and its skeleton;
the nearby `.skin.ps2` is not an equivalent fixture.

### Baseline and results contract

`mesh_qa_baseline.json` stores every case's source hash, fixture fingerprint,
and exact structural metrics. A full run requires exact case coverage and the
exact manifest SHA-256. A source/companion/argument/expectation fingerprint
change is infrastructure drift (exit 2); accepted output metric drift, missing
coverage, stale coverage, or a changed full manifest is a regression (exit 1).
Malformed baseline schema is exit 2.

Replace the baseline only after reviewing the GLBs and renders:

```powershell
python tools/validation/mesh/mesh_qa.py `
  --cli src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.exe `
  --validator tools/vendor/gltf-validator/gltf_validator.exe `
  --out TestOutput/mesh_qa `
  --update-baseline
```

Baseline update refuses filtered, degraded, render-disabled, incomplete, or
failing runs and writes atomically. It never silently blesses partial coverage.

Every invocation writes stable `results.json` and a browsable `index.html`,
plus per-case conversion logs, emitted GLBs, Khronos JSON, and review PNGs.
Exit codes are:

- `0`: complete pass, or an explicitly requested `PASS-DEGRADED`;
- `1`: mesh acceptance, validator-error, render, oracle, or baseline regression;
- `2`: invalid configuration/baseline, missing tools/fixtures, timeouts, or
  malformed tool output.

The corpus- and product-independent end-to-end self-test creates temporary fake
CLI and validator programs using only the Python standard library:

```powershell
python tools/validation/mesh/mesh_qa_selftest.py
```

## Focused inspectors

Most focused inspectors use only Python 3. Install Pillow and NumPy for
texture/image and accessor checks. The `render_*.py` and
`glb_render_angles.py` scripts run inside Blender and require its bundled
`bpy` module.

```powershell
python tools/validation/mesh/analyze_glb_geometry.py --help
python tools/validation/mesh/glb_material_diff_sweep.py --help
```

Pass an explicit GLB or corpus path to each tool. The material diff reports all
changes; `diff --fail-on-diff` provides an exact-equality gate.
