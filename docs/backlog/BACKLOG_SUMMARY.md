# Neversoft Multitool — Work Backlog (created 2026-07-03)

Documented pending work so each stream can be delegated and picked up as a self-contained task. Modeled after the Xbox360MemoryCarver `docs/backlog` layout.

Sources: the **THPG/Project 8** stream is **verified this session** (conversions run against the sample builds). The remaining streams are distilled from the project's own records — `CLAUDE.md` (its *Deferred Items* / *Not Yet Implemented* / *Research & Improvements* sections) and the auto-memory topic files under `memory/`. Those were **not** re-verified this session; a delegated session should re-confirm current state against HEAD before deep work (statuses drift as commits land).

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
| `game-thpg-p8.md` | THPG + Project 8 (PS2) support | Character skin meshes garble; `.col` unsupported | 🟢🔶 verified |
| `mesh-fidelity.md` | Mesh reconstruction correctness | PSX character models garbled; THAW worldzone "missing parts" | 🔶 |
| `animation.md` | Skeletal animation | THPS3 SKA spasms; RW DFF T-pose only; PSX anim per-game | 🔶 |
| `gsreplay-fidelity.md` | THAW GS-replay render fidelity | Shadow-decal streaks; residual over-brightness | 🔶 (research) |
| `formats-todo.md` | Unimplemented / deferred formats | THAW `.tex.ps2` metadata; PPV container; STR VLC drift | 🔴/🔶 |

## Highest-leverage / start-here

1. **`game-thpg-p8.md`** — the freshest, best-evidenced stream, and the reason this backlog exists. Two concrete, bounded gaps (mesh strip layout for version-triple `(1,8,8)`; a newer `.col` version). Fixing the skin path likely also lifts the general **mesh-fidelity** stream, since THPG skins ride the THAW pre-compiled VIF/DMA path.
2. **`mesh-fidelity.md`** — shares a root with THPG/P8 skins (strip-topology reconstruction) and with the long-standing PSX character-model problem. High cross-cut value.
3. **`gsreplay-fidelity.md`** — deep, well-documented research stream (several `memory/gsdump_*` files). Self-contained; needs PCSX2 reference captures. Lowest urgency (it's a validation reference, not a user-facing converter), highest depth.

## Dependency notes

- **THPG/P8 skins ↔ mesh-fidelity ↔ THAW pre-compiled skins.** The version-`(1,8,8)` THPG/P8 skins appear to be a variant of the THAW `.skin.ps2` pre-compiled VIF/DMA chain (leading `1` matches). Work on either should cross-reference `memory/thaw_ps2_skin_format.md` and `ThawPs2SkinFile.cs`.
- **GS-replay fidelity is a texture/blend-semantics stream**, not a mesh stream — it validates the THAW *renderer* against PCSX2, and is where the recent CLUT / alpha / blend work lives. It does not block any converter.

## Conventions (per-file)

Each stream file has: a header (created date · legend · dependencies/memory refs) · `## Remaining — needs work` with per-item **Source / Evidence (`file:line` / commit / `memory/…`) / What's left** · `## Done (for reference)` · `## By design / won't-fix`. Every item traces to a source (this session, `CLAUDE.md`, or a `memory/` file).
