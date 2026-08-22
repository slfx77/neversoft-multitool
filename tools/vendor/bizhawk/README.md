# BizHawk (vendored, untracked)

BizHawk 2.6.3 with the mGBA core — used for **dynamic analysis of the GBA Tony
Hawk ROMs** (memory watchpoints / execute breakpoints via its Lua API, to locate
loaders and renderers that can't be pinned statically).

The install itself is **not committed** (`.gitignore` tracks only this README, the
same as `tools/vendor/gltf-validator/`). Only `README.md` is under version control.

## Provisioning

Download BizHawk 2.6.3 (win-x64) and extract it into this folder so that
`tools/vendor/bizhawk/EmuHawk.exe` and `tools/vendor/bizhawk/dll/mgba.dll` exist.
Release: https://github.com/TASEmulators/BizHawk/releases/tag/2.6.3

## Usage

Run a Lua script against a ROM (loads the ROM, runs the script):

```
tools/vendor/bizhawk/EmuHawk.exe "<rom>.gba" --lua="<script>.lua"
```

Lua patterns that work in this build (see the throwaway probes under
`TestOutput/gba-probe/`, e.g. `capture_builder.lua`):

- `event.onmemoryexecute(fn, addr)` — execute breakpoint at a ROM address.
- `event.onmemorywrite(fn, addr)` — write watchpoint. **Do not watch a high-churn
  address (the framebuffer) every frame — it hangs the emulator.** Watch a narrow
  range and unregister the callback after the first burst of writes.
- `memory.read_bytes_as_array(addr, len, "System Bus")` — dump memory (EWRAM/IWRAM/
  VRAM/ROM through the bus). IO-register reads via the bus can be unreliable.

**Lua version gotcha:** this build runs **Lua 5.1**, which has NO bitwise operators —
`&` `|` `<<` `>>` `~` are Lua 5.3+ and are a *syntax error* here. Use the built-in
`bit` library: `bit.band` / `bit.bor` / `bit.lshift` / `bit.rshift` / `bit.bxor`.
(`~=` is fine — that's "not equal", not bitwise.)

GBA memory map: ROM `0x08000000+`, EWRAM `0x02000000`, IWRAM `0x03000000`,
palette `0x05000000`, VRAM `0x06000000`, OAM `0x07000000`, IO `0x04000000`.
