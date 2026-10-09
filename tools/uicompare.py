"""Compare two UI test runs (bench/ui/<run>.json + screenshots in bench/ui/<run>/).

    python uicompare.py <bench/ui folder> <run A> <run B> [--out report.md]

Run names may be given as a unique part (e.g. "off" / "on", or a date). Writes a Markdown table step by step
(first frame, max frame, frames over 25 ms, errors, top window: A -> B) and, for each screenshot present in both
runs, the share of pixels that differ (and a diff image <runB>/diff-<name>.png where they do).
"""
import json
import sys
from pathlib import Path

from PIL import Image, ImageChops

TOLERANCE = 24  # per channel: small rendering noise (animated HUD, particles) is not a difference


def find(folder: Path, part: str) -> Path:
    runs = sorted(p for p in folder.glob("*.json") if part in p.stem and not p.stem.endswith(".steps"))
    if not runs:
        sys.exit(f"no run matching '{part}' in {folder}")
    return runs[-1]  # latest


def diff(a: Path, b: Path, out: Path):
    ia, ib = Image.open(a).convert("RGB"), Image.open(b).convert("RGB")
    if ia.size != ib.size:
        return None, "size differs"
    d = ImageChops.difference(ia, ib).convert("L").point(lambda v: 255 if v > TOLERANCE else 0)
    changed = d.histogram()[255]
    share = changed / (d.width * d.height)
    if changed:
        mask = d.convert("1")
        red = Image.new("RGB", ib.size, (255, 0, 0))
        Image.composite(red, Image.blend(ib, Image.new("RGB", ib.size), 0.6), mask).save(out)
    return share, ""


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 3:
        sys.exit(__doc__)
    folder = Path(args[0])
    ja, jb = find(folder, args[1]), find(folder, args[2])
    a, b = json.loads(ja.read_text(encoding="utf-8")), json.loads(jb.read_text(encoding="utf-8"))
    lines = [f"# UI compare: {ja.stem} -> {jb.stem}", "",
             "| # | step | first ms | max ms | >25 ms | errors | top window |", "|---|---|---|---|---|---|---|"]
    sb = {s["n"]: s for s in b["steps"]}
    for s in a["steps"]:
        t = sb.get(s["n"])
        if t is None or t["step"] != s["step"]:
            lines.append(f"| {s['n']} | {s['step']} | (steps differ) | | | | |")
            continue
        top = s["top"] if s["top"] == t["top"] else f"**{s['top']} -> {t['top']}**"
        err = f"{s['errors']} -> {t['errors']}" if s["errors"] != t["errors"] else str(s["errors"])
        lines.append(f"| {s['n']} | {s['step']} | {s['firstMs']} -> {t['firstMs']} | {s['maxMs']} -> {t['maxMs']} | "
                     f"{s['over25']} -> {t['over25']} | {err} | {top} |")
    lines += ["", "## Screenshots", "", "| shot | pixels differing |", "|---|---|"]
    da, db = folder / ja.stem, folder / jb.stem
    for shot in sorted(p for p in da.glob("*.png") if not p.name.startswith("diff-")):
        other = db / shot.name
        if not other.exists():
            lines.append(f"| {shot.stem} | missing in {jb.stem} |")
            continue
        share, why = diff(shot, other, db / f"diff-{shot.name}")
        lines.append(f"| {shot.stem} | {why or f'{share * 100:.2f} %'} |")
    text = "\n".join(lines) + "\n"
    out = next((x.split("=", 1)[1] for x in sys.argv if x.startswith("--out=")), None)
    if out:
        Path(out).write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
