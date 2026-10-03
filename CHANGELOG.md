# Changelog

## Unreleased

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
