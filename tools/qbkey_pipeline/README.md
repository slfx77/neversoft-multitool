# QBKey pipeline

This standalone C utility collects Neversoft hashes and candidate names, matches
known names, runs CPU or GPU brute-force searches, and prepares data for the
application's Hash Reviewer.

Build the basic Windows executable from the repository root. Keep the binary
and all generated datasets together under `TestOutput/`:

```powershell
New-Item -ItemType Directory -Force TestOutput/qbkey_pipeline | Out-Null
clang -O3 -D_CRT_SECURE_NO_WARNINGS -o TestOutput/qbkey_pipeline/qbkey_pipeline.exe tools/qbkey_pipeline/qbkey_pipeline.c
```

Add `-fopenmp` for parallel CPU searches. GPU searches additionally require
`-DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120`, the OpenCL include/library paths,
and `-lOpenCL`.

Run `TestOutput/qbkey_pipeline/qbkey_pipeline.exe --help` for complete command options. The normal
pipeline is:

1. `collect-hashes <builds-path>`
2. `collect-names <builds-path>`
3. `match <builds-path>`
4. `brute` or `brute-gpu`
5. `filter` or `prefilter`
6. `candidates`

The commands write generated datasets next to the executable. Building it as
shown keeps those datasets in the ignored `TestOutput/qbkey_pipeline/` working
directory. This includes `hash_targets.json`, `all_hashes.txt`, `all_names.txt`,
`matched_hashes.json`, unmatched-hash lists, brute-force results, and
`review_candidates.json`. Do not commit generated datasets or the executable.

Run `python tools/qbkey_pipeline/tune_gpu.py` to benchmark GPU work-group
settings. It uses `TestOutput/qbkey_pipeline/qbkey_pipeline.exe` by default;
pass `--exe <path>` when the executable is elsewhere.

`collect_script_names.py` can supplement the name corpus from decompiled QB/TRG
files. It defaults to `Sample/Builds` and writes its generated output under
`TestOutput/qbkey_pipeline/`; pass `--builds` and `--output-dir` to override
those locations.
