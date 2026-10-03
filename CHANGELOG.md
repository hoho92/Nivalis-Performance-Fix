# Changelog

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
