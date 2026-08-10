# Repository tools

This directory contains durable tooling that remains useful outside the main
C# application. It is intentionally curated; it is not a permanent home for
every investigation script.

## Lifecycle policy

- Keep a tool when it provides a reusable capability, a reproducible validator,
  an unfinished research workflow, or a standalone workflow that does not
  belong in the application.
- Remove a research probe once its result is implemented and covered by the C#
  application and tests. Preserve the durable conclusion in code, tests, or
  documentation instead of preserving the probe indefinitely.
- Put generated reports, captures, binaries, caches, and temporary experiments
  under `TestOutput/` (or another ignored working directory), not beside tool
  source.
- Keep external binaries out of Git. A vendor directory should contain a
  tracked acquisition/version note and ignore the downloaded payload.

## Layout

| Directory | Purpose |
|---|---|
| `common/` | Shared helpers used by more than one tool family. |
| `corpus/` | Reproducible sample-corpus generation. |
| `maintenance/` | Repository and code-quality maintenance commands. |
| `qbkey_pipeline/` | Reusable C/OpenCL QBKey recovery pipeline. |
| `research/` | Open investigations whose results are not yet in the app. |
| `reverse-engineering/` | Generic disassemblers and Ghidra helpers. |
| `safedisc/` | Standalone SafeDisc emulation and THUG2 recovery suite. |
| `validation/` | Reproducible format, renderer, and GUI validators. |
| `vendor/` | Acquisition notes for optional third-party tools. |

Each substantial tool family should carry its own README with prerequisites,
inputs, outputs, and a minimal verification command.

## Retired tooling

The 2026-08 audit removed the old `PsxAnalyzer`, `DdmAnalyzer`,
`WorldzoneOracleCensus`, `BlendModeDiag`, and `XbxPassSurvey` projects; their
accepted behavior now lives in the application and focused tests. It also
removed one-off format probes and harvesters, build-specific Ghidra projects,
and generated QBKey/capture/build output. The THUG2 PC SND capture/probe suite
was retired after the decrypted executable supplied the exact codec and it was
implemented in C#. Tracked source remains recoverable from Git history; future
regressions should start from the current C# behavior and retain a new tool only
when it satisfies the lifecycle policy above.
