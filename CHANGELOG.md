# Changelog

## 1.0.7 — 2026-10-09

Compatibility with the game's Update #4 (2026-10-09).

- Menu lists: the game removed a field the mod wrote when it rebuilds a list; every list update logged an error
  ("Method not found: ItemListUI.set_isUpdating"). Fixed.
- Navigation path throttle: the game's walking code changed, so the feature turned itself off. It is back on: the
  game still reads every walking NPC's path each frame.
- Vendor stock updates (`[Economy]`): the game now skips the unused check itself. The console shows "not needed" on
  the new game version; older versions are still patched.

## 1.0.6 — 2026-10-08

Menus without freezes, fewer stutters in play, faster launch, and two game bugs fixed. Menu times below are the
slowest frame of the action, measured by an automated test (same save, same actions) without / with the mod.

Menus:
- In-game menu, Contacts tab (new, `[ContactRows]`): cells are prepared while the game is paused and only the rows
  around the view are active. First opening 133 -> 53 ms, reopening 112 -> 28 ms, switching to the tab 87 -> 17 ms,
  "Staff" filter 312 -> 14 ms.
- In-game menu, Journal and Bag (new, `[LazyLists]`): only the visible rows are active and the rows are prepared
  during loading. Journal first opening 148 -> 48 ms, reopening 75 -> 44 ms; Bag 109 -> 48 ms and 72 -> 37 ms.
  Closing the menu 25-34 -> 13-19 ms.
- Business window, Reviews tab (new, `[ReviewRows]`): with 1,016 reviews, opening the tab 324 -> 107 ms, period
  "this month" 1,891 -> 322 ms (20 ms the second time), scrolling a whole month with the wheel no longer stutters.
- Fish database (new, `[FishDatabaseRows]`): rows are kept instead of being destroyed and rebuilt at each opening
  (56-60 -> 20-27 ms).
- Recipes (new, `[RecipeDetails]`): the details panel is no longer switched off and on at each click; ingredient and
  equipment rows are prepared in advance (reopening 50 -> 29 ms).
- Scrollbars (new, `[MenuScrollbars]`): the game's scrollbars hide without resizing their list, which laid the list
  out several times (skills tab animation, recipes...).
- Pause menu with many saves (new, `[SaveRowsParking]`): the rows of the closed Save and Load windows are kept out of
  the pause menu, whose fade walked them every frame.
- Save and Load menus: first opening in game 1,561 / 2,631 -> 92 / 84 ms (1 s after pausing). Rows are prepared in
  this order during loading: in-game menu, contacts, shops, then saves (the rest while paused). A reopened Load or
  Save window no longer shows a save still selected.
- Shops: first opening of a 281-item shop 1,300 -> 161 ms, closing 156 -> 36 ms. The rows to prepare are taken from
  the largest vendor of the save instead of being learned at the first opening.

Stutters in play:
- Dialogues (new, `[DialogueVoices]`): the voice of the next lines is loaded in the background (12-40 ms freeze at
  each line before).
- Smoke and steam (new, `[ParticleCatchUp]`): looping effects resume where they stopped when they come back into
  view, instead of simulating the missed time in one frame (50-65 ms hitch when turning the camera).
- Vendor stock updates (new, `[Economy]`): a check whose result the game never uses is skipped; the update a few
  times per in-game day went from 98 to 34 ms.
- HUD (new, `[HudRedraws]`): the compass letters and the venue opening hours are no longer redrawn every frame
  (11,337 -> 3,919 HUD redraws in 15 s); the hidden autosave icon no longer animates.

Loading:
- Faster loading (new, `[FastLoading]`): Unity uploads textures faster during the launch and loading screens. First
  frame 20.0-20.3 -> 19.1 s, title screen usable about 1 s earlier.
- The mod's searches for the menus during loading screens do 60% less work.

Game bugs fixed:
- Stuck NPCs (new, `[StuckNpcs]`): an NPC that ended up off the walkable area never moved again and Unity logged a
  warning about 30 times per second (2,843 warnings in 80 s in Central Canyon). It is now put back on the nearest
  walkable point (170 warnings, then none).

