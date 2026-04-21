# THPS3 PS2 SKA - PCSX2/Ghidra Matrix Runbook

Goal: capture the THPS3 in-game per-bone animation matrices for
`skater_m_Idle.ska` and `skater_m_AirIdle.ska`, then compare those matrices
against exporter output. Use this only after the contact-sheet sweep cannot
select a safe production pose mode.

## Inputs

- PCSX2 build: `C:/Users/mmc99/Downloads/pcsx2-v2.3.218-windows-x64-Qt/pcsx2-qt.exe`
- Game: Tony Hawk's Pro Skater 3, PS2 final.
- Model: `Sample/Builds/Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)/Extracted/SKATE3/pre/cas_male/models/skater_m/skater_m.skn`
- Animations: `C:/tmp/skater_m_Idle.ska`, `C:/tmp/skater_m_AirIdle.ska`
- Export sweep: `python tools/diagnostics/thps3_variant_sweep.py --out C:/tmp/thps3_variant_sweep`

Capture these sample times:

| Animation | frame 0 | mid-animation | loop-end |
| --- | ---: | ---: | ---: |
| `skater_m_Idle` | `0.000000s` | `0.533333s` | `1.066667s` |
| `skater_m_AirIdle` | `0.000000s` | `0.666667s` | `1.333333s` |

## Step A - Confirm the visual ambiguity

1. Run the diagnostic sweep and inspect:
   - `C:/tmp/thps3_variant_sweep/contact_sheets/skater_m_Idle_az0.png`
   - `C:/tmp/thps3_variant_sweep/contact_sheets/skater_m_Idle_az90.png`
   - `C:/tmp/thps3_variant_sweep/contact_sheets/skater_m_AirIdle_az0.png`
   - `C:/tmp/thps3_variant_sweep/contact_sheets/skater_m_AirIdle_az90.png`
2. Do not promote a mode if it only looks better in one animation or one
   camera. The replacement default needs to survive both animations and both
   cameras.

## Step B - Find the interpolator output in Ghidra

1. Load the THPS3 PS2 ELF in Ghidra with the same EE processor setup used for
   previous PS2 analysis.
2. Locate the SKA interpolator path by searching for references to the loaded
   SKA header fields: bone count `29`, key counts near `136/71` for Idle and
   `122/69` for AirIdle, and the duration values listed above.
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

Focused helper command used in this pass:

```powershell
$ghidra = 'C:\tools\ghidra_12.0.2_PUBLIC\support\analyzeHeadless.bat'
$project = (Resolve-Path 'tools\ghidra\thps3_ps2\output').Path + '\callgraph_project'
$out = (Resolve-Path 'tools\ghidra\thps3_ps2\output').Path + '\thps3_ska_callgraph.txt'
$scripts = (Resolve-Path 'tools\ghidra\thps3_ps2').Path
New-Item -ItemType Directory -Force -Path $project | Out-Null
& $ghidra $project Thps3SkaCallGraph `
  -import 'C:\tmp\thps3_slus.bin' `
  -processor 'MIPS:LE:32:default' `
  -loader ElfLoader `
  -postScript DumpSkaCallGraph.java $out `
  -scriptPath $scripts `
  -analysisTimeoutPerFile 1200
```

Current static finding from `tools/ghidra/thps3_ps2/output/thps3_ska_parser.c`:

- `FUN_00230f68 @ 0x00230F68` loops over the bone count and advances Q by
  `0x18` bytes and T by `0x14` bytes.
- `FUN_0022ff38 @ 0x0022FF38` is the per-bone interpolation kernel called by
  `FUN_00230f68`. It writes one interpolated Q record and one interpolated T
  record for the current bone.
- `FUN_00231048 @ 0x00231048` composes quaternions in `x,y,z,w` order using
  Hamilton `qA * qB`, then adds translations as `tA + tB`.
- This proves field order, interpolation strides, and composition math, but not
  yet which caller supplies bind pose versus animated pose or where the final
  skinning matrix palette is stored. It does not by itself justify a production
  default change.

