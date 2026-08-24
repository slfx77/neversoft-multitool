# Generic Ghidra helpers

This directory contains reusable, parameterized Ghidra scripts rather than saved Ghidra projects or build-specific analysis output.

- `DecompileByStringRef.java`, `DecompileFunctionsByAddress.java`, and `DecompileFunctionRange.java` export targeted decompilation — by string reference, by address, or by whole address span when a subsystem is contiguous and its entry points are not yet known.
- `DumpFunctionCallEdgesByAddress.java`, `DumpFunctionPointerTable.java`, `DumpInstructionsByAddress.java`, `DumpMemoryRange.java`, and `DumpXrefsByAddress.java` export small, address-driven analysis slices.
- `SearchDwordValue.java` searches loaded memory for a caller-supplied value.
- `ApplyPsyqSymbols.java`, `parse_psyq_sym.py`, and `demangle_psyq.py` support Psy-Q symbol recovery.
- `ExtractStrings.java` exports QBKey-aware strings from the loaded program.

Install or invoke the Java files through Ghidra's Script Manager; their file
headers document interactive and headless arguments. The Python utilities expose
their input/output options through `--help`.
