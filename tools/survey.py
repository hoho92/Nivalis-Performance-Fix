"""Read the scene surveys written by the developer tools (F12, BepInEx/NivalisPerformanceFix/survey-*.json).

usage:
  python tools/survey.py <survey.json>                 summary of one scene
  python tools/survey.py <a.json> <b.json> ...         compare scenes (per-frame script types x scenes)
  python tools/survey.py --latest [N]                  the N most recent surveys of the game folder (default: all scenes, newest each)
options:
  --top N        rows to show (default 40)
  --all          include types without Update/LateUpdate/FixedUpdate
"""
import argparse
import glob
import json
import os

from gamedir import game_dir


def load(path):
    with open(path, encoding='utf-8') as f:
        s = json.load(f)
    s['_path'] = path
    return s


def active(item):
    """Instances that actually run: enabled ones when the type can be disabled, else all."""
    return item.get('enabled', item['count'])


def items(survey, include_all):
    comp = survey.get('components', {})
    return [i for i in comp.get('items', []) if include_all or i.get('perFrame')]


def summary(s, top, include_all):
    m = s.get('meta', {})
    print(f"{m.get('scene')}  {m.get('time')}  mod {m.get('modVersion')} {'on' if m.get('modEnabled') else 'off'}  "
          f"camera {m.get('cameraPos')}")
    print(f"  characters {m.get('characters')} ({m.get('charactersVisible')} visible), heap {m.get('heapMb')} MB, "
          f"quality {m.get('qualityLevel')}, shadows {m.get('shadowDistance')} m x{m.get('shadowCascades')}, "
          f"lightmaps {m.get('lightmaps')}")
    lights, probes = s.get('lights', {}), s.get('reflectionProbes', {})
    print(f"  lights {lights.get('total')} ({lights.get('enabled')} enabled, {lights.get('withShadows')} with shadows), "
          f"reflection probes {probes.get('total')} ({probes.get('realtime')} realtime, "
          f"{probes.get('refreshEveryFrame')} every frame)")
    for c in s.get('cameras', {}).get('items', []):
        if c.get('enabled'):
            print(f"  camera {c['name']}: far {c.get('far')}, {c.get('targetTexture', 'screen')}, "
                  f"{c.get('layerCullDistances')} layer cull distances")
    comp = s.get('components', {})
    print(f"  components {comp.get('instances')} in {comp.get('types')} types, "
          f"{comp.get('perFrameInstancesEnabled')} running a per-frame method")
    rows = sorted(items(s, include_all), key=active, reverse=True)[:top]
    print(f"\n  {'running':>7} {'count':>7}  frame  type")
    for i in rows:
        print(f"  {active(i):7} {i['count']:7}  {i.get('perFrame', ''):5}  {i['type']}")


def compare(surveys, top, include_all):
    names = [s.get('meta', {}).get('scene', os.path.basename(s['_path'])) for s in surveys]
    table = {}
    for k, s in enumerate(surveys):
        for i in items(s, include_all):
            row = table.setdefault(i['type'], [0] * len(surveys) + [i.get('perFrame', '')])
            row[k] = active(i)
    rows = sorted(table.items(), key=lambda kv: max(kv[1][:-1]), reverse=True)[:top]
    short = [n[:12] for n in names]
    print('running instances per scene (enabled ones when the type can be disabled)\n')
    print(' '.join(f'{n:>12}' for n in short) + '  frame  type')
    for t, row in rows:
        print(' '.join(f'{v:12}' for v in row[:-1]) + f'  {row[-1]:5}  {t}')
    print('\n' + ' '.join(f"{s.get('meta', {}).get('characters', '?'):>12}" for s in surveys) + '         characters')


def latest():
    newest = {}
    for p in sorted(glob.glob(os.path.join(game_dir(), 'BepInEx', 'NivalisPerformanceFix', 'survey-*.json'))):
        scene = os.path.basename(p)[len('survey-'):-len('-yyyymmdd-hhmmss.json')]
        newest[scene] = p  # sorted by name: the last one per scene is the newest
    return sorted(newest.values(), key=os.path.getmtime)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('files', nargs='*')
    ap.add_argument('--latest', nargs='?', type=int, const=0)
    ap.add_argument('--top', type=int, default=40)
    ap.add_argument('--all', action='store_true')
    a = ap.parse_args()
    files = a.files
    if a.latest is not None:
        files = latest()[-a.latest:] if a.latest else latest()
    if not files:
        ap.error('no survey given (or none found with --latest)')
    surveys = [load(f) for f in files]
    if len(surveys) == 1:
        summary(surveys[0], a.top, a.all)
    else:
        compare(surveys, a.top, a.all)


if __name__ == '__main__':
    main()