## Step C - Break on the matrix write in PCSX2

1. Enable PINE before launching PCSX2. In the UI, enable the PINE server, or
   set `EnablePINE = true` and `PINESlot = 28011` in
   `C:/Users/mmc99/Documents/PCSX2/inis/PCSX2.ini`, then restart PCSX2.
2. Launch THPS3 in PCSX2 and reach a state where the skater model is loaded.
3. Verify that the diagnostic helper can see the emulator:

   ```powershell
   python tools/diagnostics/thps3_matrix_dump.py --pine
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
python tools/diagnostics/thps3_matrix_dump.py --pine `
  --addr 0x00ABCDEF `
  --animation skater_m_Idle --time 0.533333 `
  --out C:\tmp\thps3_runtime_matrices\idle_mid.json
```

For a saved `.p2s` state, use the same address against `eeMemory.bin`:

```powershell
python tools/diagnostics/thps3_matrix_dump.py `
  --savestate "C:\Users\mmc99\Documents\PCSX2\sstates\SLUS-20013 (F77E2FB5).01.p2s" `
  --addr 0x00ABCDEF `
  --animation skater_m_Idle --time 0.533333 `
  --out C:\tmp\thps3_runtime_matrices\idle_mid.json
```

## Step D - Compare against exporter matrices

1. Export each diagnostic mode to GLB using `thps3_variant_sweep.py`.
2. Compare the runtime JSON against all sweep GLBs:

   ```powershell
   python tools/diagnostics/thps3_matrix_compare.py `
     --runtime C:\tmp\thps3_runtime_matrices\idle_mid.json `
     --sweep-root C:\tmp\thps3_variant_sweep `
     --top 20 `
     --out C:\tmp\thps3_runtime_matrices\idle_mid_compare.json
   ```

3. Repeat for all six samples: Idle frame 0, Idle mid, Idle loop-end,
   AirIdle frame 0, AirIdle mid, and AirIdle loop-end.
4. The comparer samples each diagnostic GLB at the runtime time and scores:
   `local`, `local-transpose`, `model`, `model-transpose`,
   `model-no-root`, `model-no-root-transpose`, `skin`, `skin-transpose`,
   `skin-no-root`, and `skin-no-root-transpose`.
5. Promote a mode only when the same transform convention gives low error for
   all sampled times across both animations.

Current local status from this pass: no PCSX2 process was running, PINE was
disabled in `PCSX2.ini`, and no `SLUS-20013` / `F77E2FB5` THPS3 savestate was
present under `C:/Users/mmc99/Documents/PCSX2/sstates`, so live capture was not
completed yet.

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
   python tools/diagnostics/thps3_pose_dump.py --pine `
     --pose-addr 0x00ABCDEF `
     --animation skater_m_Idle --time 0.533333 `
     --out C:\tmp\thps3_runtime_matrices\idle_mid_pose.json
   ```

If the breakpoint path is too noisy or does not fire during gameplay, use a
savestate scan instead:

1. Reach gameplay with the skater visible and save a PCSX2 state.
2. Run:

   ```powershell
   python tools/diagnostics/thps3_pose_scan.py `
     --top 20 `
     --animation skater_m_Idle --time 0.0 `
     --out C:\tmp\thps3_runtime_matrices\pose_scan_candidates.json `
     --dump-best C:\tmp\thps3_runtime_matrices\pose_scan_best.json
   ```

3. If the newest THPS3 savestate is not in the default PCSX2 sstates folder,
   pass its path as the first argument.

Current scanned savestate result:

```powershell
python tools/diagnostics/thps3_pose_scan.py `
  "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --top 20 `
  --animation skater_m_Idle --time 0.0 `
  --out C:\tmp\thps3_runtime_matrices\pose_scan_candidates.json `
  --dump-best C:\tmp\thps3_runtime_matrices\pose_scan_best.json
