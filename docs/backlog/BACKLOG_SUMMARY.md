# Neversoft Multitool — Work Backlog (created 2026-07-03)

> **Re-verified 2026-07-26 vs HEAD (v1.3.4, 60d0b81) — full-domain audit.** Statuses below reflect the current tree; several formerly-open streams have shipped and are retracted where noted.

Documented pending work so each stream can be delegated and picked up as a self-contained task. Modeled after the Xbox360MemoryCarver `docs/backlog` layout.

Sources: the **THPG/Project 8** stream is **verified this session** (conversions run against the sample builds). The remaining streams are distilled from the project's own records — `CLAUDE.md` (its *Deferred Items* / *Not Yet Implemented* / *Research & Improvements* sections) and the auto-memory topic files under `memory/`. As of the **2026-07-26 full re-verification vs HEAD (v1.3.4, 60d0b81)**, every stream has been re-confirmed against the current tree (previously several were carried forward un-re-verified); a delegated session should still re-confirm before deep work, as statuses drift as commits land.

## Status legend

| Tag | Meaning | Action for a delegated session |
|---|---|---|
| 🔴 **Open** | No working implementation; net-new | Implement |
| 🔶 **Partial** | Converts/runs, but output is wrong or incomplete | Finish the named gap |
| 🟢 **Verified this session** | Reproduced first-hand with evidence below | Trust the evidence; start here |
| ✅ **Done** | Shipped + evidence, kept for reference | Don't redo |
| ⚪ **By design / won't-fix** | Format or engine limitation | Don't chase |

## Streams

| File | Stream | Headline gap | Status |
|---|---|---|---|
| `game-thpg-p8.md` | THPG + Project 8 (PS2) support | **P8 + THPG skins/mdl/ska/qb/textures all work at HEAD** (THPG wrap-garble files are unused old-scale legacy data, ⚪); remaining gap = bare `.col`/`.skin` **extension routing** (parser already supports the data), not a new format | 🟢🔴(S) |
| `mesh-fidelity.md` | Mesh reconstruction correctness | **Claimed meshes verified good at HEAD**; remaining work = QA harness, worldzone multi-pass terrain, filename-free PSX appendage discovery | 🟢/🔴/🔶 |
| `animation.md` | Skeletal animation | THPS3 SKA spasms (parsing done — fidelity bug); PSX placed-object skeletal anim; PSX pulsing-colour playback | 🔶 |
| `gsreplay-fidelity.md` | THAW GS-replay render fidelity | 30/30 GsDump tests pass; shadow/magenta/over-bright saga resolved; residual = FBW-aliased bloom + biased metric + no oracle→converter coupling | 🔶 (research, low urgency) |
| `formats-todo.md` | Unimplemented / deferred formats | THAW `.tex.ps2`, THUG2 `.pcm`, and THUG2 PC `.snd` are now implemented; still open: `.stex`, STR VLC drift, `.anim`, `.col.ngc`, `.mcol` | 🔴/🔶 |

> **Mesh-fidelity sweep (2026-07-03, re-verified 2026-07-26):** the meshes we *claim* to support are healthy at HEAD — THAW skins **332/332** convert (Muska fixed; `skater_lasek` 100% recall); PSX characters (Spider-Man, THPS2) render correctly (490 files, 0 real failures; the v1.2.2 "THUG/COP/Spider-Man anims broken" feedback was fixed by `e60d4aa` + `c94e2c3`). The `CLAUDE.md`/`memory` "garbled character mesh" notes are **stale**. **RETRACTED:** the earlier "the only genuinely-broken mesh path is Proving Ground (2007) character skins — a VIF re-encoding" is FALSE at HEAD — THPG skins decode to complete characters (95.6% oracle via `ThpgPositionUnwrapper`); the wrapping whole-character files are unused old-scale legacy exports the game never loads (`game-thpg-p8.md` GS-dump/savestate proof; `memory/thpg_skin_decode.md`), and in-game CAS pieces are piece-local Q4.12 and decode exactly. No user-facing THPG/P8 mesh break remains. See `mesh-fidelity.md` for the sweep evidence.

## Highest-leverage / start-here

**Tier 1 — DONE 2026-08-07.**

