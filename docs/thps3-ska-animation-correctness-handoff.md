# THPS3 SKA Animation Correctness Handoff

Date: 2026-04-21; resolved 2026-08-11

## Current Status

- `SkaThps3Parser.ParseThps3` (formerly `SkaFile.ParseThps3`, split out in the 2026-07-14 de-partialization) now reproduces the game-loaded Q-track linearization:
  root rotation is implicit/identity, serialized record 28 is treated as the
  root/end marker for the 29-bone skater rig, and records are scheduled into
  28 non-root Q tracks by serialized time order.
- Runtime Q/T compare after that parser fix is effectively exact for the
  captured Idle savestate: source A/B Q/T are near zero error, output compare
  is `q_rmse=5.11786e-08`, `t_rmse=2.071e-07`.
- `xyzw` + raw quaternion decode is confirmed for the runtime intermediate
  Q/T buffers; `wxyz` and conjugated variants remain rejected.
- The final palette is now captured. For `skater_m_Idle` at
  `0.799999475479126s`, pose `0x00B404C0` feeds the 29 padded RwMatrix records
  at `0x00B415F0`. The unique source-space formula is local
  `TR(conjugate(runtimeQ), rawT)`, parent-chain composition, then
  `IBM * global` in System.Numerics row convention.
- Production uses that formula through `SkaCompositionMode.Thps3Runtime`.
  Z-up→Y-up is isolated on the non-joint `skeleton_root`, outside the source
  bone chain. Against all 348 semantic RwMatrix floats, a fresh GLB scores
  RMSE `0.000377701`, max `0.00223009`; the next row-oriented convention is
  RMSE `4.97997`.
- Generated diagnostics for this repo should be written under `TestOutput\...`.
  THPS3 SKA input fixtures live at `tests\TestData\Thps3\Ska\skater_m_*.ska`
  (gitignored — extracted from disc locally).

## Implemented

- THPS3 export composition is isolated in `SkaAnimationWriter`; the shared
  `SkaPoseEvaluator` remains the generic SKE-side evaluator.
- THPS3 SKA Q records are parsed using the runtime Q-blob grouping rule instead
  of the serialized `prev / 24` chains. T records still use `prev / 20`.
- RW DFF imports select `Thps3Runtime`; other SKA/SKE routes retain their
  existing raw/additive policies. Translation is authored raw T, not the old
  anchored `bindT + (skaT - firstSkaT)` diagnostic hypothesis.
- The retained skater SKN has exact HAnim IDs and Index values `0..28`; its
  push/pop flags reconstruct the parent chain used by the golden.
- `tools/research/thps3-animation/thps3_variant_sweep.py` is the historical GLB/GIF/contact-sheet
  driver. It references a removed diagnostic CLI and must be modernized before
  use; it is not a current acceptance path.
- `tools/research/thps3-animation/thps3_matrix_dump.py` dumps a 29-bone matrix palette from
  PCSX2 PINE or a `.p2s` savestate once the EE buffer address is known.
- `tools/research/thps3-animation/thps3_matrix_compare.py` scores runtime matrix dumps
  against compatible diagnostic GLBs, including local/model/skin and transpose
  conventions. The committed C# palette test is the current production gate.
- `tools/research/thps3-animation/thps3_pose_scan.py` scans PCSX2 savestates for candidate
  29-bone THPS3 runtime pose structs when debugger register capture is noisy.
- `tools/research/thps3-animation/thps3_pose_compare.py` scores scanned/dumped Q/T poses
  against diagnostic GLBs.
- `tools/research/thps3-animation/thps3_ska_runtime_compare.py` compares runtime Q/T pose
  buffers directly against SKA parser decode variants (`xyzw`/`wxyz`,
  raw/conjugated, first/last duplicate policy).
- `tools/research/thps3-animation/thps3_runtime_qblob_dump.py` reconstructs the game's
  loaded 20-byte Q-key blob from a savestate and maps each runtime record back
  to the serialized 24-byte SKA Q record.
- `Thps3SkaRuntimePaletteTests` commits a normalized 29-bone palette with full
  executable/savestate/SKN/SKA provenance, verifies the exported skin matrices,
  and pins 2,998/2,998 runtime-format files parsed in the 3,000-file loose-disc
  corpus. The obsolete silent `c:/tmp` first-key heuristic was removed.

## Final Runtime-Palette Result (2026-08-11)

The retained state is `SLUS-20013 (F77E2FB5).01.p2s`, SHA-256
`633B8BB6C80E34E212F693DAD09D29A4ADD4568859A2C11056861B38B897CD05`.
Static code at `0x00231230` follows pose `+0x24` to the final RwMatrix palette.
Each 0x40-byte RwMatrix is four `xyz + pad/flags` lanes; the words at
`+0x0C/+0x1C/+0x2C/+0x3C` are not affine elements and must be discarded.
`thps3_matrix_dump.py` now preserves those words separately while normalizing
the comparison matrix to fourth slots `0/0/0/1`.

The committed golden's 348 float payload hashes to
`9361EDCF29A801A929E723DD244C5A6FA8710DBF6E94F40FC79611760C98F99F`.
The source fixtures are `skater_m.skn` SHA-256
`DB56BFBC17E0772E7B3C1DD03D9C0CE5863A2723C714525B325F6533779F99B6`
and `skater_m_Idle.ska` SHA-256
`D0118026564FDDC46A335B618324B9984D82ECF25A859A253B0FE442FAEA4CC0`.

