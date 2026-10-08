"""Automatic presentation photos: one game launch per photo spot and variant, then photos.py.

    python photorun.py [spot ...] [--routes zone,zone] [--runs 2] [--hour 18] [--weather clear] [--rounds 2] [--live]

Spots = bench/photos entries ("zone" or "zone/name"; default: every spot); --routes adds recorded routes
(bench/routes) whose frame times feed the frame-time graphs ("none" as the only spot = routes only). Each launch
starts from the title screen (a save loaded from inside the game can keep the previous zone's venue card on screen)
and runs only one variant:
  * off: the whole mod switched off (target All) AND the original boot.config (boot.config.npf-backup), so the
    engine starts with its own job worker count, like a game without the mod;
  * on: the mod as installed (the off launch's mod start writes its job worker count back into boot.config).
--live leaves the gameplay clock running during the runs (set back to --hour before each run): NPC schedules,
crowds and restocks happen as in play. Rounds alternate the order (off, on / on, off ...).
Needs Steam running and [Developer] Enabled in the dev cfg. If a launch fails or the script is stopped, the request
is removed (the next normal game start must not run it) and the mod's boot.config is put back.
"""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

from gamedir import game_dir

TOOLS = Path(__file__).resolve().parent
LAUNCH_TIMEOUT = 15 * 60


def option(args, name, default):
    if name in args:
        i = args.index(name); value = args[i + 1]; del args[i:i + 2]
        return type(default)(value)
    return default


def running(exe_name):
    out = subprocess.run(['tasklist', '/FI', f'IMAGENAME eq {exe_name}'], capture_output=True, text=True).stdout
    return exe_name.lower() in out.lower()


def workers_line(boot):
    return next((l for l in boot.read_text(encoding='utf-8').splitlines() if l.startswith('job-worker-count=')), None)


def launch(game, exe, bench, request):
    """One game launch running the request; returns its results file, or raises (request removed)."""
    path = bench / 'request.json'
    path.write_text(json.dumps(request), encoding='utf-8')
    try:
        before = set((bench / 'results').glob('*.json'))
        subprocess.Popen([str(exe)], cwd=str(game))
        start = time.time()
        time.sleep(10)
        while running(exe.name):
            if time.time() - start > LAUNCH_TIMEOUT:
                raise RuntimeError('game still running after the timeout (close it yourself)')
            time.sleep(3)
        new = sorted(set((bench / 'results').glob('*.json')) - before)
        if not new:
            raise RuntimeError('the launch wrote no results (see BepInEx/LogOutput.log)')
        return new[-1]
    finally:
        path.unlink(missing_ok=True)  # done: the game renamed it; failed: never left for a normal start


def main():
    args = sys.argv[1:]
    runs = option(args, '--runs', 2)
    hour = option(args, '--hour', 18.0)
    weather = option(args, '--weather', 'clear')
    rounds = option(args, '--rounds', 2)
    live = '--live' in args
    if live: args.remove('--live')
    routes = [r for r in option(args, '--routes', '').split(',') if r]
    game = Path(game_dir())
    exe = game / 'Nivalis Nights.exe'
    bench = game / 'BepInEx' / 'NivalisPerformanceFix' / 'bench'
    data = next(game.glob('*_Data'))
    boot, original = data / 'boot.config', data / 'boot.config.npf-backup'
    if running(exe.name):
        sys.exit('the game is already running')
    if not running('steam.exe'):
        sys.exit('Steam is not running')
    if not original.exists():
        sys.exit(f'{original.name} not found: no original boot.config to test the off variant with')
    spots = args or sorted(f'{p.parent.name}/{p.stem}' for p in (bench / 'photos').glob('*/*.json'))
    spots = [s for s in spots if s != 'none']
    if not spots and not routes:
        sys.exit('no photo spot (record one in game with the PhotoKey, Insert by default)')
    items = [('photos', s) for s in spots] + [('routes', r) for r in routes]

    results = []
    mod_boot = boot.read_bytes()
    try:
        for rnd in range(rounds):
            for kind, spot in items:
                for variant in (('off', 'on') if rnd % 2 == 0 else ('on', 'off')):
                    if variant == 'off':
                        shutil.copyfile(original, boot)
                    elif workers_line(boot) is None:
                        print('  warning: boot.config has no job worker count before the on launch')
                    print(f'{spot}: {variant} launch ({runs} run(s), {workers_line(boot) or "engine default workers"})')
                    r = launch(game, exe, bench, {
                        'routes': [spot] if kind == 'routes' else [], 'photos': [spot] if kind == 'photos' else [],
                        'runs': runs, 'target': 'All', 'only': variant,
                        'hour': hour, 'weather': weather, 'quit': True, 'live': live})
                    results.append(r)
                    for x in json.loads(r.read_text(encoding='utf-8')):
                        print(f'  run {x["run"]} ({x["variant"]}, {x.get("workers")} workers): '
                              f'{x["avgFps"]} FPS, 1% low {x["low1"]}')
    except (RuntimeError, KeyboardInterrupt) as e:
        sys.exit(f'stopped: {e or "interrupted"}')
    finally:
        if workers_line(boot) is None and not running(exe.name):
            boot.write_bytes(mod_boot)  # the mod did not write its count back (failed launch, Mode Off): restore it
    subprocess.run([sys.executable, str(TOOLS / 'photos.py'), *map(str, results)], check=True)


if __name__ == '__main__':
    main()