1. ✅ **Bare `.col` + bare `.skin` + `.dff` extension routing** — shipped. The cause was five unsynchronized extension lists, now folded into `Core/Formats/Mesh/Detection/MeshTypeDetector` (mirroring `ArchiveTypeDetector`). 1,678 bare `.col`, 3,608 bare `.skin` and 477 `.dff` now reach their parsers. Bare `.skin` routes by content with `IsXbxScene` demoted to LAST — it is only a `(1,1,1)` triple, which ~32% of PS2-build files also satisfy. A companion commit added `MeshOutputPathPlanner`, because those files collide on output name (a THPG missions tree is 50 files all called `6F980DC3.col`, previously yielding one GLB, now 34).
2. ✅ **PC/Xbox THAW additive/subtractive approximation** — RETRACTED as an open item: already shipped by `XbxPassCompositor` (`25d3283`), which composites pass k≥1 and bakes ADD/SUB. `CLAUDE.md`'s "Not Yet Implemented" entry naming `ped_boone_full`'s tattoo contradicted its own §31 and has been deleted.

**Tier 2 — the THAW fidelity chain (M→L, user-facing):**

3. **PC/Xbox THAW multi-pass texture-stage baking** (`formats-todo.md`, M) — `XbxGeometryWriter` exports `Passes[0]` only; pass-k overlays (tattoos/decals) are dropped. Composite pass-k over pass-0 by pass-k alpha under a synthetic checksum (same pattern as `Ps2GeomDestinationAlphaSynthesis`).
4. **PS2 worldzone multi-pass terrain + zone-TEX swizzle** (`mesh-fidelity.md`/`formats-todo.md`, L each) — overlapping GS passes z-fight/get suppressed, and some zone-TEX subgroups decode with the wrong swizzle/offset → garbled worldzone textures.
5. **`gsreplay-fidelity.md`** — deep, well-documented research stream (several `memory/gsdump_*` files); 30/30 GsDump tests pass at HEAD and the shadow/magenta/over-brightness sagas are resolved. Lowest urgency as a converter, but it is the **verification dependency** for the Tier-2 THAW blend chain: there is currently **no programmatic oracle→converter coupling** (replay is human-read; nothing imports `Formats.GsDump`), and the scoring metric is a biased embedded-HW-screenshot reference. Building that coupling + a fixed metric is what would let the THAW blend claims be VERIFIED, not just implemented.

## Dependency notes

- **THPG/P8 skins ↔ mesh-fidelity ↔ THAW pre-compiled skins.** The version-`(1,8,8)` THPG/P8 skins appear to be a variant of the THAW `.skin.ps2` pre-compiled VIF/DMA chain (leading `1` matches). Work on either should cross-reference `memory/thaw_ps2_skin_format.md` and `ThawPs2SkinFile.cs`. (Note: THPG/P8 skins are RESOLVED at HEAD — this cross-reference is for the remaining THUG2 bare pre-compiled `.skin.ps2` and THAW static-export gaps, not a THPG break.)
- **GS-replay fidelity is a texture/blend-semantics stream**, not a mesh stream — it validates the THAW *renderer* against PCSX2, and is where the CLUT / alpha / blend work lives. It does not block any converter, but it IS the verification dependency for the THAW blend/multi-pass fidelity chain (Tier 2 above): the coupling `Formats.GsDump` → the converters does not exist yet, so THAW blend claims are currently implemented-but-unverified.
- **`.blend` limb-stretch (cross-cutting).** The v1.3.4 `.blend` limb-stretch fix was gated `SourceKind == "Psx"`; THAW/PS2 and THPS3 skinned characters share the SAME latent double-translation `.blend` stretch. Generalizing needs the `matrix_basis` form + a real rig to validate — relevant to both `mesh-fidelity.md` and `animation.md`.

## Proven dead-ends (don't reattempt — GS renderer)

- PSMCT16 RT-compose for "vertical bands" (net regression; bands are 5-bit quantization).
- PSM-aware upload cache (MAE-neutral / byte-identical; VRAM-stomping hypothesis disproven).
- 16:9 "image shifted up" = PCSX2 letterbox, not a bug (don't apply NTSC DX/DY offsets).
- Framebuffer-feedback triage found no bug (0290 scramble is genuine game-side aliasing).
- The queued "force-latch-magenta-palette" shadow experiment and the full PCRTC-sim refactor are STALE — the shadow decal renders correctly and the magenta SPECIAL meter was a float32 Z-interp overshoot past 2^24 (fixed `74a603b` + `2c078f9`).

## Conventions (per-file)

Each stream file has: a header (created date · legend · dependencies/memory refs) · `## Remaining — needs work` with per-item **Source / Evidence (`file:line` / commit / `memory/…`) / What's left** · `## Done (for reference)` · `## By design / won't-fix`. Every item traces to a source (this session, `CLAUDE.md`, or a `memory/` file).
