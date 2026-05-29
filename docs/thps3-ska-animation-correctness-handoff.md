# THPS3 SKA Animation Correctness Handoff

Date: 2026-04-21

## Current Status

- `SkaFile.ParseThps3` now reproduces the game-loaded Q-track linearization:
  root rotation is implicit/identity, serialized record 28 is treated as the
  root/end marker for the 29-bone skater rig, and records are scheduled into
  28 non-root Q tracks by serialized time order.
- Runtime Q/T compare after that parser fix is effectively exact for the
  captured Idle savestate: source A/B Q/T are near zero error, output compare
  is `q_rmse=5.11786e-08`, `t_rmse=2.071e-07`.
- `xyzw` + raw quaternion decode is confirmed for the runtime intermediate
  Q/T buffers; `wxyz` and conjugated variants remain rejected.
- The production/default exporter mode was not changed. The latest contact
  sheets visually eliminate `direct-raw` and `direct-raw-rawt` because their
  arms fold through/cross the torso. `bind-raw` remains the default.
- Generated diagnostics for this repo should be written under `TestOutput\...`.
  THPS3 SKA input fixtures live at `tests\TestData\Thps3\Ska\skater_m_*.ska`
  (gitignored — extracted from disc locally).

## Implemented

- THPS3 pose application is now isolated in `Thps3SkaPoseApplier`.
- THPS3 SKA Q records are parsed using the runtime Q-blob grouping rule instead
  of the serialized `prev / 24` chains. T records still use `prev / 20`.
- `ska --skn` accepts `--thps3-mode` with these diagnostic modes:
  `bind-raw`, `direct-raw`, `bind-conjugated`, `direct-conjugated`,
  `bind-raw-rawt`, `direct-raw-rawt`.
- Translation defaults to anchored additive:
  `bindT + (skaT - firstSkaT)`.
- HAnim ID/index ordering is reported for THPS3 `--skn` exports.
- `tools/diagnostics/thps3_variant_sweep.py` exports GLBs, renders GIFs, and
  builds labeled contact sheets from sampled GIF frames.
- `tools/diagnostics/thps3_matrix_dump.py` dumps a 29-bone matrix palette from
  PCSX2 PINE or a `.p2s` savestate once the EE buffer address is known.
- `tools/diagnostics/thps3_matrix_compare.py` scores runtime matrix dumps
  against every diagnostic GLB in the sweep, including local/model/skin and
  transpose conventions.
- `tools/diagnostics/thps3_pose_scan.py` scans PCSX2 savestates for candidate
  29-bone THPS3 runtime pose structs when debugger register capture is noisy.
- `tools/diagnostics/thps3_pose_compare.py` scores scanned/dumped Q/T poses
  against diagnostic GLBs.
- `tools/diagnostics/thps3_ska_runtime_compare.py` compares runtime Q/T pose
  buffers directly against SKA parser decode variants (`xyzw`/`wxyz`,
  raw/conjugated, first/last duplicate policy).
- `tools/diagnostics/thps3_runtime_qblob_dump.py` reconstructs the game's
  loaded 20-byte Q-key blob from a savestate and maps each runtime record back
  to the serialized 24-byte SKA Q record.
- Focused tests cover rotation modes, anchored/raw translation, HAnim mapping
  status, and local THPS3 fixture counts when assets are present.

## Latest Sweep Result

Command:

```powershell
python tools\diagnostics\thps3_variant_sweep.py `
  --out TestOutput\thps3_qschedule_variant_sweep `
  --size 512 --fps 15 --columns 8 --thumb-size 192
```

`dotnet run` hit an unrelated local workload-manifest mismatch on the final
`direct-raw-rawt` mode, so that mode was finished with the built executable:

```powershell
src\NeversoftMultitool\bin\Debug\net10.0\NeversoftMultitool.exe ska ...
src\NeversoftMultitool\bin\Debug\net10.0\NeversoftMultitool.exe glb-gif ...
python tools\diagnostics\thps3_variant_sweep.py `
  --out TestOutput\thps3_qschedule_variant_sweep --contact-only `
  --columns 8 --thumb-size 192
```

Outputs inspected:

