"""Compare blend-mode distribution across two materials.csv outputs.

Usage:
    python inspect_blend_distribution.py <csv_path>

Prints subtractive material details, alpha-c field distribution, and the largest
emitters by pixel count for a single gsdump audit.
"""
import csv
import collections
import sys


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    src = sys.argv[1]
    rows = list(csv.DictReader(open(src, encoding="utf-8")))

    subs = []
    for r in rows:
        try:
            a = int(r["alpha_a"])
            b = int(r["alpha_b"])
            d = int(r["alpha_d"])
            if a == 2 and b == 0 and d == 1:
                subs.append(r)
        except Exception:
            pass

    total = sum(int(r["pixels_written"]) for r in subs)
    print(f"Subtractive materials: {len(subs)} contributing {total:,} pixels\n")

    subs.sort(key=lambda r: -int(r["pixels_written"]))
    print("Top subtractive emitters:")
    for r in subs[:6]:
        print(
            f"  pix={int(r['pixels_written']):>7,}  avg_a={r['avg_a']:>4}  "
            f"avg_rgb={r['avg_rgb']:<22}  c={r['alpha_c']} fix={r['alpha_fix']:>3}  "
            f"tfx={r['texture_tfx']} tcc={r['texture_tcc']} psm={r['texture_psm']} "
            f"aem={r['aem']} ta0={r['ta0']} ta1={r['ta1']}"
        )

    print("\nAlpha-c field for subtractive:")
    c_dist = collections.Counter()
    for r in subs:
        c = int(r["alpha_c"])
        c_dist[c] += int(r["pixels_written"])
    for c, px in sorted(c_dist.items()):
        name = ["As (srcA)", "Ad (dstA)", "FIX"][c]
        print(f"  C={c} ({name}): {px:,} pixels")


if __name__ == "__main__":
    main()
