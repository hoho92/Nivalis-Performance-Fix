"""Summarise NivalisPerformanceFix.playlog.log (developer play log) to find what to optimize next.

usage: python analyze_playlog.py [path/to/NivalisPerformanceFix.playlog.log] [--since YYYY-MM-DD] [--cell 25]

Sections: per-scene averages, worst minutes, hitch hot spots (position grid), hitch size vs. characters on
screen, and every player MARK with the hitches around it.
"""
import argparse, collections, datetime as dt, os, statistics

DEFAULT = r'C:\Games\Steam\steamapps\common\Nivalis Nights\BepInEx\NivalisPerformanceFix.playlog.log'

ap = argparse.ArgumentParser()
ap.add_argument('path', nargs='?', default=DEFAULT)
ap.add_argument('--since', help='only records from this date (YYYY-MM-DD)')
ap.add_argument('--cell', type=float, default=25.0, help='hot-spot grid size in metres')
a = ap.parse_args()


def parse(line):
    parts = line.split()
    if len(parts) < 3: return None
    try: t = dt.datetime.strptime(parts[0] + ' ' + parts[1], '%Y-%m-%d %H:%M:%S')
    except ValueError: return None
    rec = {'t': t, 'type': parts[2]}
    for kv in parts[3:]:
        if '=' in kv:
            k, v = kv.split('=', 1)
            try: rec[k] = float(v) if k not in ('scene', 'pos', 'version') else v
            except ValueError: rec[k] = v
    return rec


recs = [r for r in (parse(l) for l in open(a.path, encoding='utf-8', errors='replace')) if r]
if a.since:
    since = dt.datetime.strptime(a.since, '%Y-%m-%d')
    recs = [r for r in recs if r['t'] >= since]
playing = lambda r: r.get('paused', 0) == 0 and r.get('mod', 1) == 1
mins = [r for r in recs if r['type'] == 'MIN' and playing(r)]
spikes = [r for r in recs if r['type'] == 'SPIKE' and playing(r)]
marks = [r for r in recs if r['type'] == 'MARK']
sessions = [r for r in recs if r['type'] == 'SESSION']

print(f'{len(sessions)} sessions, {sum(r.get("secs", 0) for r in mins) / 60:.0f} min of play recorded, '
      f'{len(spikes)} hitches, {len(marks)} player marks')
if not mins:
    raise SystemExit('no MIN records yet: play a few minutes with [Developer] Enabled and PlayLog on')

print('\n== per scene (unpaused minutes)')
print('  scene                      min   fps  low1   p99  >20ms/min >30ms/min  hitches/min  gc share')
by_scene = collections.defaultdict(list)
for r in mins: by_scene[r.get('scene', '?')].append(r)
for scene, rs in sorted(by_scene.items(), key=lambda kv: -len(kv[1])):
    m = sum(r['secs'] for r in rs) / 60
    sp = [s for s in spikes if s.get('scene') == scene]
    gc = sum(s.get('gc', 0) for s in sp) / len(sp) if sp else 0
    print(f'  {scene[:26]:26} {m:5.0f} {statistics.mean(r["fps"] for r in rs):5.1f} {statistics.mean(r["low1"] for r in rs):5.1f} '
          f'{statistics.mean(r["p99"] for r in rs):5.1f} {sum(r["over20"] for r in rs) / m:9.1f} {sum(r["over30"] for r in rs) / m:9.1f} '
          f'{len(sp) / m:12.1f} {100 * gc:8.0f}%')

print('\n== 10 worst minutes (by 1% low)')
for r in sorted(mins, key=lambda r: r['low1'])[:10]:
    print(f'  {r["t"]:%m-%d %H:%M}  fps {r["fps"]:5.1f}  low1 {r["low1"]:5.1f}  p99 {r["p99"]:5.1f}  max {r["max"]:6.1f}  '
          f'>30ms {r["over30"]:3.0f}  {r.get("scene")} pos {r.get("pos")} chars {r.get("chars"):.0f}/{r.get("visible"):.0f} heap {r.get("heap_mb"):.0f} MB')


def cell(pos):
    try:
        x, y, z = (float(v) for v in pos.split(','))
        return (round(x / a.cell) * a.cell, round(z / a.cell) * a.cell)
    except Exception:
        return None


print(f'\n== hitch hot spots ({a.cell:.0f} m cells, hitches > 25 ms, excluding GC)')
hot = collections.defaultdict(list)
for s in spikes:
    if s.get('gc', 0) == 0 and s.get('ms', 0) > 25:
        c = cell(s.get('pos', ''))
        if c: hot[(s.get('scene'), c)].append(s)
for (scene, c), ss in sorted(hot.items(), key=lambda kv: -len(kv[1]))[:12]:
    print(f'  {len(ss):4d} hitches  {scene} around x={c[0]:.0f} z={c[1]:.0f}  worst {max(s["ms"] for s in ss):.0f} ms  '
          f'avg visible chars {statistics.mean(s.get("visible", 0) for s in ss):.0f}')

print('\n== hitch size vs. characters on screen')
buckets = collections.defaultdict(list)
for s in spikes:
    v = s.get('visible', -1)
    if v >= 0: buckets[int(v // 10) * 10].append(s['ms'])
for b in sorted(buckets):
    xs = buckets[b]
    print(f'  {b:3d}-{b + 9:<3d} on screen: {len(xs):5d} hitches, median {statistics.median(xs):5.1f} ms, worst {max(xs):6.1f} ms')

print('\n== player marks (with hitches in the 5 s before)')
for m in marks:
    near = [s for s in spikes if dt.timedelta(seconds=-5) <= s['t'] - m['t'] <= dt.timedelta(seconds=1)]
    print(f'  {m["t"]:%m-%d %H:%M:%S}  {m.get("scene")} pos {m.get("pos")}  last 5 s: {m.get("last5s_fps")} fps, '
          f'max {m.get("last5s_max")} ms  chars {m.get("chars"):.0f}/{m.get("visible"):.0f}  '
          f'hitches: {", ".join(f"{s["ms"]:.0f} ms" + (" GC" if s.get("gc") else "") for s in near) or "none logged"}')
