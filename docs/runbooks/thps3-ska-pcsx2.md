# THPS3 PS2 SKA - PCSX2/Ghidra Matrix Runbook

Goal: reproduce or independently corroborate the closed THPS3 final-palette
oracle. The retained `skater_m_Idle.ska` capture already selected the production
formula; this runbook is no longer a prerequisite for implementation.

## Inputs

- PCSX2 build: `<pcsx2>\pcsx2-qt.exe` (set `PCSX2_EXE` for the capture scripts)
- Game: Tony Hawk's Pro Skater 3, PS2 final.
- Model: `Sample/Builds/Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)/SKATE3/pre/cas_male/models/skater_m/skater_m.skn`
- Animations: `tests/TestData/Thps3/Ska/skater_m_Idle.ska`, `tests/TestData/Thps3/Ska/skater_m_AirIdle.ska`
- Historical export sweep: `thps3_variant_sweep.py`. It references a removed
  diagnostic CLI mode and must be modernized before it can be run again; it is
  retained only to reproduce the pre-palette visual investigation.

## Proven result (2026-08-11)

- Retained state: `SLUS-20013 (F77E2FB5).01.p2s`, 17,680,740 bytes, SHA-256
  `633B8BB6C80E34E212F693DAD09D29A4ADD4568859A2C11056861B38B897CD05`.
- Pose `0x00B404C0`; final 29×0x40 RwMatrix palette `0x00B415F0`;
  sample time `0.799999475479126s`.
- Local transform: raw authored T plus `conjugate(runtime XYZW Q)`.
  Compose `local * parentGlobal`, then `IBM * global` in System.Numerics row
  convention. The non-joint `skeleton_root` owns only Z-up→Y-up conversion.
- Fresh GLB vs all 348 semantic RwMatrix floats: RMSE `0.000377701`, maximum
  error `0.00223009`. The next row-oriented convention is RMSE `4.97997`.
- RwMatrix layout is `right.xyz/+0x0C flags`, `up.xyz/+0x1C pad`,
  `at.xyz/+0x2C pad`, `pos.xyz/+0x3C pad`. The fourth words are not matrix
  elements; normalize them to `0/0/0/1` before comparison.

Capture these sample times:

| Animation | frame 0 | mid-animation | loop-end |
| --- | ---: | ---: | ---: |
| `skater_m_Idle` | `0.000000s` | `0.533333s` | `1.066667s` |
| `skater_m_AirIdle` | `0.000000s` | `0.666667s` | `1.333333s` |

## Historical Step A - Confirm the visual ambiguity

1. Run the diagnostic sweep and inspect:
   - `TestOutput/thps3_variant_sweep/contact_sheets/skater_m_Idle_az0.png`
   - `TestOutput/thps3_variant_sweep/contact_sheets/skater_m_Idle_az90.png`
   - `TestOutput/thps3_variant_sweep/contact_sheets/skater_m_AirIdle_az0.png`
   - `TestOutput/thps3_variant_sweep/contact_sheets/skater_m_AirIdle_az90.png`
2. At that stage, no mode was promoted merely because it looked better in one
   animation or one camera. This visual ranking is now superseded by the final
   palette oracle above.

## Step B - Find the interpolator output in Ghidra

1. Load the THPS3 PS2 ELF in Ghidra with the same EE processor setup used for
   previous PS2 analysis.
2. Locate the SKA interpolator path by searching for references to the loaded
   SKA header fields: bone count `29`, key counts `158/71` for Idle and
   `132/69` for AirIdle, and the duration values listed above.
3. Follow the function that expands SKA rotation/translation keys into a bone
   matrix palette. Prior notes for this codebase point at
   `FUN_00230f68` / `FUN_00231048` as likely interpolator functions; verify
   these against the THPS3 executable before trusting the names.
4. Identify the destination buffer for the 29 per-bone matrices after
   interpolation but before skinning. Record:
   - function entry address,
   - destination pointer register or stack slot,
   - matrix stride,
   - matrix convention evidence: row-major vs column-major and local vs model.

The build-specific Ghidra project and one-off call-graph script were removed
after their findings were incorporated. Use the parameterized scripts under
`tools/reverse-engineering/ghidra/` for a fresh binary and addresses.

Preserved static findings from that pass:

- `FUN_00230f68 @ 0x00230F68` loops over the bone count and advances Q by
  `0x18` bytes and T by `0x14` bytes.
- `FUN_0022ff38 @ 0x0022FF38` is the per-bone interpolation kernel called by
  `FUN_00230f68`. It writes one interpolated Q record and one interpolated T
  record for the current bone.
- `FUN_00231048 @ 0x00231048` composes quaternions in `x,y,z,w` order using
  Hamilton `qA * qB`, then adds translations as `tA + tB`.