- `TestOutput\thps3_qschedule_variant_sweep\contact_sheets\skater_m_Idle_az0.png`
- `TestOutput\thps3_qschedule_variant_sweep\contact_sheets\skater_m_Idle_az90.png`
- `TestOutput\thps3_qschedule_variant_sweep\contact_sheets\skater_m_AirIdle_az0.png`
- `TestOutput\thps3_qschedule_variant_sweep\contact_sheets\skater_m_AirIdle_az90.png`

HAnim diagnostic for the fixture reports `id=exact, index=exact`, so there is
no current evidence for a THPS3 bone-order remap.

No mode was promoted. `bind-raw` remains the default. The direct rotation modes
are rejected visually in the latest sheets despite matching the runtime Q/T
intermediate buffers, which indicates those buffers are not the final local
skinning transforms by themselves. Raw-translation modes remain diagnostic
controls until final matrix evidence proves otherwise.

## Mode Notes

- `bind-raw`: current production default and latest visual winner/control.
- `direct-raw`: parser-level Q/T match to runtime intermediate buffers, but
  contact sheets fold/cross the arms through the torso; do not promote.
- `bind-conjugated`: obvious arm/torso distortion in Idle contact sheets.
- `direct-conjugated`: better than `bind-conjugated` in some Idle samples, but
  still not consistently valid across AirIdle.
- `bind-raw-rawt`: raw-translation control. It should remain diagnostic-only
  unless matrix evidence says THPS3 wants raw SKA translations.
- `direct-raw-rawt`: raw translation plus runtime-intermediate Q convention.
  Rejected visually for the same arm/torso crossing as `direct-raw`; keep only
  as a diagnostic comparison mode.

## Runtime Pose Evidence

Savestate:

```text
C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s
```

Scan command:

```powershell
python tools\diagnostics\thps3_pose_scan.py `
  "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --top 20 `
  --animation skater_m_Idle --time 0.0 `
  --out TestOutput\thps3_runtime_matrices\pose_scan_candidates.json `
  --dump-best TestOutput\thps3_runtime_matrices\pose_scan_best.json
```

Top candidate:

- pose struct: `0x00B404C0`
- quaternion buffer: `0x00B40660`
- translation buffer: `0x00B40930`
- score: `7.8997`
- `q_unit=1.000`, `neg_w=1.000`, `trans=1.000`

The dumped records carry repeated time-like value `0.483332`, so the GLB
comparison used that inferred record time.

Best Q/T comparison before the Q-track parser fix was `direct-raw-rawt`
(`q_rmse=0.170994`, `t_rmse=0.000000207`). That result is superseded by the
parser-level Q-track fix below.

Additional parser-level checks:

```powershell
python tools\diagnostics\thps3_pose_dump.py `
  --savestate "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --pose-addr 0x00B404C0 --slot output `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_output_pose.json

python tools\diagnostics\thps3_pose_dump.py `
  --savestate "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --pose-addr 0x00B404C0 --slot source-a `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_source_a_pose.json

python tools\diagnostics\thps3_pose_dump.py `
  --savestate "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --pose-addr 0x00B404C0 --slot source-b `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_source_b_pose.json

python tools\diagnostics\thps3_ska_runtime_compare.py `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --pose TestOutput\thps3_runtime_matrices\debug_output_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_a_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_b_pose.json `
  --out TestOutput\thps3_runtime_matrices\debug_ska_runtime_compare.json
```

Results before the Q-track parser fix:

- `xyzw` was correct; `wxyz` was clearly wrong (`q_rmse` around `0.66-0.67`).
- Raw versus conjugated quaternions did not explain the remaining error.
- Translation was effectively exact against the SKA parser (`t_rmse=0` for
  source slots, `0.000000207` for output).
- The runtime pose struct reported:
  `key_table=0x00B40560`, output Q/T `0x00B40660/0x00B40930`,
  source A `0x00B40B90/0x00B40E60`, source B
  `0x00B410C0/0x00B41390`.

Results after the Q-track parser fix:

```powershell
python tools\diagnostics\thps3_ska_runtime_compare.py `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --pose TestOutput\thps3_runtime_matrices\debug_output_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_a_pose.json `
  --pose TestOutput\thps3_runtime_matrices\debug_source_b_pose.json `
  --out TestOutput\thps3_runtime_matrices\debug_ska_runtime_compare_after_qschedule.json `
  --top 12
