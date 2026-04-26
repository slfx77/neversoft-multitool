# Neversoft file-format references

A growing collection of format specifications for the Neversoft-engine assets
this project parses. Each doc aims to be a freestanding reference: what the
bytes mean, how the runtime engine consumes them, and what in our parser is
verified vs. still speculative.

The bar for adding a format here is higher than for the `CLAUDE.md` index
entry — docs in this directory should cite **binary evidence** (either the
original engine decompilation or a data-set cross-reference), so that a reader
who is not already familiar with the format can trust them.

## Format docs

| Format | Games | Role | Status |
|---|---|---|---|
| [THAW worldzone MDL](thaw-worldzone-mdl.md) | THAW (PS2) | Level geometry (streets, buildings) | Incomplete: runtime-side decomp mostly verified, on-disk preamble fields partly speculative |

## Adding a new format doc

When you start a new format spec, copy the section scaffold from
`thaw-worldzone-mdl.md`:

1. **Summary** — one-paragraph description, games it appears in, source in
   this repo, representative file path for testing.
2. **File identity** — magic/signature/hash, how we distinguish it from
   neighbouring formats.
3. **File layout** — top-down map from file offset 0 to end.
4. **Records** — one table per repeating structure, with offsets, types,
   names, and evidence.
5. **Runtime representation** (if materially different from disk) — what the
   engine does with the bytes at load time.
6. **Open questions** — an honest list of what's still speculation or TODO.
7. **Evidence trail** — links to the decomp artefacts, test data, and code
   that implement or validate the spec.

When you add a new format here, also add a one-line entry to the table above.
