# Changelog

## 1.0.4 — 2026-10-04

- NPC spawn spreading (new): every in-game hour, all background NPC spawn points of a zone fired within one second
  (frames of 25-50 ms, up to 200 ms). Their hourly appearance / removal is now spread over 5 s
  (`[Spawns] SpreadSeconds`). Arrivals in a zone are untouched: they already happen during the loading screen.
- Garbage collector: a full cleanup now also runs during zone loading screens (`[GarbageCollector] CollectOnLoading`,
  ~150 ms inside a ~1.5 s loading freeze). In a 10-zone test, no GC hitch was left during play, and the managed heap
  stayed around 800 MB instead of growing to 1.5-2 GB.
- Compatibility: tested with Nivalis Unofficial Patch and Nivalis Config Manager; detected mods are listed at startup.
- Works with Nivalis Config Manager (in-game settings menu, F1): every setting applies immediately, except job worker
  threads, which are written to boot.config right away and need a game restart.
- Developer tools moved to their own file (`hoho92.nivalisperformancefix.dev.cfg`) so they stay out of the
  in-game menu.

## 1.0.3 — 2026-10-03

- Animation LOD: after an unexpected error it now turns itself off only until the game is restarted. Before, it
  switched itself off in the config file, so it stayed off for good even once the cause was gone.

## 1.0.2 — 2026-10-03

- Light probe walk limit: Unity's per-frame light probe search could loop for thousands of steps in some areas
  (13_Stacks after arriving through a zone transition: ~40 FPS instead of ~140). Now capped per frame; lighting
  unchanged.

## 1.0.1 — 2026-10-03

- Garbage collector frequency: back to the game's value while paused and when quitting. During long pauses the
  game kept allocating with no GC (memory grew ~80 MB/min); one quit after a 28 min pause hung.
- Developer play log: numbers always written with a decimal point (some were written with a comma).

## 1.0.0 — 2026-10-03

First release, for the game build of 2026-10-02.

- Job worker threads set automatically from the CPU in `boot.config` (Auto / Manual / Off).
- Animation LOD for far and off-screen characters.
- Offscreen camera throttle (sky and snow footprints).
- Agent schedule throttle.
- Navigation path throttle.
- Garbage collector frequency.
- Compatibility with Hvizeu's Tracked Quests HUD (no hitch when no quest is pinned).
- Startup summary; each optimization disables itself if a game update changed the code it patches.
- Developer tools (off by default): on/off toggle, A/B benchmark, frame-time measurement, play log with hitch marks.