## Historical Visual Sweep (superseded by the matrix palette)

Command:

```powershell
python tools\research\thps3-animation\thps3_variant_sweep.py `
  --out TestOutput\thps3_qschedule_variant_sweep `
  --size 512 --fps 15 --columns 8 --thumb-size 192
```

`dotnet run` hit an unrelated local workload-manifest mismatch on the final
`direct-raw-rawt` mode, so that mode was finished with the built executable:

```powershell
src\NeversoftMultitool\bin\Debug\net10.0\NeversoftMultitool.exe ska ...
src\NeversoftMultitool\bin\Debug\net10.0\NeversoftMultitool.exe glb-gif ...
python tools\research\thps3-animation\thps3_variant_sweep.py `
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

At that historical stage no mode was promoted and `bind-raw` remained the default. The direct rotation modes
are rejected visually in the latest sheets despite matching the runtime Q/T
intermediate buffers, which indicates those buffers are not the final local
skinning transforms by themselves. Raw-translation modes remain diagnostic
controls until final matrix evidence proves otherwise.

## Historical Mode Notes (superseded)

- `bind-raw`: former production default and visual winner/control before the palette capture.
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

Savestate fixture (user-supplied and not committed): `thp3_debug.p2s`.
Set its path once for the commands below:

```powershell
$savestate = "<path-to-savestate>\thp3_debug.p2s"
```

Scan command:

```powershell
python tools\research\thps3-animation\thps3_pose_scan.py `
  $savestate `
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
python tools\research\thps3-animation\thps3_pose_dump.py `
  --savestate $savestate `
  --pose-addr 0x00B404C0 --slot output `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_output_pose.json

python tools\research\thps3-animation\thps3_pose_dump.py `
  --savestate $savestate `
  --pose-addr 0x00B404C0 --slot source-a `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_source_a_pose.json

python tools\research\thps3-animation\thps3_pose_dump.py `
  --savestate $savestate `
  --pose-addr 0x00B404C0 --slot source-b `
  --animation skater_m_Idle `
  --out TestOutput\thps3_runtime_matrices\debug_source_b_pose.json

python tools\research\thps3-animation\thps3_ska_runtime_compare.py `
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
python tools\research\thps3-animation\thps3_ska_runtime_compare.py `
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
python tools\research\thps3-animation\thps3_runtime_qblob_dump.py `
  --savestate $savestate `
  --ska tests\TestData\Thps3\Ska\skater_m_Idle.ska `
  --out TestOutput\thps3_runtime_matrices\debug_runtime_qblob.json
```

The game-loaded Q blob starts at `0x00D12C28`, contains `158` packed 20-byte
records, and splits into `28` runtime Q tracks. The earlier parser instead
treated the serialized Q section as `159` records and grouped it by simple
`prev / 24` chains into `29` tracks. That grouping was wrong and has since been
replaced by the runtime grouping in `SkaThps3Parser`.

Example runtime Q tracks from the game-loaded blob:

- runtime Q track 0: file records `0,29`
- runtime Q track 1: file records `1,30,66,79,85,101,114,125,137`
- runtime Q track 2: file records
  `2,31,60,67,74,76,80,83,86,92,107,115,122,126,129,135,138,144,152`

Interpretation: the serialized Q records are `prev + q/time`, but the THPS3
loader strips `prev` and linearizes Q keys into runtime bone tracks before
interpolation. Root rotation appears implicit/identity; the loaded blob has 28
animated Q tracks for bones 1-28, while translation has 29 tracks including
root. `SkaThps3Parser.ParseThps3` now implements this rule.

Historical blind matrix-palette scan (superseded):

- `tools/research/thps3-animation/thps3_matrix_palette_scan.py` was run against
  `thp3_debug.p2s` and the additional user-supplied `1.p2s` capture.
- No credible simple contiguous 29-matrix EE palette was found in those tested
  `mat4`, `mat3x4`, and `mat4x3` windows. The later deterministic call-chain
  trace followed `pose + 0x24` to `0x00B415F0`, whose 29 padded RwMatrix records
  form the committed final-palette oracle.

Additional standing-idle savestates: `1.p2s`, `2.p2s`, `3.p2s`, and `4.p2s`,
all supplied from the same external capture directory.

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

## Closure

The parser grouping and final transform convention are both resolved and
production-pinned. Do not reopen this item from the historical contact-sheet
ranking or intermediate-Q/T notes. A future report needs a new captured final
palette that contradicts the committed 29-bone oracle, not another visual mode
sweep or SKA field-order permutation.

Static Ghidra progress:

- `FUN_0022FF38` is the per-bone interpolation kernel.
- `FUN_00230F68` calls it for each bone, using Q stride `0x18` and T stride
  `0x14`.
- `FUN_00231048` confirms `x,y,z,w` Hamilton quaternion composition and
  additive translation, but no direct caller was identified in the focused
  call-graph dump.
- The retained final palette closes the live-capture requirement after the
  parser reproduced the Q-key runtime linearization shown by
  `debug_runtime_qblob.json`.
