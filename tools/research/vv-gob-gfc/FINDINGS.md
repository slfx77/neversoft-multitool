# Vicarious Visions GOB / GFC container (DS) — open investigation

Status: **research in progress.** The container framing is characterized; the
exact per-entry codec set, the record field semantics, and the tail name table
need the DS ARM9/overlay loader disassembled — which the NitroFS reader already
exposes (extract a cart and the loader is under `_system/arm9.bin` +
`_system/overlay9_*.bin`). Throwaway probes: `TestOutput/nds-probe/gfc_probe.py`,
`gfc_records.py` (regenerable; the carts are in `Sample/Builds`).

The three Tony Hawk DS carts store nearly all game content in a
`main.gob`/`main.gfc` pair (`gob/mainUS.*` on Downhill Jam and Proving Ground),
reached now that the `.nds` Nitro filesystem is readable. `.gfc` is the index,
`.gob` the data blob. Everything below is **big-endian**.

## GFC index

- Header (4 BE u32): `{0x8008 magic, gobTotalSize, entryCount, uniqueCount}`.
  `gobTotalSize` equals the `.gob` length exactly. `entryCount > uniqueCount`
  because many logical entries **dedup** onto one shared `.gob` blob (Sk8land
  18,540 entries / 14,606 unique; DHJ 12,087 / 4,657; PG 11,016 / 5,665).
- A `entryCount` array of **16-byte BE records** follows the header:
  `{u32 size, u32 offset, u16 ?, u16 = 0x7FFF, u8 codec, u8 ?, u16 ?}`.
  `offset` is into the `.gob`; `size` is the stored (compressed) length. The
  `codec` byte selects compression per entry — `0x7A` = zlib, `0x30` = a second
  scheme (see below). Records repeat verbatim (record 10 == record 4 on
  Sk8land), which is the dedup.
- After the record array sits a large **tail** (Sk8land 249 KB, DHJ 104 KB,
  PG 112 KB) of high-entropy bytes — the presumptive **name/hash table** (its
  size tracks `uniqueCount`), format unresolved.

## GOB payloads — codec evolves across the three games

- **Sk8land (2005)**: entries are compressed. `codec 0x7A` blobs begin `78 9C`
  (zlib) but a plain `zlib.decompress` reports a trailing-check error, so the
  stored `size` is not the exact deflate length (padding, or size counts to the
  next record) — needs the loader to confirm how the length is derived.
  `codec 0x30` blobs are a different scheme (one begins `10 00 21 43`, a GBA-BIOS
  LZ77 header; another `50 00 00 00`, which is neither zlib nor LZ77) — so `0x30`
  is not a single codec, or a sub-tag distinguishes them.
- **Downhill Jam (2006) / Proving Ground (2007)**: entries are largely **raw,
  2048-byte aligned** (heads are zero-fill / plain data), a simpler layout than
  Sk8land's compressed one.

## Why it's blocked, and the lever

A correct listing needs (a) the exact `size`→codec→decompressed-length contract
and (b) the tail name table. Both are read by the GOB loader, which lives in the
ARM9 binary / overlays — now extractable via the NitroFS route
(`_system/arm9.bin`, `_system/overlay9_*.bin`). Ghidra-import ARM9 (ARMv5TE LE,
load address from the header's ARM9 RAM field at 0x28), find the reader that
consumes `main.gfc` (search for the `0x8008` magic or the string "gfc"/"gob"),
and read the record walk + decompressor directly.

## Next steps

1. Disassemble the ARM9 GOB loader → exact record semantics + codec dispatch +
   name-table format.
2. Implement `GobArchive` on the PAK+PAB companion pattern (`.gfc` index +
   `.gob` data via `companionPath` in `FileArchiveFileSystem`), an
   `ArchiveEntryDecoder` arm per codec, and `.gob` in the nested-open gates so it
   opens in place inside an extracted `.nds`. Names hex/type-tagged until the
   tail table is resolved (deterministic, HED style).
3. `[CorpusFact]` across all three carts: entry counts, chained-offset
   invariant, round-trip of a pinned entry.
