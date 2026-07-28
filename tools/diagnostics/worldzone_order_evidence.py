#!/usr/bin/env python3
"""Re-derive worldzone draw-order evidence with attribution-untrustworthy checksums excluded.

The WorldzoneOracleCensus tool (tools/WorldzoneOracleCensus) scored converter
draw order for same-geometry overlap-cluster pairs against the GS-oracle
goldens: for each capture that drew BOTH pair checksums, it compared
frame-global FirstDrawIndex (preferring state buckets matching the leaf's
A/B/C/D blend state, falling back to all buckets) and counted one vote per
capture. That census trusted every (checksum, capture) draw bucket — but the
texoracle goldens prove some TEX0 slots carried STREAMED FOREIGN CONTENT in
some captures (e.g. 0x0935DD38), so those buckets describe someone else's
draws and their order evidence is void.

Trust rule (per checksum, per capture tag):
  - trusted:    the capture's texoracle rows for the checksum exist and are
                ALL in {Match, QuantizationOnly, AlphaProtocolDiff}
  - unverified: the capture has NO texoracle row for the checksum (drawn but
                content never verified — reported separately, not silently
                trusted)
  - untrusted:  any row in {Divergent, ForeignContent, AttributionMismatch,
                SlotReuseSuspect, NotComparable} (or an unknown label)

Votes are recomputed for every cluster pair under three tiers:
  (a) trusted-only          — both checksums trusted in the voting capture
  (b) trusted + unverified  — neither checksum untrusted in the voting capture
  (c) everything            — reproduces the census (sanity-checked against
                              cluster_pairs.csv vote columns)

Usage:
  python tools/diagnostics/worldzone_order_evidence.py
      [--goldens tests/NeversoftMultitool.Tests/GoldenFiles/GsOracle]
      [--pairs TestOutput/WorldzoneOracleCensus/cluster_pairs.csv]
      [--verbose]

Reads only committed goldens + the census CSV; writes nothing.
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from collections import Counter
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

GOOD = {"Match", "QuantizationOnly", "AlphaProtocolDiff"}
BAD = {"Divergent", "ForeignContent", "AttributionMismatch", "SlotReuseSuspect", "NotComparable"}


def load_goldens(golden_dir: Path):
    """-> (sorted capture tags, draws[tag][checksum] = list of bucket dicts,
           tex[tag][checksum] = list of Classification strings)."""
    tags = []
    draws = {}
    tex = {}
    for f in sorted(golden_dir.glob("*.gsoracle.json"), key=lambda p: p.name.lower()):
        tag = f.name.split(".", 1)[0]
        report = json.loads(f.read_text())
        tags.append(tag)
        per = {}
        for t in report["Textures"]:
            per.setdefault(t["Checksum"], []).extend(t["StateBuckets"])
        draws[tag] = per
    for f in sorted(golden_dir.glob("*.texoracle.json"), key=lambda p: p.name.lower()):
        tag = f.name.split(".", 1)[0]
        report = json.loads(f.read_text())
        per = {}
        for row in report["Rows"]:
            per.setdefault(row["Checksum"], []).append(row["Classification"])
        tex[tag] = per
    return tags, draws, tex


def first_draw_index(draws, tag, checksum, abcd):
    """Mirror of OracleGoldenSet.FirstDrawIndex: min FirstDrawIndex over
    buckets whose AlphaA/B/C/D equal the leaf state, else min over all
    buckets; None when the capture never drew the checksum."""
    buckets = draws.get(tag, {}).get(checksum)
    if not buckets:
        return None
    a, b, c, d = abcd
    matched = [k["FirstDrawIndex"] for k in buckets
               if k["AlphaA"] == a and k["AlphaB"] == b and k["AlphaC"] == c and k["AlphaD"] == d]
    if matched:
        return min(matched)
    return min(k["FirstDrawIndex"] for k in buckets)


def trust_of(tex, tag, checksum):
    rows = tex.get(tag, {}).get(checksum)
    if not rows:
        return "unverified", []
    offenders = [r for r in rows if r not in GOOD]
    return ("trusted", []) if not offenders else ("untrusted", sorted(set(offenders)))


def abcd_of(blend_byte):
    return (blend_byte & 3, (blend_byte >> 2) & 3, (blend_byte >> 4) & 3, (blend_byte >> 6) & 3)


def agreement(a_before, b_before, both):
    if both == 0:
        return "unobserved"
    if a_before > b_before:
        return "agrees"
    if b_before > a_before:
        return "DISAGREES"
    return "tied"


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--goldens", type=Path,
                    default=REPO_ROOT / "tests/NeversoftMultitool.Tests/GoldenFiles/GsOracle")
    ap.add_argument("--pairs", type=Path,
                    default=REPO_ROOT / "TestOutput/WorldzoneOracleCensus/cluster_pairs.csv")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    tags, draws, tex = load_goldens(args.goldens)
    print(f"goldens: {len(tags)} captures from {args.goldens}")

    # Global trust census — the texoracle comparer only sees vram/image_upload
    # dumps (external dumps ARE the catalog fed back; comparing is circular),
    # so most drawn checksums have no row at all (= unverified here).
    row_census = Counter()
    combo_census = Counter()
    for tag in tags:
        for checksum, cls in tex.get(tag, {}).items():
            row_census.update(cls)
            combo_census["trusted" if all(c in GOOD for c in cls) else "untrusted"] += 1
    print(f"texoracle rows by classification: {dict(row_census)}")
    print(f"(checksum, capture) combos with rows: {dict(combo_census)}")
    if not any(row_census[c] for c in GOOD):
        print("NOTE: the committed texoracle goldens contain ZERO rows in "
              "{Match, QuantizationOnly, AlphaProtocolDiff} - no (checksum, capture) "
              "can be 'trusted' under the strict rule; tier (a) is vacuously empty.")

    pairs = list(csv.DictReader(args.pairs.open()))
    print(f"census pairs: {len(pairs)} from {args.pairs}\n")

    tier_stats = {t: Counter() for t in ("a", "b", "c")}
    evidenced = []       # per-pair dict with per-tier votes + per-capture detail
    sanity_mismatch = []
    gained = []          # pairs the census had unobserved but goldens now evidence

    for row in pairs:
        tex_a = int(row["texA"], 16)
        tex_b = int(row["texB"], 16)
        if tex_a == 0 or tex_b == 0 or tex_a == tex_b:
            continue  # census carries no order signal for these by design
        abcd_a = abcd_of(int(row["alphaBlendA"], 16))
        abcd_b = abcd_of(int(row["alphaBlendB"], 16))

        detail = []  # (tag, fa, fb, vote, trustA, badA, trustB, badB)
        for tag in tags:
            fa = first_draw_index(draws, tag, tex_a, abcd_a)
            fb = first_draw_index(draws, tag, tex_b, abcd_b)
            if fa is None or fb is None:
                continue
            vote = "A" if fa < fb else ("B" if fb < fa else "tie")
            ta, bad_a = trust_of(tex, tag, tex_a)
            tb, bad_b = trust_of(tex, tag, tex_b)
            detail.append((tag, fa, fb, vote, ta, bad_a, tb, bad_b))

        # sanity: tier (c) must reproduce the census CSV columns
        c_both = len(detail)
        c_a = sum(1 for d in detail if d[3] == "A")
        c_b = sum(1 for d in detail if d[3] == "B")
        c_t = sum(1 for d in detail if d[3] == "tie")
        census_cols = (int(row["capturesBoth"]), int(row["capturesABeforeB"]),
                       int(row["capturesBBeforeA"]), int(row["capturesTied"]))
        if (c_both, c_a, c_b, c_t) != census_cols:
            sanity_mismatch.append((row, (c_both, c_a, c_b, c_t), census_cols))
        if c_both > 0 and census_cols[0] == 0:
            gained.append(row)
        if c_both == 0:
            continue

        votes = {}
        for tier in ("a", "b", "c"):
            if tier == "a":
                sub = [d for d in detail if d[4] == "trusted" and d[6] == "trusted"]
            elif tier == "b":
                sub = [d for d in detail if d[4] != "untrusted" and d[6] != "untrusted"]
            else:
                sub = detail
            na = sum(1 for d in sub if d[3] == "A")
            nb = sum(1 for d in sub if d[3] == "B")
            nt = sum(1 for d in sub if d[3] == "tie")
            votes[tier] = (len(sub), na, nb, nt, agreement(na, nb, len(sub)),
                           [d[0] for d in sub])
            tier_stats[tier][votes[tier][4]] += 1
        evidenced.append({"row": row, "detail": detail, "votes": votes})

    # ---- report -------------------------------------------------------------
    if sanity_mismatch:
        print("!! SANITY FAILURE: recompute does not reproduce the census CSV:")
        for row, got, want in sanity_mismatch:
            print(f"   cluster {row['clusterId']} leaves {row['leafA']}/{row['leafB']}: "
                  f"recomputed {got} vs census {want}")
    else:
        print("sanity: tier (c) reproduces every census vote column exactly "
              f"({len(evidenced)} evidenced pairs).")
    if gained:
        print(f"pairs gaining evidence vs census: {len(gained)} (goldens drifted since census run)")
    else:
        print("no pair gains evidence beyond the census set (as expected - "
              "trust filtering only removes votes).")

    vote_census = Counter()
    for e in evidenced:
        for _, _, _, _, ta, _, tb, _ in e["detail"]:
            vote_census[tuple(sorted((ta, tb)))] += 1
    print("\ncapture-votes across evidenced pairs by (trustA, trustB): "
          + ", ".join(f"{k}: {v}" for k, v in sorted(vote_census.items())))

    print("\n=== three-tier agreement stats over evidenced pairs ===")
    names = {"a": "(a) trusted-only", "b": "(b) trusted+unverified", "c": "(c) everything"}
    for tier in ("a", "b", "c"):
        s = tier_stats[tier]
        print(f"  {names[tier]:24s}: agrees {s['agrees']}, DISAGREES {s['DISAGREES']}, "
              f"tied {s['tied']}, unobserved-after-filter {s['unobserved']}")

    print("\n=== per-pair adjudication ===")
    for e in evidenced:
        r = e["row"]
        va, vb, vc = e["votes"]["a"], e["votes"]["b"], e["votes"]["c"]
        interesting = (vc[4] == "DISAGREES" or va[4] != vc[4] or vb[4] != vc[4]
                       or args.verbose)
        if not interesting:
            continue
        print(f"\ncluster {r['clusterId']} {r['mdl']}/{r['space']} "
              f"converter order: leaf {r['leafA']} {r['texA']} -> leaf {r['leafB']} {r['texB']}")
        for tier in ("a", "b", "c"):
            n, na, nb, nt, verdict, vtags = e["votes"][tier]
            print(f"  tier {tier}: {na}:{nb} (ties {nt}) over {n} captures -> {verdict}"
                  + (f"  [{','.join(vtags)}]" if n else ""))
        for tag, fa, fb, vote, ta, bad_a, tb, bad_b in e["detail"]:
            flags = []
            if ta != "trusted":
                flags.append(f"A={ta}{':' + '/'.join(bad_a) if bad_a else ''}")
            if tb != "trusted":
                flags.append(f"B={tb}{':' + '/'.join(bad_b) if bad_b else ''}")
            print(f"    {tag}: firstDraw A={fa} B={fb} vote={vote}"
                  + (f"  !! {' '.join(flags)}" if flags else ""))

    print("\n=== census DISAGREES adjudication summary ===")
    for e in evidenced:
        if e["votes"]["c"][4] != "DISAGREES":
            continue
        r = e["row"]
        va = e["votes"]["a"]
        vb = e["votes"]["b"]
        status = ("SURVIVES (trusted-only)" if va[4] == "DISAGREES"
                  else "SURVIVES only with unverified votes" if vb[4] == "DISAGREES"
                  else "VOIDED")
        print(f"  cluster {r['clusterId']} leaves {r['leafA']}->{r['leafB']} "
              f"{r['texA']}/{r['texB']}: {status} "
              f"(tier a {va[1]}:{va[2]}/{va[0]}, tier b {vb[1]}:{vb[2]}/{vb[0]}, "
              f"tier c {e['votes']['c'][1]}:{e['votes']['c'][2]}/{e['votes']['c'][0]})")

    return 0 if not sanity_mismatch else 1


if __name__ == "__main__":
    sys.exit(main())