- `0x00231230` follows pose `+0x24` to the final matrix path;
  `0x0022EF50` constructs local RwMatrices and `0x002860D0` applies the
  hierarchy/inverse-bind palette composition.
- Together with the retained palette these establish the production formula;
  the earlier “not enough for a default change” conclusion is superseded.

## Step C - Break on the matrix write in PCSX2

1. Enable PINE before launching PCSX2. In the UI, enable the PINE server, or
   set `EnablePINE = true` and `PINESlot = 28011` in the INI named by
   `PCSX2_INI` (normally `$env:USERPROFILE\Documents\PCSX2\inis\PCSX2.ini`),
   then restart PCSX2.
2. Launch THPS3 in PCSX2 and reach a state where the skater model is loaded.
3. Verify that the diagnostic helper can see the emulator:

   ```powershell
   python tools/research/thps3-animation/thps3_matrix_dump.py --pine
   ```

4. Open the EE debugger: `Debug -> Open CPU Debugger`.
5. Set a breakpoint at the verified interpolator function or at the matrix
   store loop from Step B.
6. When the breakpoint hits for `skater_m`, inspect the animation time or key
   cursor state. Advance until the target samples are hit: frame 0, mid, and
   loop-end for both Idle and AirIdle.
7. Dump the 29 matrices from the destination buffer for each sample. Preserve
   raw float order and also export a normalized JSON/CSV form with:
   `animation`, `time`, `bone`, `m00..m33`.

If the matrix buffer address is known, dump it without hand-copying:

```powershell
python tools/research/thps3-animation/thps3_matrix_dump.py --pine `
  --addr 0x00ABCDEF `
  --animation skater_m_Idle --time 0.533333 `
  --out TestOutput\thps3_runtime_matrices\idle_mid.json
```

For a saved `.p2s` state, use the same address against `eeMemory.bin`:

```powershell
python tools/research/thps3-animation/thps3_matrix_dump.py `
  --savestate "$env:USERPROFILE\Documents\PCSX2\sstates\SLUS-20013 (F77E2FB5).01.p2s" `
  --addr 0x00B415F0 `
  --animation skater_m_Idle --time 0.799999475479126 `
  --out TestOutput\thps3_runtime_matrices\idle_mid.json
```

## Historical Step D - Compare against exporter matrices

The following records the pre-closure sweep procedure. The sweep script still
references a removed diagnostic CLI and cannot be rerun until it is modernized;
use `Thps3SkaRuntimePaletteTests` for the current production acceptance gate.

1. Export each historical diagnostic mode to GLB using the then-current
   `thps3_variant_sweep.py`.
2. Compare the runtime JSON against all sweep GLBs:

   ```powershell
   python tools/research/thps3-animation/thps3_matrix_compare.py `
     --runtime TestOutput\thps3_runtime_matrices\idle_mid.json `
     --sweep-root TestOutput\thps3_variant_sweep `
     --top 20 `
     --out TestOutput\thps3_runtime_matrices\idle_mid_compare.json
   ```

3. Repeat for all six samples: Idle frame 0, Idle mid, Idle loop-end,
   AirIdle frame 0, AirIdle mid, and AirIdle loop-end.
4. The comparer samples each diagnostic GLB at the runtime time and scores:
   `local`, `local-transpose`, `model`, `model-transpose`,
   `model-no-root`, `model-no-root-transpose`, `skin`, `skin-transpose`,
   `skin-no-root`, and `skin-no-root-transpose`.
5. At that stage, a mode would only have been promoted if the same transform
   convention gave low error for all sampled times across both animations.
   The retained final palette has since selected the production formula.

Historical status from the earlier pass was that no usable state existed. That
is superseded by the retained state and deterministic palette address above.

## Step C2 - Easier Q/T capture fallback

If the final 4x4 matrix palette is hard to identify in the debugger, capture
the composed runtime pose from `FUN_00231048` first. This is not a replacement
for final skinning matrices, but it directly verifies the quaternion and
translation composition question.

1. Set breakpoints at:
   - `0x00231048` function entry,
   - `0x00231220` function return path.
2. When `0x00231048` hits for a 29-bone pose, copy register `a0`. It should
   point to a pose struct where:
   - `[a0 + 0x00]` is bone count,
   - `[a0 + 0x2C]` is the output quaternion buffer,
   - `[a0 + 0x30]` is the output translation buffer.
3. Continue to the `0x00231220` breakpoint so the output buffers have been
   written.
4. Dump the pose using the copied entry `a0` value:

   ```powershell
   python tools/research/thps3-animation/thps3_pose_dump.py --pine `
     --pose-addr 0x00ABCDEF `
     --animation skater_m_Idle --time 0.533333 `
     --out TestOutput\thps3_runtime_matrices\idle_mid_pose.json
   ```

