# THPS3 animation research

These scripts compare exported poses with PCSX2 runtime matrices and savestate
data. They remain because the final runtime-matrix convention question is still
open; earlier exploratory SKA parsers were removed after their findings entered
the C# parser and evaluator.

Start with `docs/runbooks/thps3-ska-pcsx2.md`. The broader correctness record is
`docs/thps3-ska-animation-correctness-handoff.md`. Store dumps, screenshots, and
comparison output under `TestOutput/`.

The analysis scripts require Python 3; `thps3_variant_sweep.py` additionally
uses Pillow and invokes the built multitool CLI. PCSX2 is needed only to make
new runtime captures. A minimal source-tree check is:

```powershell
python -m pip install Pillow
python tools/research/thps3-animation/thps3_matrix_compare.py --help
python tools/research/thps3-animation/thps3_variant_sweep.py --help
```
