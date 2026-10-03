# Nivalis Performance Fix

*[Version française](README.fr.md)*

A BepInEx mod that removes CPU bottlenecks and stutters in **Nivalis Nights**. Nivalis Nights is limited by the
CPU in busy places (markets, crowds), not by the graphics card. This mod targets what the game's main thread
actually spends its time on, found by profiling the game (PIX captures and disassembly).

Measured on a Ryzen 7 9800X3D, busy market, standing still (automatic A/B benchmark, 3 rounds, same session):

| | average FPS | 1% low FPS |
|---|---|---|
| Optimizations off (job threads already at 6) | 89.4 | 58.8 |
| **Optimizations on** | **118.1** | **85.5** |

That is +32% average and +45% 1% lows, on top of the job thread setting (+18% on its own: 77.5 → 91.1 FPS).

While walking around town, stutters over 30 ms went from 27 per 90 s to 0 (together with a 1000 Hz mouse, see below).
Your gains depend on your CPU and where you are in the game.

## What it does

| Optimization | What it changes | Measured gain |
|---|---|---|
| **Job worker threads** | Sets Unity's worker thread count from your CPU (physical cores − 2, between 2 and 8) in `boot.config`. The game wakes its workers thousands of times per second; too many workers cost more than they help. | +18–21% FPS |
| **Animation LOD** | Far and off-screen characters update their animation every few frames instead of every frame (closest characters unchanged). | +7% FPS, 1% lows +28% |
| **Offscreen camera throttle** | The sky and snow-footprint helper cameras render every 2 / 4 frames instead of every frame. No visible difference. | +8% FPS, 1% lows +47% |
| **Agent schedule throttle** | Simulated NPCs re-check their schedule every 8 frames instead of every frame. | +2.5% FPS |
| **Navigation path throttle** | Walking NPCs re-read their path every 4 frames instead of copying it every frame. | +2.3% FPS, 1% lows +22% |
| **Garbage collector frequency** | The garbage collector runs ~4× less often, so its ~20 ms hitch happens every ~18 s instead of every ~4 s. Uses a bit more memory. | 4× fewer GC hitches |
| **Tracked Quests HUD compatibility** | Only with Hvizeu's *Tracked Quests HUD* mod: skips the quest HUD rebuild when no quest is pinned (it caused a 40–60 ms hitch every ~20 s for an empty HUD). | removes those hitches |

Nothing changes gameplay or saves. Every optimization can be switched off in the config.

## Requirements

- Nivalis Nights (Steam, Windows)
- [BepInEx 6 IL2CPP, bleeding edge](https://builds.bepinex.dev/projects/bepinex_be) (tested with build 788), run the game once after installing it

## Installation

1. Extract the archive into the game folder (the one with `Nivalis Nights.exe`), so you get
   `BepInEx/plugins/NivalisPerformanceFix/NivalisPerformanceFix.dll`.
2. Start the game. The BepInEx console shows:
   `Nivalis Performance Fix 1.0.0: 6/6 optimizations active`
3. **The first time only**: the console asks you to **restart the game**. The job thread setting is read when the
   engine starts, so it only applies from the next launch.

After a game update or a Steam "verify files", `boot.config` is restored. The mod writes the setting again and asks
for one more restart.

## Configuration

`BepInEx/config/hoho92.nivalisperformancefix.cfg` (created at first launch):

- `[General] Enabled`: master switch.
- One section per optimization with `Enabled` and its settings (`[Animation]`, `[Agents]`, `[Navigation]`,
  `[Cameras]`, `[GarbageCollector]`, `[QuestHud]`).
- `[JobWorkers] Mode`: `Auto` (default), `Manual` (uses `Count`) or `Off` (never touches `boot.config`).

The defaults are the values that measured best.

## Tips

- **Mouse polling rate**: at 2000 Hz and above, Unity processes every mouse message on the main thread. Turning the
  camera at 1000 Hz instead of 2000 Hz gave +6–9% FPS. 1000 Hz is plenty.
- The Steam overlay has no measurable cost.

## Compatibility

- **Tracked Quests HUD** (Hvizeu): supported, see above. Load order does not matter.
- Other mods: no known conflicts. The mod patches `Character.LateUpdateAll` (postfix),
  `ActiveJournalEntriesUi.Refresh` (prefix, only with Tracked Quests HUD) and three call sites in the game code.
- If the game updates and a patch no longer matches the game code, that optimization **disables itself** and the
  console says which one: `... disabled: code signature found 0 times (game update?)`. The game keeps working;
  check for a mod update.

## Uninstall

Delete `BepInEx/plugins/NivalisPerformanceFix`. To restore the original job thread setting, either set
`[JobWorkers] Mode = Off` first and replace `Nivalis Nights_Data/boot.config` with the
`boot.config.npf-backup` next to it, or use Steam's *Verify integrity of game files*.

## For developers

Developer tools are off by default (`[Developer] Enabled = true` to use them):

- **F8** switches the whole mod on/off; **F9** runs an automatic A/B benchmark of `BenchTarget` (stand still in a
  busy place); **F10** measures frame times for 20 s.
- **Play log**: one line per minute with frame-time stats and context, every hitch, and **F11** to mark a hitch you
  felt. Written to `BepInEx/NivalisPerformanceFix.playlog.log` (local only). Summarise it with
  `python tools/analyze_playlog.py`.

### Building

Requires the .NET 6 SDK and the game with BepInEx installed and run once (for the interop assemblies).

```
dotnet build src/NivalisPerformanceFix -c Release -p:GameDir="C:\path\to\Nivalis Nights"
```

A Release build copies the DLL into the game (`-p:InstallToGame=false` to skip). `tools/package.ps1` builds the
release archive into `dist/`.

## License

[MIT](LICENSE) © hoho92
