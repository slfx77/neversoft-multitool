# DS GOB/GFC container — session handoff

**Goal:** crack the Vicarious Visions `main.gob` / `main.gfc` container so the DS
Tony Hawk carts' assets extract, then wire it into the tool as `GobArchive` the way
PAK/PAB and the other archives are wired. This is a self-contained brief — start a
fresh session in this repo and work from here. Read `FINDINGS.md` (same folder) for
the full evidence; this doc is the action plan.

## Where things stand

- **DONE:** the DS `.nds` Nitro filesystem reads (`Core/Formats/Nds/NdsRomArchive.cs`).
  Extracting a cart yields the NitroFS tree **and** `_system/` (header, `arm9.bin`,
  `arm7.bin`, banner, `overlay9_*`) — the ARM9/overlay code is exposed *specifically*
  because the GOB loader lives there and you need it.
- **CHARACTERIZED (not yet decodable):** the GFC index header + 16-byte record shape,
  and that the GOB codec set evolves across the three carts. See FINDINGS.md §GFC/§GOB.
- **BLOCKED ON:** the exact `size → codec → decompressed-length` contract and the tail
  name/hash table. Both are read by the ARM9 GOB loader — so the lever is to
  **disassemble that loader**. Everything else is downstream of that.

## The three carts (staged as bare `.nds`, like the GBA/N64 carts)

```
Sample/Builds/Tony Hawk's American Sk8land (2005-11-15, DS - Final)/Tony Hawk's American Sk8land (USA).nds
Sample/Builds/Tony Hawk's Downhill Jam (2006-10-24, DS - Final)/Tony Hawk's Downhill Jam (USA).nds
Sample/Builds/Tony Hawk's Proving Ground (2007-10-15, DS - Final)/Tony Hawk's Proving Ground (USA).nds
```

