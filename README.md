![Nivalis Performance Fix](docs/banner.jpg)

*[Version française](README.fr.md)*

Nivalis Nights can struggle in busy places like the markets, and a powerful graphics card doesn't help much: the
game is held back by the CPU. This BepInEx mod takes load off the CPU where you can't see the difference.

In my tests, a crowded market went from about 90 to about 118 FPS, and stutters while walking around town mostly
disappeared. Results depend on your CPU and where you are. Visuals, gameplay and saves don't change.

## What it changes

- **Worker threads.** Sets the engine's worker thread count to suit your CPU. The game's default wastes time waking
  up too many threads.
- **Distant characters.** People far away or off-screen update their animation less often.
- **Background cameras.** The sky and snow-footprint cameras render every few frames instead of every frame.
- **NPC schedules and paths.** NPCs re-plan their day and re-read their path less often.
- **Memory cleanup.** The game's cleanup pass, which causes a short hitch, runs about 4 times less often.
- **Tracked Quests HUD.** If you use Hvizeu's *Tracked Quests HUD*, a hitch it causes when no quest is pinned is
  removed.

Each one can be turned off in the config.

## Install

1. Install [BepInEx 6 IL2CPP, build 788](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)
   (not the regular BepInEx 5): extract it into the game folder and launch the game once.
2. Download the [latest release](https://github.com/hoho92/Nivalis-Performance-Fix/releases/latest) and extract it
   into the game folder, next to `Nivalis Nights.exe`.
3. Launch the game, then **restart it once**. The thread setting is only read when the game starts.

After a game update or a Steam file check, the mod applies the thread setting again and asks for one more restart.

To check it's running, the BepInEx console shows `Nivalis Performance Fix 1.0.1: 6/6 optimizations active`.

## Configuration

`BepInEx/config/hoho92.nivalisperformancefix.cfg`, created on first launch. There is a master switch, one section
per change, and `[JobWorkers] Mode` (`Auto`, `Manual` or `Off`, where `Off` never touches the game files). The
defaults are the settings that worked best.

One tip unrelated to the mod: a mouse polling at 2000 Hz or more costs FPS in this game when you turn the camera.
1000 Hz is plenty.

## If the game updates

If an update changes the code the mod patches, that part turns itself off and the console names it. The game keeps
running normally.

## Uninstall

Delete `BepInEx/plugins/NivalisPerformanceFix`, then run Steam's *Verify integrity of game files* to restore the
original thread setting.

## About updates

I made this for my own playthrough and I'm sharing it in case it helps. I'll keep it working while I'm playing; once
I stop, updates may stop too. The code is MIT-licensed, so anyone is welcome to pick it up.

## For developers

The changes come from profiling the game with PIX and reading the disassembly. The mod patches
`Character.LateUpdateAll` (postfix), `ActiveJournalEntriesUi.Refresh` (prefix, only with Tracked Quests HUD) and
three call sites in native code, each found by byte signature.

Developer tools are off by default (`[Developer] Enabled = true`):

- **F8** toggles the whole mod, **F9** runs an A/B benchmark of `BenchTarget` (stand still somewhere busy), **F10**
  measures frame times for 20 s.
- A play log writes one line per minute plus every hitch to `BepInEx/NivalisPerformanceFix.playlog.log`; **F11**
  marks a hitch you felt. `python tools/analyze_playlog.py` summarises it.

Building needs the .NET 6 SDK and the game with BepInEx installed and run once:

```
dotnet build src/NivalisPerformanceFix -c Release -p:GameDir="C:\path\to\Nivalis Nights"
```

A Release build copies the DLL into the game (`-p:InstallToGame=false` to skip). `tools/package.ps1` builds the
release zip into `dist/`.

## License

[MIT](LICENSE) © hoho92
