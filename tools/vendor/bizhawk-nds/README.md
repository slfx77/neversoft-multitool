# BizHawk 2.9.1 — melonDS core (vendored, untracked)

Used for **dynamic analysis of the Tony Hawk Nintendo DS carts** (memory
watchpoints / execute breakpoints via the Lua API, to read codecs and loaders out
of the running game rather than inferring them from bytes).

This is a **second, separate install**. `tools/vendor/bizhawk/` is pinned at
**2.6.3**, which ships mGBA and has no NDS core at all — no `melonDS.wbx.zst`, no
`gamedb_nds.txt` — so it cannot load a `.nds`. Upgrading it in place would move
the GBA work onto an emulator it was not validated against, so the DS build lives
beside it instead.

The install itself is **not committed** (`.gitignore` tracks only this README, the
same as `tools/vendor/gltf-validator/` and `tools/vendor/bizhawk/`).

## Provisioning

Download BizHawk 2.9.1 (win-x64) and extract it into this folder so that
`tools/vendor/bizhawk-nds/EmuHawk.exe` and `tools/vendor/bizhawk-nds/dll/melonDS.wbx.zst`
exist.

Release: https://github.com/TASEmulators/BizHawk/releases/tag/2.9.1

```
curl -L -o bizhawk.zip \
  https://github.com/TASEmulators/BizHawk/releases/download/2.9.1/BizHawk-2.9.1-win-x64.zip
```

**DS firmware is not required** for these carts — melonDS boots them directly.

## Usage

```
tools/vendor/bizhawk-nds/EmuHawk.exe "<rom>.nds" --lua="<script>.lua"
```

The Lua surface is the same one the GBA work used, and the same caveats apply:

- `event.onmemoryexecute(fn, addr)` — execute breakpoint.
- `event.onmemorywrite(fn, addr)` — write watchpoint. **Do not watch a high-churn
  address every frame** — it hangs the emulator. Watch a narrow range and
  unregister the callback after the first burst.
- BizHawk's Lua is **5.1** and has **no bitwise operators** — use arithmetic
  (`math.floor(x / 2^n) % 2^k`) or the `bit` library where available.
- Write captures to `TestOutput/`, never beside this install.

### DS specifics

- Two CPUs. `memory.usememorydomain("ARM9 System Bus")` /
  `"ARM7 System Bus"` selects which one addresses resolve against; the default
  domain is not necessarily the one a breakpoint should target.
- The audio codec work wants **ARM7**, which owns the SPU; the asset loaders are
  on **ARM9**.