GFC census (from FINDINGS): entries / unique blobs — Sk8land 18,540 / 14,606;
DHJ 12,087 / 4,657; PG 11,016 / 5,665. `entryCount > uniqueCount` because logical
entries dedup onto shared `.gob` blobs. Sk8land is the compressed (hard) case;
DHJ/PG are largely raw 2048-aligned (easier — consider starting there to prove the
record walk, then tackle Sk8land's codecs).

## Step 1 — get the loader + the container out of a cart

```bash
# Extract a cart's NitroFS + _system (routes through NdsRomArchive):
dotnet run --project src/NeversoftMultitool/NeversoftMultitool.csproj -f net10.0 -- \
  archive "Sample/Builds/Tony Hawk's American Sk8land (2005-11-15, DS - Final)/Tony Hawk's American Sk8land (USA).nds" \
  -o TestOutput/sk8land-nds
```

You get `main.gob` + `main.gfc` (the container) and `_system/arm9.bin` +
`_system/overlay9_*.bin` (the loader). On DHJ/PG the container is named
`gob/mainUS.gob` / `gob/mainUS.gfc`.

## Step 2 — disassemble the ARM9 GOB loader (the lever)

ARM9 is **ARMv5TE, little-endian**. Its RAM load address is in the `.nds` header at
**+0x28** (ARM9 RAM address); size at +0x2C, entry at +0x24. Two routes:

- **Ghidra (recommended):** import `_system/arm9.bin` as raw ARM:LE:32 v5, set the
  image base to the +0x28 value, auto-analyze. Overlays load at their own addresses
  (overlay table at header +0x50); the GOB reader may be in an overlay, so import
  those too if arm9.bin doesn't contain it. The repo's headless string script
  (`tools/reverse-engineering/ghidra/ExtractStrings.java`) works on ARM unchanged.
- **Quick triage first:** `tools/reverse-engineering/gba/gba_disasm.py` is Capstone
  ARM/THUMB and disassembles ARMv5 fine — use `--func <addr>` to spot-check a
  candidate once you have an address (change the base to the +0x28 value).

**Find the reader:** search arm9.bin/overlays for the GFC magic `0x8008` (as an
immediate or a compared word), or the strings `"gfc"`/`"gob"`/`"main"`. The function
that consumes `main.gfc` walks the 16-byte records and dispatches on the `codec`
byte. Read out, exactly:

1. How the **stored length** relates to the record `size` (Sk8land's zlib blobs
   `78 9C…` fail a plain `zlib.decompress` trailing-check — is `size` padded, or does
   it count to the next record's offset? The loader settles it).
2. The **codec dispatch**: `0x7A` = zlib; `0x30` is *not* one codec (one blob starts
   `10 00 21 43` = GBA-BIOS LZ77, another `50 00 00 00`) — find the sub-tag or the
   second dispatch that separates them.
3. The **tail table** after the record array (Sk8land ~249 KB, DHJ ~104 KB, PG
   ~112 KB; its size tracks `uniqueCount`) — is it a name table, a hash→index map,
   or per-blob metadata? This is what turns hex-named entries into real names.

## Step 3 — implement `GobArchive`

Mirror the **PAK + PAB companion pattern** exactly (it's the closest precedent — an
index file + a separate data file):

- `Core/Formats/Archives/GobArchive.cs`: parse the `.gfc` index; each entry resolves
  to a byte range in the companion `.gob` (via `companionPath` in
  `FileArchiveFileSystem`, like PAK→PAB). Dedup: multiple entries can point at one
  blob — surface them as one file or per-entry, your call, but keep it deterministic.
- `ArchiveEntryDecoder`: one arm per codec (zlib via the existing path; LZ77 via the
  shipped `GbaBiosLz77` if the `0x30`/`10 00` blobs really are BIOS LZ77; raw
  pass-through for DHJ/PG).
- Registration — the container checklist (10 touchpoints, same as NDS got):
  `ArchiveTypeDetector` (extensions/Classify/DetectAssetType), `ArchiveAssetType.Gob`,
  `ArchiveFileSystem.TryOpen`, `ArchiveEntryDecoder`, `RecursiveUnpacker` (stem-wrap +
  arm), `ArchiveCommand`, `ArchiveExtractorTab` (both switches),
  `ArchiveAssetBackend.LegacyEnumerableTypes`, per-tab pickers.
- Add `.gob` to the **nested-open gates** so it opens in place inside an extracted
  `.nds` tree: `ArchiveFileSystemBase.TryOpenNested` **and** the duplicate list at
  `TextureTabFileScanner.cs` (there are two — grep for the existing `.pre`/`.pak`
  gate list). The `.gfc`/`.gob` pair uses the companion mechanism, so gate on `.gob`
  (data) with `.gfc` as its companion index, mirroring PAK/PAB.
- Names: hex/type-tagged (`gob_<index>` or by codec) until the tail table is
  resolved, then resolve names from it — the HED-dictionary house style.

## Step 4 — tests

- Synthetic `[Fact]`: an in-memory `.gfc`/`.gob` pair (header + a couple records +
  one zlib + one raw blob) — round-trip + rejection of a malformed index.
- `[CorpusFact]` across all three carts: entry/unique counts (pin the numbers above),
  the chained-offset invariant (`offset[i+1] == align(offset[i] + size[i])` — verify
  the exact alignment from the loader), and a round-trip of one pinned entry SHA.
- Corpus sweeps are **opt-in** here — new unbounded-enumeration tests must use
  `[CorpusFact]`/`[CorpusTheory]` and run with `--explicit on`.

## Gotchas (house rules)

- **Exact-file staging only.** Parallel sessions run on this branch — `git add`
  *specific paths* you changed, never `git add -A`/`.`. Check `git status` first;
  files you didn't touch belong to another session.
- Throwaway probes go in `TestOutput/nds-probe/` (gitignored), never `tools/`. The
  existing probes there (`gfc_probe.py`, `gfc_records.py`, `nds_walk.py`) regenerate
  the census — start from them.
- Build tests via the exe, not `dotnet test`:
  `tests/NeversoftMultitool.Tests/bin/Debug/net10.0/NeversoftMultitool.Tests.exe`.
- **Verify against a tree extracted by CORRECT code**, not one your in-progress parser
  produced — a past lesson: never validate against output a buggy reader generated.
- Don't trust a first-glance framing: the earlier "GOB = clean zlib chain" pre-probe
  over-promised; the real records dedup and mix codecs. Confirm every field against
  the loader before pinning.

## Key files

- Reader: `Core/Formats/Nds/NdsRomArchive.cs` (extracts NitroFS + `_system/`).
- Research: `tools/research/vv-gob-gfc/FINDINGS.md` (evidence), probes in
  `TestOutput/nds-probe/`.
- Precedent to copy: the PAK/PAB companion path, and `Core/Formats/Gba/GbaBiosLz77.cs`
  (strict BIOS-LZ77 codec, if the `0x30`/`10 00` blobs need it).
- Memory: `handheld_wii_corpus_expansion.md` (the DS entry) has the corpus context.
