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
| `game-thpg-p8.md` | THPG + Project 8 (PS2) support | **P8 + THPG skins work** (2026-07-06: THPG wrap-garble files are unused old-scale legacy data, ⚪); newer `.col` version unsupported | 🟢🔶 |
| `mesh-fidelity.md` | Mesh reconstruction correctness | **Claimed meshes verified good at HEAD**; remaining work = QA harness, worldzone, filename-free PSX appendage discovery | 🟢/🔴/🔶 |
| `animation.md` | Skeletal animation | THPS3 SKA spasms; RW DFF T-pose only; PSX anim per-game | 🔶 |
| `gsreplay-fidelity.md` | THAW GS-replay render fidelity | Shadow-decal streaks; residual over-brightness | 🔶 (research) |
| `formats-todo.md` | Unimplemented / deferred formats | THAW `.tex.ps2` metadata; `.stex`; STR VLC drift | 🔴/🔶 |

> **Mesh-fidelity sweep (2026-07-03):** the meshes we *claim* to support are healthy at HEAD — THAW skins **332/332** convert (Muska fixed; `skater_lasek` 100% recall); PSX characters (Spider-Man, THPS2) render correctly. The `CLAUDE.md`/`memory` "garbled character mesh" notes are **stale**. The only genuinely-broken mesh path is **Proving Ground (2007) character skins** — a VIF re-encoding; Project 8 skins render fine (`game-thpg-p8.md`). See `mesh-fidelity.md` for the sweep evidence.

## Highest-leverage / start-here

1. **`game-thpg-p8.md`** — the THPG skin garble was RESOLVED 2026-07-06 (support is complete for everything the game renders; the wrapping files are unused old-scale legacy data with no reconstruction mechanism — see the stream file). The remaining bounded gap is the newer `.col` collision version.
2. **`mesh-fidelity.md`** — shares a root with THPG/P8 skins (strip-topology reconstruction) and with the long-standing PSX character-model problem. High cross-cut value.
3. **`gsreplay-fidelity.md`** — deep, well-documented research stream (several `memory/gsdump_*` files). Self-contained; needs PCSX2 reference captures. Lowest urgency (it's a validation reference, not a user-facing converter), highest depth.

## Dependency notes

- **THPG/P8 skins ↔ mesh-fidelity ↔ THAW pre-compiled skins.** The version-`(1,8,8)` THPG/P8 skins appear to be a variant of the THAW `.skin.ps2` pre-compiled VIF/DMA chain (leading `1` matches). Work on either should cross-reference `memory/thaw_ps2_skin_format.md` and `ThawPs2SkinFile.cs`.
- **GS-replay fidelity is a texture/blend-semantics stream**, not a mesh stream — it validates the THAW *renderer* against PCSX2, and is where the recent CLUT / alpha / blend work lives. It does not block any converter.

## Conventions (per-file)

Each stream file has: a header (created date · legend · dependencies/memory refs) · `## Remaining — needs work` with per-item **Source / Evidence (`file:line` / commit / `memory/…`) / What's left** · `## Done (for reference)` · `## By design / won't-fix`. Every item traces to a source (this session, `CLAUDE.md`, or a `memory/` file).
