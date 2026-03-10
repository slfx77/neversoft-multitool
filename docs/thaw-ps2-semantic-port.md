# THAW PS2 Semantic Port

## Status

The current THAW PS2 shipping path stays on the existing heuristic mesh extractor in
`ThawPs2SkinFile`. This document locks the replacement plan and the internal seams for
the semantic-port replay layer. The first milestone scaffolding now exists in
`ThawPs2Replay.cs` and is exposed through `ThawPs2SkinFile.ReplayBatches(...)` for
trace-oriented tests and diagnostics.

## Locked Decisions

- Keep `Ps2SceneGltfWriter` as the first downstream consumer.
- Replace topology reconstruction upstream of glTF export, not export semantics first.
- Port only the needed PCSX2 behavior into repo-native C#.
- Do not embed PCSX2, shell out to PCSX2, or add native dependencies.
- Scope replay to the THAW PS2 skin path only:
  - VIF stepping and `vif_next_code` behavior
  - `STCYCL`, `BASE`, `OFFSET`, `ITOP`, `STMOD`, `STMASK`
  - `UNPACK` addressing, `FLG`, `USN`, and CL/WL write behavior
  - double-buffer state: `TOPS`, `TOP`, `DBF`
  - `MSCAL` and `MSCNT` save-and-flip behavior with `XTOP`-visible batch starts
  - the subset of VU1 output behavior needed to explain kicked strip data
  - `XGKICK` and GIF packet interpretation for vertex emission
  - GS queue semantics for strip reconstruction, specifically `XYZ` and `ADC`
- Explicitly exclude rasterization, texture sampling, full GS emulation, and unrelated
  PS2 scene formats.

## Internal Seams

The replay implementation is split into four internal seams. These are the units that
later milestones should expand instead of adding more parser heuristics.

- `VifReplayState`
  - Owns VIF register state and double-buffer state per command.
  - Tracks `CL`, `WL`, `BASE`, `OFFSET`, `TOPS`, `TOP`, `DBF`, `ITOP`, `STMOD`,
    and `STMASK`.
- `Vu1BatchSnapshot`
  - Captures replay-state facts at each `MSCAL` or `MSCNT` boundary.
  - Holds `XTOP`, pre/post `TOPS`, written-memory span, decoded parser tag, and the
    set of `UNPACK` commands that produced the batch.
- `GifKickPacket` and `GsVertexEvent`
  - `GifKickPacket` is the replay-side packet summary that already exists.
  - `GsVertexEvent` is the next seam to add when replay moves from batch snapshots to
    deterministic strip emission.
  - It should carry the ordered GS queue events, including `ADC/no-kick`.
- `ThawReplayMeshExtractor`
  - Planned replacement for the current `c2/c3`-driven topology reconstruction path.
  - Its input is replayed kick and vertex events, not parser heuristics.
  - Its output remains `Ps2Mesh` and `Ps2Vertex` so the existing writer can stay in place.

## Corpus And Proving Set

The rollout and validation corpus is all THAW PS2 skins. The fixed proving set is:

- `skater_lasek`
- `skater_hawk`
- `acc_backpack01`
- `body_f_torso`
- `pro_vallely_head`
- `sec_jimbo_xen`

`skater_lasek` stays first because it exposes both missing faces and incorrect
connections.

## Current Heuristic Inventory

This is the replacement map for the current parser behavior.

| Current behavior | Classification | Replay outcome |
| --- | --- | --- |
| `FLUSH+DIRECT` setup detection | True format fact | Survives as format decoding |
| pre-first-`FLUSH` preamble batches | True format fact | Survives as format decoding |
| `NLOOP` output-window bounding | Inferred VU1 behavior | Disappears into replayed kick packets |
| setup-local slot buffers | Inferred VU1 behavior | Disappears into replayed VU1 state |
| constrained second-pair copies | Unsupported guess | Removed when replay emits real kick events |
| address-gap fallback splitting | Unsupported guess | Validation-only fallback during rollout, then removed from THAW path |
| oversized-triangle restart filter | Exporter workaround | Validation-only diagnostic after replay is complete |
| odd-slot parity tracking | GS strip fact | Survives, but becomes replay-derived instead of heuristic |

## Architecture

`ThawPs2SkinFile` should converge on a thin orchestration role:

1. Parse the THAW container and entry tables.
2. Find setup ranges, including the preamble range before the first setup boundary.
3. Hand VIF chains to the replay layer.
4. Convert replayed kick output into `Ps2Scene`.

The current writer stays unchanged except for metadata it already supports, such as
strip parity or restart markers sourced from replay.

Diagnostics stay available, but they remain separate from shipping parser code. The
Python simulator and scratch tools are truth-discovery inputs, not production
dependencies.

## Rollout

### Milestone 1

Trace-parity scaffolding for `skater_lasek`.

- Land replay-state types and `ReplayBatches(...)`.
- Verify VIF register transitions and `XTOP` snapshots against the Python reference.
- Do not switch the production parser.

Stop/go gate:

- replay-state trace parity first
- no production THAW parser regression

### Milestone 2

Replay-driven mesh extraction for the fixed proving set.

- Add ordered GS queue events.
- Add `ThawReplayMeshExtractor`.
- Compare replay-driven strips against the current heuristic output and PC meshes.

Stop/go gate:

- batch-level replay parity
- visible topology improvement on the proving set

### Milestone 3

THAW-wide rollout across all skins.

- Switch THAW skin parsing from heuristic reconstruction to replay extraction.
- Keep THAW-wide zero-failure conversion and glTF validation as hard gates.

Stop/go gate:

- replay parity holds across the proving set
- THAW-wide conversion remains zero-failure
- glTF validation remains clean

### Milestone 4

Delete obsolete heuristic branches.

- Remove setup-local slot-buffer reconstruction.
- Remove constrained second-pair copy logic from the THAW shipping path.
- Keep only replay-backed metadata and validation diagnostics.

## Acceptance Checks

### Replay Parity

- VIF register transitions match the reference trace for proving files.
- `XTOP` snapshots match at every batch boundary on proving files.
- Kick-packet counts and `ADC/no-kick` placement match the replay reference.
- Double-buffer phase changes and batch boundaries are identical.

### Mesh Parity

- Compare exported non-degenerate triangle counts against PC companions.
- Keep unique unordered triangle-set comparison, but do not treat it as the only metric.
- Keep `skater_lasek` as the first visual review file.

### Regression Gates

- Existing THAW parser tests stay in place until replay fully replaces the heuristic path.
- THAW-wide zero-failure conversion remains required.
- glTF validation remains required.
- “Count matches but topology is still wrong” is detected by:
  - unique unordered triangle-set comparison
  - proving-set visual review
  - replay-event parity against the reference trace

## Implementation Notes

- The replay layer is intentionally additive until Milestone 3.
- The shipping parser should not depend on replay-only interpretations until replay
  parity is strong enough to replace the current topology reconstruction path.
- The Python simulator and scratch harnesses should be treated as reference or analysis
  tools only; the production target remains repo-native C#.
