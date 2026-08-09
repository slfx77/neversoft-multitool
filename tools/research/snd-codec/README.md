# THUG2 SND codec research

This is an open codec-recovery workflow for THUG2 PC `.snd` payloads. The main
application deliberately does not claim these files as decoded yet, so the
capture, probe generation, solver, and fit-analysis tools remain useful.

The Python analysis tools use the standard library. Live capture additionally
requires Frida and a disposable Windows VM as described in the runbook.

See `docs/backlog/snd-capture-runbook.md` for prerequisites, capture procedure,
known constraints, and current hypotheses. Generated captures and solver
outputs belong under `TestOutput/`, not in this directory.

Run the fixture-free solver check after changing the recovery logic:

```powershell
python tools/research/snd-codec/snd_solve.py --self-test
```
