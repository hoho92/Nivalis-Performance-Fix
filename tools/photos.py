"""Collect the presentation photos of a route benchmark campaign (photo spots, request "photos").

    python photos.py [results.json ...] [--out folder]

Default: the latest bench/results/*.json of the game folder, written to nexus/shots/auto (the local Nexus page
workspace, not in git). Several results files
(e.g. one launch per variant, see photorun.py) are merged. Per photo spot
(zone/name): average FPS and 1% low per variant (off / on / as is) over all its runs, and the screenshot of the
first run of each variant copied as <zone>-<name>-<variant>.png. Everything also lands in photos.json, which
nexus/maquette/plates.py reads to draw the comparison plates.
"""
import json
import shutil
import sys
from pathlib import Path

from gamedir import game_dir

ROOT = Path(__file__).resolve().parent.parent


def typical(runs):
    """The run whose average FPS is closest to the variant's mean (its frame trace is the one drawn)."""
    mean = sum(r['avgFps'] for r in runs) / len(runs)
    return min(runs, key=lambda r: abs(r['avgFps'] - mean))


def stutters(every, files, out):
    """Route runs (not photo spots) with frame times (photorun.py --routes) -> stutters.json: per route and variant the averages over all
    runs (FPS, 1% low, longest frame, frames over 33 ms) and the frame times of its most typical run."""
    runs = [r for r in every if not r['route'].startswith('photo ') and r.get('frameMs')]
    if not runs:
        return
    path = out / 'stutters.json'
    summary = json.loads(path.read_text(encoding='utf-8')) if path.exists() else {}
    for route in dict.fromkeys(r['route'] for r in runs):
        key = route.replace('/', '-')
        entry = {'results': [f.name for f in files]}
        for variant in dict.fromkeys(r['variant'] for r in runs if r['route'] == route):
            mine = [r for r in runs if r['route'] == route and r['variant'] == variant]
            n = len(mine)
            entry[variant.replace(' ', '')] = {
                'avg': round(sum(r['avgFps'] for r in mine) / n), 'low': round(sum(r['low1'] for r in mine) / n),
                'maxMs': round(sum(r['maxMs'] for r in mine) / n), 'over33': round(sum(r['over33'] for r in mine) / n, 1),
                'runs': n, 'workers': sorted({r.get('workers', 0) for r in mine}),
                'perRun': [{'avg': r['avgFps'], 'low': r['low1'], 'maxMs': r['maxMs'], 'over33': r['over33']} for r in mine],
                'frameMs': typical(mine)['frameMs'],
            }
        summary[key] = entry
        print(f'{key}: ' + ', '.join(f'{v} {e["avg"]} FPS / 1% low {e["low"]} / longest {e["maxMs"]} ms / >33 ms {e["over33"]}'
                                     for v, e in entry.items() if v != 'results'))
    path.write_text(json.dumps(summary), encoding='utf-8')


def main():
    args = sys.argv[1:]
    out = ROOT / 'nexus' / 'shots' / 'auto'
    if '--out' in args:
        i = args.index('--out'); out = Path(args[i + 1]); del args[i:i + 2]
    if args:
        files = [Path(a) for a in args]
    else:
        found = sorted((Path(game_dir()) / 'BepInEx' / 'NivalisPerformanceFix' / 'bench' / 'results').glob('*.json'))
        if not found:
            sys.exit('no results in the game bench folder')
        files = found[-1:]
    bench = files[0].parent.parent
    every = [r for f in files for r in json.loads(f.read_text(encoding='utf-8'))]
    out.mkdir(parents=True, exist_ok=True)
    stutters(every, files, out)
    runs = [r for r in every if r.get('shot')]
    if not runs:
        print('no photo spot runs in ' + ', '.join(f.name for f in files))
        return
    summary_path = out / 'photos.json'
    summary = json.loads(summary_path.read_text(encoding='utf-8')) if summary_path.exists() else {}
    for spot in dict.fromkeys(r['route'] for r in runs):
        key = spot.removeprefix('photo ').replace('/', '-')
        entry = {'results': [f.name for f in files]}
        for variant in dict.fromkeys(r['variant'] for r in runs if r['route'] == spot):
            mine = [r for r in runs if r['route'] == spot and r['variant'] == variant]
            name = variant.replace(' ', '')
            shot = bench / mine[0]['shot']
            if shot.exists():
                shutil.copyfile(shot, out / f'{key}-{name}.png')
            else:
                print(f'  missing screenshot {shot}')
            entry[name] = {
                'avg': round(sum(r['avgFps'] for r in mine) / len(mine)),
                'low': round(sum(r['low1'] for r in mine) / len(mine)),
                'runs': len(mine),
                'workers': sorted({r.get('workers', 0) for r in mine}),
                'shot': f'{key}-{name}.png',
            }
        summary[key] = entry  # a newer campaign replaces the spot's numbers and photos
        print(f'{key}: ' + ', '.join(f'{v} {e["avg"]} FPS / 1% low {e["low"]}' for v, e in entry.items() if v != 'results'))
    summary_path.write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(f'-> {out}')


if __name__ == '__main__':
    main()
