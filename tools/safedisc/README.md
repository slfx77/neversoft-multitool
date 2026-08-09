# SafeDisc research tools

This directory preserves the standalone SafeDisc loader emulator and the exact
protected-input THUG2 decryption workflow. These scripts are research tools;
they are intentionally separate from the main C# application.

The superseded loader-debugging chronology is retained in [HISTORY.md](HISTORY.md)
so the emulator source itself leads with current design, usage, and status.

Install the Python dependencies from the repository root:

```powershell
python -m pip install -r tools/safedisc/requirements.txt
```

The primary entry point is `thug2_safedisc_decrypt.py`. It verifies the known
protected executable and matching CD1 image, runs `safedisc_emu.py` to the game
entry point, authenticates the retained runtime records, and writes a standalone
PE. `thug2_cd3_recover.py` is validation-only and does not supply bytes to the
protected-input decryptor.

Run the fixture-free checks after changing the emulator or finalizer:

```powershell
python tools/safedisc/safedisc_emu_selftest.py
python tools/safedisc/thug2_cd3_recover_selftest.py
```

Use `--help` on an individual script for its command-line contract. The exact
recovery chain and input attestations are documented in
`docs/backlog/safedisc-emulation-handoff.md`.
