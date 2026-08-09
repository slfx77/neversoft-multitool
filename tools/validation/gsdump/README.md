# GS dump validation

These scripts compare the multitool's GS replay output with PCSX2 software-
renderer captures and maintain the native-reference regression gate.

Dependencies:

- Python 3, Pillow, and NumPy for image comparison and the synthetic self-test.
- PowerShell and a built `NeversoftMultitool` CLI for oracle generation.
- PCSX2 only when capturing or refreshing real reference frames. Set
  `PCSX2_EXE`, `PCSX2_INI`, and `PCSX2_SNAPS_DIR`, or use the matching script
  parameters.

Minimal verification from the repository root:

```powershell
python -m pip install Pillow numpy
python tools/validation/gsdump/native_gate_selftest.py --root TestOutput/native_gate_selftest
```

Run `build_gsoracle.ps1` to refresh committed oracle JSON from local captures.
Capture and comparison output belongs under `TestOutput/`.
