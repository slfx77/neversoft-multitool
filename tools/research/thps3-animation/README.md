# THPS3 animation research

These scripts compare exported poses with PCSX2 runtime matrices and savestate
data. The final convention question is closed by the retained 29-bone palette;
the scripts remain to reproduce that oracle or inspect a genuinely new capture.
Earlier exploratory SKA parsers were removed after their findings entered the
C# parser and writer.

Start with `docs/runbooks/thps3-ska-pcsx2.md`. The broader correctness record is
`docs/thps3-ska-animation-correctness-handoff.md`. Store dumps, screenshots, and
comparison output under `TestOutput/`.

The analysis scripts require Python 3. `thps3_variant_sweep.py` is historical
and must be modernized before use: it still references the removed
`--thps3-mode` CLI, an obsolete `/Extracted` SKN path, and does not contain the
eventual `direct-conjugated-rawt` winner. PCSX2 is needed only for a new runtime
capture. A minimal source-tree check is:

```powershell
python -m pip install Pillow
python tools/research/thps3-animation/thps3_matrix_compare.py --help
python tools/research/thps3-animation/thps3_matrix_dump.py --help
```
