"""Score predicted plans against the test set.

    python score.py predictions.jsonl [--gold test.jsonl] [--show 20]

Each line of the predictions is {"q": ..., "plan": ...}. Prints how many plans are right,
whether the row was offered when it should be, and the score for each tag in the test set.
"""

import argparse
import collections
import json
import sys

from plan import is_row, parse, same


def load(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pred")
    ap.add_argument("--gold", default=str(__import__("plan").HERE / "test.jsonl"))
    ap.add_argument("--show", type=int, default=15)
    args = ap.parse_args()

    gold = load(args.gold)
    pred = {p["q"]: p["plan"] for p in load(args.pred)}
    right = 0
    bad_format = 0
    rows = collections.Counter()  # (should, did)
    tags = collections.defaultdict(lambda: [0, 0])
    wrong = []
    for g in gold:
        want = parse(g["plan"])
        try:
            got = parse(pred.get(g["q"], "none"))
        except ValueError:
            bad_format += 1
            got = None
        ok = got is not None and same(want, got)
        right += ok
        rows[(is_row(want), got is not None and is_row(got))] += 1
        for t in g.get("tags", []):
            tags[t][0] += ok
            tags[t][1] += 1
        if not ok:
            wrong.append((g["q"], g["plan"], pred.get(g["q"], "(missing)")))

    n = len(gold)
    tp, fp, fn = rows[(True, True)], rows[(False, True)], rows[(True, False)]
    print(f"plans right   {right}/{n}  {100 * right / n:.1f}%")
    print(f"row offered   precision {100 * tp / max(tp + fp, 1):.1f}%  recall {100 * tp / max(tp + fn, 1):.1f}%")
    print(f"unreadable    {bad_format}")
    print()
    for t, (k, m) in sorted(tags.items(), key=lambda x: x[1][0] / x[1][1]):
        print(f"  {t:<14} {k:>3}/{m:<3} {100 * k / m:5.1f}%")
    if wrong and args.show:
        print()
        for q, w, p in wrong[: args.show]:
            print(f"  {q}\n      want {w}\n      got  {p}")


if __name__ == "__main__":
    sys.exit(main())
