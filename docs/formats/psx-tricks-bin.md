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
| THPS1 PS1 | — | — | ships no `tricks.bin` (and its EXE holds no trick strings) | | |
| THPS2 2000-3-29 proto | 13,362 | halfword LE | 121 | 103 | 38 |
| THPS2 PSX / DC / 2X | 33,168 / 33,192 | byte LE | 202 | 181 | 111 |
| THPS3 PS1 | 33,540 | byte LE | 230 | 202 | 125 |
| THPS4 PS1 | 33,408 | byte LE | 229 | 201 | 125 |
| THPS2 N64 | 33,168 | byte **BE** | 202 | 181 | 110 |
| THPS3 N64 | 33,540 | byte **BE** | 230 | 202 | 124 |
| Spider-Man N64 | — | — | no trick table (not a skating game) | | |

The N64 ports carry the table as an ordinary carved payload with no
distinguishing name, so it is found by parsing rather than by path. Its bytes
are **not** the PS1 sibling's — 21,318 of THPS2's 33,168 differ — because each
port authored operands against its own animation bank; yet the two agree on
every slot they both name.

**THPS1 N64 is a separate case**: it ships no standalone table, but its
`boot.bin` contains the trick name strings, so the data is embedded in the
executable. THPS1's PS1 EXE contains no such strings. Naming THPS1 animations
would need that embedded table located and is not attempted.

## Record grammar

A trick is a run of opcode records introduced by a name record (`0x0B`, opcode
plus a NUL-terminated string) and closed by `0x07`. Record `0x01` carries an
animation slot index.

Two dialects and two operand byte orders, all detected from the file rather than
assumed from the build:

- **Halfword** (2000-3-29 THPS2 prototype): opcode and operands are 16-bit, so
  records are 2-byte aligned and the name case ends with `& ~1`.
- **Byte** (every shipped retail build): the opcode narrowed to one byte while
  operands stayed 16-bit and became **unaligned**. Retail's operand reader is an
  explicit `lbu`/`lbu`/shift pair rather than an `lh` precisely because a record
  may now begin at an odd offset — which is the tell that the re-encoding is
  real and not a mis-parse.
- **Operand byte order** follows the console: little-endian on PS1/DC/Xbox,
  big-endian on N64. The record grammar is otherwise identical. This is not
  cosmetic — read the wrong way, slot indices come back spread across the whole
  s16 range (`0x8F00` instead of `0x008F`), so a file parses cleanly in exactly
  one order and into garbage in the other.

## Rejecting things that are not trick tables

`0x0B` followed by printable ASCII occurs by chance in unrelated data, so
`Parse` gates a candidate reading on four measured properties:

| property | all 8 real tables | rejected examples |
|---|---|---|
| every trick terminates | 100% | 153/193 (N64 render bank) |
| tricks carrying a plausible slot | 85–90% | — |
| distinct names | 82–83% | 4/8 (`aaaaaaaa` ×8) |
| trick count | 121–230 | 8 |

Thresholds sit at half for the two ratios and 32 for the count — wide margins
below every real table rather than tuned values. A real table read in the wrong
byte order also fails these, which is what makes byte-order selection decisive
rather than a coin-flip between near-equal scores.

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

## Finding the table on an N64 cart

The carts ship the same table, but a carve has no name for it — it is emitted as
an unclassified payload (`misc/164.bin` in both THPS2 and THPS3, though the
ordinal is not guaranteed). So `N64TrickTableLocator` finds it by **parsing**
rather than by path, which the credibility gate above makes safe: exactly one
carved asset per cart qualifies.

The sweep is kept cheap by skipping the three bulk roles (`models/`, `group2/`,
`textures/`) — none of which can hold the table — applying a size band, and then
a one-pass count of `0x0B`-plus-printable-ASCII pairs before any parse. Results
cache per cart. Both source shapes are covered: an archive walk for a ROM opened
in place, and a carve-root walk for a carve extracted to disk. They must agree,
and that parity is pinned — the two take different branches and it is not free.

**The bank gate is EXACT here, not "every slot fits."** On disc the table sits
beside its bank, so containment is enough; on a cart the pairing is found by
search, and a cart holds shells with as many as 300 clips — any of which would
swallow a 218-slot table's names under a containment test.
`TrickAnimationNames.BuildForExactBank` therefore requires the bank's last index
to BE the table's highest reference. Measured, both skating carts keep their
skater bank at slot 045 and both fit exactly:

| cart | bank slots | table max slot | slots named |
|---|---|---|---|
| THPS2 N64 | 218 | 217 | 110 |
| THPS3 N64 | 226 | 225 | 124 |
| Spider-Man N64 | — | no table | 0 |

A per-cart census pins that exactly one bundle takes the names.

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
