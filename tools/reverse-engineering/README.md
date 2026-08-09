# Reverse-engineering helpers

Only input-agnostic helpers live here: architecture disassemblers, PSY-Q symbol
utilities, and parameterized Ghidra scripts. Build-specific Ghidra projects,
phase scripts, decompiler output, and investigation logs are intentionally not
retained after their conclusions enter the C# implementation and format docs.

See `ghidra/README.md` for the headless-script workflow. Generated Ghidra
projects and output should live outside the repository or under `TestOutput/`.

The N64, PowerPC, and PSX disassemblers require Python 3 and Capstone:

```powershell
python -m pip install capstone
python tools/reverse-engineering/n64/n64_disasm.py --help
```

The PS2 VU disassemblers use only the Python standard library. PSY-Q signature
detection additionally needs `GHIDRA_HOME` or an explicit `--sigs` directory.
