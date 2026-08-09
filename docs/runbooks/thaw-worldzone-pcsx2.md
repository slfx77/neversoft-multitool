# THAW PS2 Worldzone — PCSX2 debugging runbook

Goal: capture the in-memory form of `003B1940.mdl` (or any 0x7EA7357B worldzone
level MDL) so we can identify the transformation the engine applies between
the raw PAK bytes and what `FUN_001D4248` (the CGeomNode relocator) actually
reads.

Tools:
- **PCSX2 build**: a recent Qt build with the EE debugger (set its path locally)
- **Source reference** (debugger APIs): `Sample/pcsx2/pcsx2/DebugTools/` + `Sample/pcsx2/pcsx2/PINE.{h,cpp}`

## Step A — Savestate diff (do this first)

This is the fastest path. No breakpoints needed; we simply snapshot EE RAM
while the level is loaded and search it for the MDL signature.

1. Launch `pcsx2-qt.exe` with THAW (2005-8-22, PS2, Final).
2. Boot into a worldzone — Beverly Hills (BH) is the corpus we've analysed
   most. Any zone with one of the 0x7EA7357B level MDLs will do. **Wait until
   the level is fully loaded and visible.**
3. Save a savestate: **F2** (or `System → Save State → Slot 1`). This writes
   `.p2s` next to the game or to `%USERPROFILE%\Documents\PCSX2\savestates\`.
4. Open the `.p2s` as a ZIP archive and extract its 32 MiB `eeMemory.bin`.
   In a binary editor, search that RAM image for a distinctive 32-64-byte
   sequence from the on-disk MDL. If it matches, compare forward from that RAM
   offset against the disk file; otherwise continue with the breakpoint trace
   in Step B to obtain the runtime buffer address.

Comparison outcomes:
- **Exact full-file match**: the MDL is loaded byte-for-byte into EE RAM. Then
  `FUN_001D4248` must be reading from a *different* buffer (a copy made
  somewhere in the pre-parse chain — probably via vtable+0x8C/memcpy path).
- **The matched region diverges after N bytes**: the engine rewrites part of the data
  in-place. Compare the EE head dump against disk head dump to see the
  transformation. Classic possibilities: (a) pointer relocation writes into
  the first 16 KB, (b) header decompression, (c) byte-swapping.
- **No matching bytes found**: the MDL is either not loaded yet, is in
  scratchpad / VU1 data memory (not in `eeMemory.bin`), or the engine has
  rewritten the `0x4B189680` preamble signatures too. Take a savestate at a
  different point and re-run.

## Step B — Breakpoint trace (if Step A is inconclusive)

1. In PCSX2, open the EE debugger: `Debug → Open CPU Debugger` (or Ctrl+D).
2. Right-click the breakpoint list → **Add Breakpoint**.
3. Set address to `0x001D4248` (the `FUN_001D4248` entry), CPU = EE, enabled.
4. Boot into the worldzone. The breakpoint will fire as the engine loads the
   MDL.
5. When it hits, look at the **`a0` register** (first argument to
   FUN_001D4248) — that's `param_1`, the pointer the relocator is about to
   rebase.
6. Open `Debug → Memory View` and jump to the `a0` address. **Copy the first
   128 bytes as hex.**
7. Compare the copied hex against the on-disk MDL head. If they differ, the
   transformation happens somewhere between disk-read and this point.

## Step C — Automate via PINE (last resort)

PCSX2 exposes a socket protocol (PINE) at port `28011` supporting MsgRead8/
16/32/64 over TCP. A Python client can poll arbitrary EE addresses while the
game is running. This is useful if we need to repeatedly sample during a
long load, but for one-shot captures the savestate approach in Step A is
simpler.

See `Sample/pcsx2/pcsx2/PINE.cpp` lines 144-650 for the wire format. Note that
PINE does **not** support setting breakpoints — only memory read/write — so
Step B still requires the GUI debugger.

## Known facts going in

- Recovered call chain (summarized in `docs/formats/thaw-worldzone-mdl.md`): PAK dispatcher →
  `FUN_0025D488` → `FUN_00255BD8` → `FUN_00258C18` → `FUN_00257FA8` →
  `FUN_00258CA0` → `FUN_001571D0` → `FUN_00197EB0` → `FUN_001D4248`.
- Static vtable at `0x004CAB10`; loader is case 3 in `FUN_00255578`.
- `FUN_001D4248` does a two-hop rebase:
  `file += *file; root = file + *file; FUN_001CFB58(root, file, …)`.
- For the on-disk `003B1940.mdl`: `file[0] = 0xBFDD1111` — clearly NOT an
  offset, so something transforms the data before the relocator sees it.
- The vtable+0x8C handler at `0x00258218` calls `FUN_00472FF4` (memcpy) and
  `FUN_001A3500` (constructor on a 0xB0-byte object). The stock load path
  passes `param_4 = 0`, which skips that memcpy — so whatever transform
  exists is not *that* memcpy. Worth confirming in the emulator.
