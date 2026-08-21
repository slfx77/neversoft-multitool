# `tricks.bin` — the PS1-era trick table

`tricks.bin` is the bytecode trick table the PS1-era skating games ship beside
the skater animation bank. It matters here because it is the **only** place the
otherwise anonymous PSX animation slots are given human names: the format
records nothing but numbered slots, and the engine plays them by index from game
code. This file *is* that code's table.

Implemented by `Core/Formats/Animation/TricksFile.cs`; slot naming by
`TrickAnimationNames` + `TrickNameLocator`.

## Corpus

| build | file size | dialect | tricks | with anim ids | uniquely-owned slots |
|---|---|---|---|---|---|
| THPS1 (all) | — | — | ships no `tricks.bin` | | |
| THPS2 2000-3-29 proto | 13,362 | halfword | 121 | 103 | 38 |
| THPS2 PSX / DC / 2X | 33,168 / 33,192 | byte | 202 | 181 | 111 |
| THPS3 PS1 | 33,540 | byte | 230 | 202 | 125 |
| THPS4 PS1 | 33,408 | byte | 229 | 201 | 125 |

## Record grammar

A trick is a run of opcode records introduced by a name record (`0x0B`, opcode
plus a NUL-terminated string) and closed by `0x07`. Record `0x01` carries an
animation slot index.

Two dialects, detected from the file rather than the build:

- **Halfword** (2000-3-29 THPS2 prototype): opcode and operands are 16-bit, so
  records are 2-byte aligned and the name case ends with `& ~1`.
- **Byte** (every shipped retail build): the opcode narrowed to one byte while
  operands stayed 16-bit and became **unaligned**. Retail's operand reader is an
  explicit `lbu`/`lbu`/shift pair rather than an `lh` precisely because a record
  may now begin at an odd offset — which is the tell that the re-encoding is
  real and not a mis-parse.

## Where the retail table came from

`Trick_Skip` dispatches through a jump table whose case bodies do nothing but
return an advanced pointer, so the case target *is* the record size:

```
op = p[0]                      ; byte opcode
if (op >= 0x5A) return p + 1   ; default
jr table[op]
```

Located by shape (`lbu` … `sltiu` bound … `sll ×4` … `lw` … `jr`, with case
bodies of the form `addiu $v0, $a1, k`) in all three PS1 binaries:

| build | dispatch | jump table |
|---|---|---|
| THPS2 PSX final | `0x80022078` | `0x800AFA74` |
| THPS3 PS1 final | `0x800226CC` | `0x800B19C8` |
| THPS4 PS1 final | `0x80023E98` | `0x800B4288` |

All three tables are **identical in content** — same `0x5A` bound, same nine case
classes, same per-opcode assignment.

| class | count | opcodes |
|---|---|---|
| 1 (default) | 31 | `00 02 03 04 05 06 07 08 0C 13 14 16 1A 1C 1E 20 22 2C 30–33 35 3B 3C 3E 46 4A 4D 56 57` |
| 2 | 3 | `52 55 59` |
| 3 | 43 | `01 09 0A 0D 0E 10–12 15 18 19 1B 1D 1F 21 23–2A 2D–2F 34 36 3A 3D 3F–44 49 4B 4E 51 53 54 58` |
| 4 | 1 | `4C` |
| 5 | 4 | `17 37 47 48` |
| 7 | 4 | `0F 2B 38 45` |
| 9 + C string | 1 | `4F` |
| C string | 2 | `0B 50` |
| `3 + 2*count` | 1 | `39` |

The prototype table is the matched decomp's own (`PHYSICS.cpp:4268`) and is held
separately rather than derived: the halfword opcode does make every shared width
exactly one larger, but `0x17` genuinely changed class between the builds (4
bytes in the prototype, 5 in retail), so deriving one table from the other would
mis-size it. Retail also *added* opcodes `0x49`–`0x59`.

## Why the operands are animation indices

`ExtraAnims_AddTrick` appends the `0x01` operands onto the skater's animation
list, which goes to `Spool_StripModel(region, pAnimationList)`. That function's
header comment says "mesh index list" and types its parameter `meshList` — both
wrong. The PERFECT-matched body walks `PSXRegion[].pAnimFile` with `NumAnims`
and an `AnimUsed[512]` bitmap, keeping only animations whose index appears.

The corpus confirms it: in every build the highest slot any trick references is
the bank's **last index**.

| build | bank slots | highest referenced |
|---|---|---|
| THPS2 proto | 147 | 146 |
| THPS2 retail | 218 | 217 |
| THPS3 PS1 | 226 | 225 |
| THPS4 PS1 | 235 | 234 |

## Naming policy

**Only uniquely-owned slots are named.** Trick scripts share approach and
recovery animations heavily — prototype slot 14 leads "Kissed the Rail" but 28
tricks reference it — so naming a slot after the first trick that mentions it
would attach an arbitrary, usually wrong, label. A slot reached from more than
one trick keeps its synthetic `anim_N` name.

The bank/table pairing is positional (one table beside one bank; neither file
references the other), so `TrickAnimationNames.BuildForBank` declines the whole
map unless every referenced slot exists in the bank.

## What was tried and refuted

Before the binary route, the table was attacked from the data alone: the name
records are self-locating, so the bytes between two of them must decompose
exactly into whole records, giving ~200 exact tiling equations. Solving those by
arc consistency was **validated against the prototype first**, where the decomp
gives ground truth, and refuted there:

- Pruning per gap by intersection is unsound — an opcode absent from a gap's
  true tiling still appears in bogus ones, so the truth gets pruned. Restricting
  to positions every tiling agrees on (cut positions) is sound but pins only
  **2 of 63** opcodes.
- Singleton arc consistency adds nothing once the candidate set is wide enough
  to express variable-length instances.
- On retail the test has no power at all: with byte opcodes and small sizes, a
  **uniform width of 1 tiles 198/198 gaps**.

Run straight on retail, that approach would have produced a plausible-looking
table with roughly eight silent errors. Validating the method where ground truth
existed is what caught it.