If the breakpoint path is too noisy or does not fire during gameplay, use a
savestate scan instead:

1. Reach gameplay with the skater visible and save a PCSX2 state.
2. Run:

   ```powershell
   python tools/research/thps3-animation/thps3_pose_scan.py `
     --top 20 `
     --animation skater_m_Idle --time 0.0 `
     --out TestOutput\thps3_runtime_matrices\pose_scan_candidates.json `
     --dump-best TestOutput\thps3_runtime_matrices\pose_scan_best.json
   ```

3. If the newest THPS3 savestate is not in the default PCSX2 sstates folder,
   pass its path as the first argument.

Current scanned savestate result:

```powershell
python tools/research/thps3-animation/thps3_pose_scan.py `
  "<path-to-savestate>\thp3_debug.p2s" `
  --top 20 `
  --animation skater_m_Idle --time 0.0 `
  --out TestOutput\thps3_runtime_matrices\pose_scan_candidates.json `
  --dump-best TestOutput\thps3_runtime_matrices\pose_scan_best.json
```

Best candidate was:

- pose struct: `0x00B404C0`
- quaternion buffer: `0x00B40660`
- translation buffer: `0x00B40930`
- 29 bones, unit quaternions, all negative `w`, plausible THPS3 skeleton-scale
  translations.

Compare the dumped Q/T pose against diagnostic GLBs:

```powershell
python tools/research/thps3-animation/thps3_pose_compare.py `
  --pose TestOutput\thps3_runtime_matrices\pose_scan_best.json `
  --sweep-root TestOutput\thps3_variant_sweep `
  --use-record-time `
  --top 10 `
  --out TestOutput\thps3_runtime_matrices\pose_compare_record_time.json
```

Historically, with `direct-raw-rawt` added as a diagnostic mode, the best Idle Q/T match was
`direct-raw-rawt` with effectively zero translation error and lower quaternion
error than `bind-raw-rawt`. That intermediate-only comparison is superseded by
the final matrix-palette result above.

## Historical Step C3 - Runtime Q-blob loader check

The Q/T fallback exposed a parser-level issue before final matrix capture:
the game does not interpolate directly from the serialized Q record order.
It loads a packed 20-byte Q blob, grouped by runtime Q track.

Use this command to reconstruct that blob from a savestate:

```powershell
python tools/research/thps3-animation/thps3_runtime_qblob_dump.py `
  --savestate "<path-to-savestate>\thp3_debug.p2s" `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --out TestOutput\thps3_runtime_matrices\debug_runtime_qblob.json
```

Observed for `thp3_debug.p2s` + `skater_m_Idle.ska`:

- Runtime Q blob base: `0x00D12C28`
- Packed records: `158`
- Runtime Q tracks: `28`
- The parser used at that time grouped by simple `prev / 24`, which was wrong
  for rotations. `SkaThps3Parser` now implements the recovered runtime grouping.
- Translation grouping remains consistent with runtime buffers.

Direct parser compare command:

```powershell
python tools/research/thps3-animation/thps3_ska_runtime_compare.py `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --pose TestOutput\thps3_runtime_matrices\debug_output_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_a_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_b_pose.json `
  --out TestOutput\thps3_runtime_matrices\debug_ska_runtime_compare.json
```

Historical interpretation and resolution:

- `xyzw` is correct; `wxyz` is clearly worse.
- Raw/conjugated quaternion transforms alone did not fix that intermediate
  mismatch.
- It was caused by Q-track loader linearization, not field order or translation
  anchoring. Reversing that loader produced the grouping now shipped by
  `SkaThps3Parser`; the later final-palette capture then established the
  conjugated-local runtime composition independently.

Additional standing-idle states `1.p2s`, `2.p2s`, `3.p2s`, and `4.p2s` were
scanned from a user-supplied capture directory.

All four resolve to the same best pose addresses as `thp3_debug.p2s`
(`pose=0x00B404C0`, `quat=0x00B40660`, `trans=0x00B40930`) and are identical
to each other. Their repeated record value is `0.616666`, not `0.483332`, and
their root/hips pose differs from the earlier dump. Treat them as scanner
stability evidence and separate standing-idle pose evidence, not as direct
validation for `skater_m_Idle.ska`.

## Step E - Acceptance for a new corroborating capture

- Compare only the 12 semantic affine floats per padded RwMatrix; preserve pad
  words separately and never score them as transforms.
- Strip only the export-only non-joint `skeleton_root`, then require
  `IBM * sourceGlobal` to stay below RMSE `0.0005` and max error `0.003` across
  all 29 bones.
- A new result must contradict that committed oracle before changing the
  production formula. Visual mode rankings and intermediate Q/T buffers alone
  are no longer sufficient grounds to reopen it.
- Do not apply the 12-byte pre-Q metadata unless runtime evidence establishes
  a semantic use.
