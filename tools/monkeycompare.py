"""Compare two UI test runs that played the same steps (a monkey run and its replay, e.g. mod on / mod off).

    python monkeycompare.py <bench/ui folder> <run A> <run B> [--out report.md]

Run names may be a unique part of the name (latest match). Keeps, step by step, only what differs between the two
runs: errors, template texts, shown texts (numbers ignored), list rows (empty in one run only, or a gap of more
than 2 rows), top window, frames over 100 ms, and the screenshots both runs took (share of pixels that differ).
"""
import json
import re
import sys
from pathlib import Path

from uicompare import diff


def find(folder: Path, part: str) -> Path:
    runs = sorted(p for p in folder.glob("*.json") if part in p.stem and not p.stem.endswith(".steps"))
    if not runs:
        sys.exit(f"no run matching '{part}' in {folder}")
    return runs[-1]  # latest

NUMERIC = re.compile(r"^[\d\s.,:;%$€+\-/()xXm×]*$")
DIFF_SHARE = 0.02  # screenshot pixels that may differ (animated world behind the window, clock)


def lists(text):
    out = {}
    for part in (text or "").split():
        name, _, n = part.rpartition(":")
        if n.isdigit():
            out.setdefault(name, []).append(int(n))
    return out


TAG = re.compile(r"<[^>]*>")


def texts(step):
    """Shown texts, rich-text tags removed; numbers only (prices, clock, money) are not compared."""
    out = set()
    for t in step.get("texts") or []:
        plain = TAG.sub("", t).strip()
        if plain and not NUMERIC.match(plain):
            out.add(plain)
    return out


def compare(a, b, dir_a, dir_b):
    rows, diverged = [], None
    for sa, sb in zip(a["steps"], b["steps"]):
        n, found = sa["n"], []
        if sa["step"] != sb["step"]:
            diverged = f"step {n}: '{sa['step']}' vs '{sb['step']}'"
            break
        if sa["errors"] != sb["errors"]:
            found.append(f"errors {sa['errors']} -> {sb['errors']}")
        if sa.get("selected") and sb.get("selected") and sa["selected"].split("/")[-1] != sb["selected"].split("/")[-1]:
            found.append(f"selection {sa['selected']} -> {sb['selected']}")
        if (sa.get("top") or "-") != (sb.get("top") or "-"):
            found.append(f"top {sa.get('top')} -> {sb.get('top')}")
        ta, tb = set(sa.get("templates") or []), set(sb.get("templates") or [])
        if tb - ta:
            found.append("template only in B: " + ", ".join(sorted(tb - ta)[:3]))
        if ta - tb:
            found.append("template only in A: " + ", ".join(sorted(ta - tb)[:3]))
        xa, xb = texts(sa), texts(sb)
        if xa != xb:
            only_a, only_b = sorted(xa - xb), sorted(xb - xa)
            if only_a:
                found.append(f"texts only in A ({len(only_a)}): " + " / ".join(only_a[:4]))
            if only_b:
                found.append(f"texts only in B ({len(only_b)}): " + " / ".join(only_b[:4]))
        la, lb = lists(sa.get("lists")), lists(sb.get("lists"))
        for name in sorted(set(la) | set(lb)):
            ca, cb = la.get(name, []), lb.get(name, [])
            if len(ca) != len(cb) or any((x == 0) != (y == 0) or abs(x - y) > 2 for x, y in zip(ca, cb)):
                found.append(f"list {name} {ca} -> {cb}")
        slow_a, slow_b = sa.get("maxMs", 0) > 100, sb.get("maxMs", 0) > 100
        if slow_a != slow_b:
            found.append(f"frame >100 ms only in {'B' if slow_b else 'A'} ({sa.get('maxMs')} -> {sb.get('maxMs')})")
        for shot in {sa.get("shot"), sb.get("shot")} - {None}:
            pa, pb = dir_a / shot, dir_b / shot
            if pa.exists() and pb.exists():
                share, why = diff(pa, pb, dir_b / f"diff-{shot}")
                if why or share > DIFF_SHARE:
                    found.append(f"screenshot {shot} differs " + (why or f"{share:.1%}"))
        if found:
            rows.append((n, sa["step"], found, sa.get("note", ""), sb.get("note", "")))
    return rows, diverged


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    folder = Path(args[0])
    fa, fb = find(folder, args[1]), find(folder, args[2])
    a, b = json.loads(fa.read_text(encoding="utf-8")), json.loads(fb.read_text(encoding="utf-8"))
    rows, diverged = compare(a, b, folder / fa.stem, folder / fb.stem)
    out = [f"# Compare A {fa.stem} / B {fb.stem}", ""]
    out.append(f"{min(len(a['steps']), len(b['steps']))} steps compared, {len(rows)} with differences"
               + (f"; RUNS DIVERGED at {diverged}" if diverged else ""))
    out.append("")
    for n, step, found, na, nb in rows:
        out.append(f"## {n} {step}")
        out += [f"- {f}" for f in found]
        if any(f.startswith("errors") for f in found):
            out += [f"  - A: {na[:300]}", f"  - B: {nb[:300]}"]
        out.append("")
    text = "\n".join(out)
    target = next((x.split("=", 1)[1] for x in sys.argv if x.startswith("--out=")), None)
    target = Path(target) if target else folder / f"compare-{fa.stem}-vs-{fb.stem}.md"
    target.write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