```

- `source-a`, `xyzw/raw`: `q_rmse=5.71532e-17`, `t_rmse=0`.
- `source-b`, `xyzw/raw`: `q_rmse=6.37428e-17`, `t_rmse=0`.
- `output`, `xyzw/raw`: `q_rmse=5.11786e-08`, `t_rmse=2.071e-07`.
- Conjugated variants remain wrong (`q_rmse` around `0.252-0.254`).

Critical Q-track finding:

```powershell
python tools\diagnostics\thps3_runtime_qblob_dump.py `
  --savestate "C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\thp3_debug.p2s" `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --out TestOutput\thps3_runtime_matrices\debug_runtime_qblob.json
```

The game-loaded Q blob starts at `0x00D12C28`, contains `158` packed 20-byte
records, and splits into `28` runtime Q tracks. The current parser instead
treats the serialized Q section as `159` records and groups it by simple
`prev / 24` chains into `29` tracks. That grouping is wrong.

Example runtime Q tracks from the game-loaded blob:

- runtime Q track 0: file records `0,29`
- runtime Q track 1: file records `1,30,66,79,85,101,114,125,137`
- runtime Q track 2: file records
  `2,31,60,67,74,76,80,83,86,92,107,115,122,126,129,135,138,144,152`

Interpretation: the serialized Q records are `prev + q/time`, but the THPS3
loader strips `prev` and linearizes Q keys into runtime bone tracks before
interpolation. Root rotation appears implicit/identity; the loaded blob has 28
animated Q tracks for bones 1-28, while translation has 29 tracks including
root. `SkaFile.ParseThps3` now implements this rule.

Matrix-palette scan:

- `tools/diagnostics/thps3_matrix_palette_scan.py` was run against
  `thp3_debug.p2s` and `C:\Users\mmc99\Desktop\thps3\1.p2s`.
- No credible simple contiguous 29-matrix EE palette was found in the tested
  `mat4`, `mat3x4`, and `mat4x3` layouts. Best hits had only one anchor and
  RMSE around `2.77`, so the final palette is either elsewhere, transformed
  differently, or not stored as a simple contiguous float array in the tested
  windows.

Additional standing-idle savestates:

- `C:\Users\mmc99\Desktop\thps3\1.p2s`
- `C:\Users\mmc99\Desktop\thps3\2.p2s`
- `C:\Users\mmc99\Desktop\thps3\3.p2s`
- `C:\Users\mmc99\Desktop\thps3\4.p2s`

All four scan to the same best pose addresses:

- pose struct: `0x00B404C0`
- quaternion buffer: `0x00B40660`
- translation buffer: `0x00B40930`

The four dumped Q/T poses are byte/float identical to each other, with repeated
record value `0.616666`. They differ from the earlier `thp3_debug.p2s` dump,
which had repeated record value `0.483332`. The largest difference is bone 1
root/hips translation, which is expected for the stopped one-foot-on-board
stance. These states confirm that the savestate scanner is finding stable
runtime skater pose buffers, but they should not be used to validate
`skater_m_Idle.ska` directly because the visual pose appears to be a different
standing idle/state.

## Next Step

The parser-level blocker is resolved. The remaining correctness question is the
final transform convention after the runtime Q/T intermediate buffers are
combined with bind/model/skinning matrices. The next useful target is final
matrix capture, not more SKA field-order permutations.

Recommended next work:

- Capture or locate the final 29-bone matrix palette after the game applies the
  interpolated Q/T buffers.
- Compare that palette against `bind-raw`, `bind-raw-rawt`, and any required
  matrix-space variants. Direct modes can stay available but are visually
  deprioritized.
- Keep the production default at `bind-raw` unless final matrix evidence proves
  raw translation or another composition.

Static Ghidra progress:

- `FUN_0022FF38` is the per-bone interpolation kernel.
- `FUN_00230F68` calls it for each bone, using Q stride `0x18` and T stride
  `0x14`.
- `FUN_00231048` confirms `x,y,z,w` Hamilton quaternion composition and
  additive translation, but no direct caller was identified in the focused
  call-graph dump.
- Live PCSX2 matrix capture is the remaining high-value evidence now that the
  parser reproduces the Q-key runtime linearization shown by
  `debug_runtime_qblob.json`.