Fixed during the 1.0.6 tests (automated tests with and without the mod, each setting off in turn, and with Nivalis
Unofficial Patch 0.3.13 and Tracked Quests HUD 1.1.10):
- Shops: after a big shop scrolled down, a small shop (e.g. the butcher's 9 offers) could open with an empty list.
- Shops: after a filter or sorting change with a controller, the details panel could show another item than the
  selected row. This affected the game's own sorting as well as Unofficial Patch's Decorations filter and price
  sorting.
- Recipes: the tab could open empty with a "Recipe title" placeholder (the category tiles must not be prepared in
  advance; the setting is corrected automatically).

Other:
- Tracked Quests HUD: version 1.1 and later fix the hitch themselves; the mod's patch is then not installed
  ("not needed" in the log) and stays for 1.0.
- Turning a feature off in game now undoes what it changed (rows shown again, patches removed).
- Developer tools stay off for players; the startup timeline is no longer printed unless they are on.

## 1.0.5 — 2026-10-06

For the game's Update #3 (build of 2026-10-06).

- Save and Load menus (new, `[SaveMenus]`): with many saves each opening froze the game. Measured with 191 saves,
  same actions without / with the mod: title-screen Load menu 1335 -> ~150 ms; in game, Save menu 1561 -> 121 ms
  (reopening 536 -> 115 ms), Load menu 1531 -> 124 ms (reopening 544 -> 106 ms). Only the rows around the view are
  active (the others keep their place, so the scrollbar does not move), the list is not rebuilt when the saves did
  not change, rows and screenshots are prepared in the background after a scene loads. Saves are read straight from
  the game's list (Il2CppInterop returned a wrong save header, so the Save window got 1 prepared row instead of 190).
- Shop windows (new, `[ShopRows]`): measured in a 127-item shop, same actions without / with the mod: first opening
  425 -> 118 ms, category change 130-320 -> under 40 ms, buying ~95 -> under 40 ms. Only the rows around the view are
  active; a filter click (which changed two toggles, so two full rebuilds) now rebuilds once, and not at all when
  nothing changed (e.g. Delete held in an empty search field); rows are prepared in the background (their number
  grows by itself if a shop needs more); the shop scrollbars no longer lay the list out 2-3 times to decide whether
  to show themselves (`SimpleScrollbars`). Filters, search, sorting, buying and gamepad navigation unchanged.
- Crash when quitting (new, `[ExitCrash]`): every quit ended in an access violation in UnityPlayer.dll, also without
  any mod. Unity's shutdown destroyed the job system, then the texture upload manager waited on one of its jobs
  through a null queue. That wait is now skipped when the job system is gone (only at shutdown).
- Lens flares (new, `[LensFlares]`): the post-processing asked twice per frame whether any flare is visible, each
  time preparing every flare source of the area (222 in Metro Hub); the second answer is reused.
- Enum flag checks (new, `[EnumFlags]`): the game's `Enum.HasFlag` calls (110 of 136 call sites, e.g. NPC task
  checks) are rewritten into a plain bit test: no more two boxed objects per call, ~10,000 garbage objects per
  second less in Metro Hub. Same results.
- Player keyboard shortcuts (new, `[PlayerGui]`): the shortcut handler ran an unused IMGUI layout pass every frame
  (garbage each frame); it is skipped, keys still work.
- Navigation paths: walking NPCs' path reads use one shared path object instead of a new one per read (~2,500
  garbage objects per second less in Metro Hub).
- The mod's own per-frame code no longer creates garbage (Unity values are read through the compiled methods,
  not through Il2CppInterop, which boxed every returned value).
- Garbage collector: the full collection at the start of loading screens is removed. Unity already does one in the
  same loading, so it only added 120-180 ms.
- Paused characters (new, game bug fix): while the game is paused, Character.LateUpdateAll stops but
  Character.UpdateAll keeps counting down each character's animation step, so every far character on screen was
  re-animated with a zero time step every frame. The step is now held while paused (`[PausedCharacters]`).
  Metro Hub pause menu: 146.9 -> 171.1 FPS.
- Far character details (new): head look-at (FinalIK) and lip-sync (SALSA) are switched off for off-screen
  characters and beyond 20 m / 25 m (`[CharacterDetails]`). They ran every frame for every character, ~35 ms per
  second of main thread in Metro Hub. Measured +2-3% FPS there.
- Light probe walk limit removed: the 2026-10-06 game update fixed the cause (light probes of unloaded scenes kept
  in memory). Tested in The Stacks after a zone transition: 132 FPS without the patch (41 before the game fix).
- Snow-footprint camera: no longer throttled by the mod. Since the 2026-10-06 game update the game renders it only
  while it snows (and at a lower rate); toggling it too could re-enable it when it does not snow.
- Sky camera throttle: the camera is only switched back on if the mod switched it off; when the game turns it off,
  it stays off (and it is left alone when the setting is off).
- Robustness after game updates: the character list and the save list are read with offsets taken from the game's
  own classes (no more fixed offsets: a changed layout now switches the feature off with a warning instead of
  reading the wrong memory); code patches stop with an error if the code cannot be made writable; a feature that
  keeps failing is logged once and switched off for the session after 30 errors instead of filling the log.
- boot.config (job worker threads) is written to a temporary file and then swapped in, never left half-written.
- Developer tools: the A/B benchmark no longer saves its on/off phases to the config file (quitting during a
  benchmark left a feature off); dev log files are moved to `*.old` past 20 MB.
- Nivalis Unofficial Patch 0.3.13: works together with this mod (it switches off its own overlapping performance
  options). Its `LightingRefreshCache` option measured about -9% FPS with this mod (busy market, 3 x 20 s per
  setting); the startup log now says so when it is on. Nothing is changed in that mod's settings.
- Building: the game folder comes from a local `GameDir.props` (not in git) or `-p:GameDir`, see README.

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