```

Best candidate was:

- pose struct: `0x00B404C0`
- quaternion buffer: `0x00B40660`
- translation buffer: `0x00B40930`
- 29 bones, unit quaternions, all negative `w`, plausible THPS3 skeleton-scale
  translations.

Compare the dumped Q/T pose against diagnostic GLBs:

```powershell
python tools/diagnostics/thps3_pose_compare.py `
  --pose C:\tmp\thps3_runtime_matrices\pose_scan_best.json `
  --sweep-root C:\tmp\thps3_variant_sweep `
  --use-record-time `
  --top 10 `
  --out C:\tmp\thps3_runtime_matrices\pose_compare_record_time.json
```

With `direct-raw-rawt` added as a diagnostic mode, the best Idle Q/T match was
`direct-raw-rawt` with effectively zero translation error and lower quaternion
error than `bind-raw-rawt`. This is useful evidence for Q/T composition but is
not yet final matrix-palette evidence.

## Step C3 - Runtime Q-blob loader check

The Q/T fallback exposed a parser-level issue before final matrix capture:
the game does not interpolate directly from the serialized Q record order.
It loads a packed 20-byte Q blob, grouped by runtime Q track.

Use this command to reconstruct that blob from a savestate:

```powershell
python tools/diagnostics/thps3_runtime_qblob_dump.py `
  --savestate "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --ska C:\tmp\skater_m_Idle.ska `
  --out C:\tmp\thps3_runtime_matrices\debug_runtime_qblob.json
```

Observed for `thp3_debug.p2s` + `skater_m_Idle.ska`:

- Runtime Q blob base: `0x00D12C28`
- Packed records: `158`
- Runtime Q tracks: `28`
- The current parser's simple `prev / 24` grouping is wrong for rotations.
- Translation grouping remains consistent with runtime buffers.

Direct parser compare command:

```powershell
python tools/diagnostics/thps3_ska_runtime_compare.py `
  --ska C:\tmp\skater_m_Idle.ska `
  --pose C:\tmp\thps3_runtime_matrices\debug_output_pose.json `
  --pose C:\tmp\thps3_runtime_matrices\debug_source_a_pose.json `
  --pose C:\tmp\thps3_runtime_matrices\debug_source_b_pose.json `
  --out C:\tmp\thps3_runtime_matrices\debug_ska_runtime_compare.json
```

Interpretation:

- `xyzw` is correct; `wxyz` is clearly worse.
- Raw/conjugated quaternion transforms do not fix the mismatch.
- The remaining rotation error is caused by Q-track loader linearization, not
  field order or translation anchoring.
- Before doing more matrix-palette work, reverse the loader that converts
  serialized 24-byte Q records into the packed runtime Q blob.

Additional standing-idle states were scanned:

- `C:\Users\mmc99\Desktop\thps3\1.p2s`
- `C:\Users\mmc99\Desktop\thps3\2.p2s`
- `C:\Users\mmc99\Desktop\thps3\3.p2s`
- `C:\Users\mmc99\Desktop\thps3\4.p2s`

All four resolve to the same best pose addresses as `thp3_debug.p2s`
(`pose=0x00B404C0`, `quat=0x00B40660`, `trans=0x00B40930`) and are identical
to each other. Their repeated record value is `0.616666`, not `0.483332`, and
their root/hips pose differs from the earlier dump. Treat them as scanner
stability evidence and separate standing-idle pose evidence, not as direct
validation for `skater_m_Idle.ska`.

## Step E - Escalation rules

- First fix parser-level Q-track linearization so exported Q tracks match the
  runtime Q blob. Do not promote an exporter mode while parser grouping is
  known wrong.
- If one existing exporter mode then matches the PCSX2 matrices, make that mode
  the `Thps3SkaAnimationMode.Default` and keep the others as diagnostics.
- `wxyz` is already disfavored by runtime buffer comparison; only revisit field
  order if new matrix evidence contradicts the Q-blob findings.
- Do not apply the 12-byte pre-Q metadata in production unless loader or matrix
  evidence shows how the game uses it.
