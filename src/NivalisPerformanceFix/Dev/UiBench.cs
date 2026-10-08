using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Economy;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.Locale.UI;
using Nivalis.UI;
using Nivalis.UI.InGameMenu;
using NivalisPerformanceFix.Features;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using PlayerInputManager = Nivalis.PlayerInputManager;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: UI test run, the game's windows opened, browsed and photographed by a script so a build, the mod
/// on / off, another mod installed or a new game version can be compared window by window.
/// bench/ui.json present at startup: from the title screen, loads "save", waits "settle" seconds, then plays the
/// steps; each step is measured (frames until the next step: first frames, max, frames over 25 ms), Unity and
/// BepInEx errors logged during it are counted, and the window on top is noted (a window that no longer opens
/// after a game update shows there). Writes bench/ui/&lt;date&gt;-&lt;label&gt;.md + .json, screenshots in
/// bench/ui/&lt;date&gt;-&lt;label&gt;/, renames the request *.done and quits if asked.
/// Request: {"save":"name", "master":true, "label":"on", "quit":true, "settle":8, "steps":[...]}; master false =
/// whole mod off for the run (in memory: compare with a second launch). Steps ("do"):
///   menu {tab: Inventory|Journal|Characters|Skills|Achievements|Recipes|FishDatabase|Venue} — in-game menu tab;
///   pause; save; load (pause menu, then its Save / Load button); shop {vendor: name part, else first};
///   venue {tab: Overview|Inventory|Reviews|Staff, venue: name part, else the one with most reviews};
///   window {type: game window class, method: opener (default Open, else Show), flag: its bool arguments, venue};
///   click {name, index} — Button / Toggle named so in the top window; nav {dir: up|down|left|right, count, every}
///   — gamepad moves (UI move events, every N frames; start "list" = from the first row of the longest list),
///   checks the selection stays visible; scroll {to: 0 bottom ..
///   1 top, over: seconds, name}; shot {name}; close; closeAll; wait; gamepad / mouse (input scheme). Every step: "s" = seconds measured (default
///   1.5). Wait and shot steps also check the top window for template texts ("placeholders": more of them).
/// </summary>
internal sealed partial class UiBench
{
    private const float HitchMs = 25f, TitleWaitSeconds = 3f, LoadTimeout = 120f;

    /// <summary>
    /// Template texts of the game's prefabs: shown, the window was not filled (recipes tab opened with
    /// "Recipe title" when its category tiles were made in advance, found by the user 2026-10-08).
    /// </summary>
    private static readonly string[] DefaultPlaceholders =
        { "Recipe title", "Item name", "Item description", "Short description", "Lorem ipsum", "New Text", "Sample text", "TODO" };

    private sealed class Request
    {
        public string Save { get; set; } = "";
        public bool Master { get; set; } = true;
        public string Label { get; set; } = "";
        public bool Quit { get; set; }
        public float Settle { get; set; } = 8;
        /// <summary>Feature sections switched off for the run (in memory), e.g. ["ContactRows","LazyLists"].</summary>
        public List<string> Disable { get; set; } = new();
        /// <summary>Game methods timed at every step ("Type.Method", see MethodTimers).</summary>
        public List<string> Time { get; set; } = new();
        /// <summary>More texts that must never be shown (added to DefaultPlaceholders).</summary>
        public List<string> Placeholders { get; set; } = new();
        public List<StepRequest> Steps { get; set; } = new();
        /// <summary>Steps drawn at random after the given ones (see UiMonkey).</summary>
        public MonkeyRequest Monkey { get; set; }
    }

    private sealed class StepRequest
    {
        public string Do { get; set; } = "wait";
        public string Tab { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Dir { get; set; } = "down";
        public int Count { get; set; } = 1;
        public int Every { get; set; } = 4;
        public int Index { get; set; }
        /// <summary>click: the first text shown in the button / toggle (a row's own text): among the ones named so,
        /// the one showing it is pressed (lists that keep only the rows near the view active number them differently).</summary>
        public string Text { get; set; } = "";
        /// <summary>nav: "list" = first select the first visible row of the window's longest list (else the
        /// selection the game made, e.g. a tab).</summary>
        public string Start { get; set; } = "";
        /// <summary>window: the game's window class (e.g. "MapUI"), its opener ("Open", else "Show") and the value
        /// given to its bool parameters.</summary>
        public string Type { get; set; } = "";
        public string Method { get; set; } = "";
        public bool Flag { get; set; }
        public float To { get; set; } = -1;
        public float Over { get; set; } = 1;
        public float S { get; set; } = -1;
    }

    private sealed class StepResult
    {
        public int N { get; set; }
        public string Step { get; set; }
        public double FirstMs { get; set; }
        public double MaxMs { get; set; }
        public int Over25 { get; set; }
        public int Frames { get; set; }
        public double AvgMs { get; set; }
        public int Errors { get; set; }
        public string Top { get; set; }
        public string Note { get; set; }
        /// <summary>Texts shown in the top window at the step's end (compared between runs).</summary>
        public List<string> Texts { get; set; }
        /// <summary>Shown texts still equal to a prefab's template text.</summary>
        public List<string> Templates { get; set; }
        /// <summary>Lists of the top window: "name:visible rows".</summary>
        public string Lists { get; set; }
        public string Shot { get; set; }
        /// <summary>The selected object at the step's end (gamepad focus), "-" if none.</summary>
        public string Selected { get; set; }
    }

    private static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    private readonly DevTools dev;
    private string folder, requestFile, runName, shotDir;
    private Request request;
    private bool masterSaved;
    private readonly List<(BepInEx.Configuration.ConfigEntry<bool> entry, bool saved)> disabled = new();

    public UiBench(DevTools dev) => this.dev = dev;

    public void Start()
    {
        folder = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix", "bench");
        string path = Path.Combine(folder, "ui.json");
        if (!File.Exists(path)) return;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path), json) ?? new Request();
        }
        catch (Exception e) { Plugin.Log.LogError($"UiBench: bad ui.json: {e.Message}"); return; }
        requestFile = path;
        Application.runInBackground = true;
        ListTools.WatchLoadingScreen();
        masterSaved = Plugin.MasterEnabled.Value;
        Plugin.MasterEnabled.ConfigFile.SaveOnConfigSet = false; // in memory only
        Plugin.MasterEnabled.Value = request.Master;
        foreach (string section in request.Disable)
        {
            if (dev.FindTarget(section) is not BepInEx.Configuration.ConfigEntry<bool> entry) { Plugin.Log.LogWarning($"UiBench: unknown feature '{section}'"); continue; }
            disabled.Add((entry, entry.Value));
            entry.Value = false;
        }
        string label = request.Label.Length > 0 ? request.Label : request.Master ? "on" : "off";
        runName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}";
        shotDir = Path.Combine(folder, "ui", runName);
        Directory.CreateDirectory(shotDir);
        ListenLogs();
        InstallSaveGuard();
        if (request.Time.Count > 0 && MethodTimers.Add(request.Time) is { Count: > 0 } missing)
            Plugin.Log.LogWarning("UiBench: not timed: " + string.Join(", ", missing));
        Plugin.Log.LogMessage($"UI test request found: save '{request.Save}', {request.Steps.Count} step(s)" +
                              (request.Monkey is { } mk ? $" + {mk.Actions} drawn (seed {mk.Seed})" : "") + $", mod {(request.Master ? "on" : "off")}");
        Go(Phase.WaitTitle);
    }

    // ---- phases

    private enum Phase { Idle, WaitTitle, WaitLoadStart, WaitLoadEnd, Settle, Step }

    private Phase phase = Phase.Idle;
    private float phaseTime;
    private int stepIndex;
    private readonly List<StepResult> results = new();
    private DateTime started;

    private void Go(Phase p)
    {
        phase = p;
        phaseTime = 0;
    }

    public void Update(float dt)
    {
        if (phase == Phase.Idle) return;
        phaseTime += dt;
        try { Tick(dt); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"UiBench: {e}");
            if (phase == Phase.Step) { Note("FAILED: " + e.Message); NextStep(); }
            else Finish("failed: " + e.Message);
        }
    }

    private void Tick(float dt)
    {
        switch (phase)
        {
            case Phase.WaitTitle:
                if (GameObject.Find("P_MainMenu(Clone)") is null) phaseTime = 0;
                else if (phaseTime > TitleWaitSeconds) LoadSave();
                break;
            case Phase.WaitLoadStart:
                if (ListTools.Loading) Go(Phase.WaitLoadEnd);
                else if (phaseTime > LoadTimeout) Finish($"save '{request.Save}' did not load");
                break;
            case Phase.WaitLoadEnd:
                if (!ListTools.Loading && !ListTools.Held && Player() is not null) Go(Phase.Settle);
                else if (phaseTime > LoadTimeout) Finish("loading did not end");
                break;
            case Phase.Settle:
                if (ListTools.Loading || ListTools.Held) { phaseTime = 0; break; }
                if (phaseTime > request.Settle) { FindAllWindows(); HarvestTemplates(); IsolateInput(); started = DateTime.Now; stepIndex = -1; NextStep(); }
                break;
            case Phase.Step:
                StepTick(dt);
                break;
        }
    }

    private void LoadSave()
    {
        SerializationManager sm = Singleton<SerializationManager>.Instance;
        if (sm is null || !sm.DoesSaveExist(request.Save)) { Finish($"save '{request.Save}' not found"); return; }
        Plugin.Log.LogMessage($"UI test: loading '{request.Save}'");
        allowLoad = true;
        try { sm.Load(request.Save); } finally { allowLoad = false; }
        Go(Phase.WaitLoadStart);
    }

    // ---- steps

    private StepRequest step;
    private StepResult result;
    private readonly List<float> frames = new();
    private float stepLength;
    private int stepFrame;
    private readonly List<string> notes = new();

    private void NextStep()
    {
        if (result != null) EndStep();
        if (++stepIndex >= request.Steps.Count)
        {
            if (DrawStep() is not { } drawnStep) { Finish(null); return; }
            request.Steps.Add(drawnStep);
        }
        step = request.Steps[stepIndex];
        result = new StepResult { N = stepIndex + 1, Step = Describe(step) };
        frames.Clear();
        notes.Clear();
        stepFrame = 0;
        errors = 0;
        checkedFrame = false;
        stepLength = step.S > 0 ? step.S : Defaultlength(step);
        PerformanceBehaviour.TickTimes = new Dictionary<string, double>();
        if (request.Time.Count > 0) MethodTimers.Take(); // counts from this step on
        Go(Phase.Step);
        UnityEngine.Debug.Log($"[UiBench] step {result.N} {result.Step}");
        var actionWatch = System.Diagnostics.Stopwatch.StartNew();
        Begin(step);
        if (actionWatch.Elapsed.TotalMilliseconds >= 2) Note($"action {actionWatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static float Defaultlength(StepRequest s) => s.Do.ToLowerInvariant() switch
    {
        "nav" => s.Count * Math.Max(1, s.Every) / 60f + 1f,
        "scroll" => s.Over + 1f,
        "shot" => 1f,
        _ => 1.5f,
    };

    private static string Describe(StepRequest s) => s.Do.ToLowerInvariant() switch
    {
        "menu" or "venue" => $"{s.Do} {s.Tab}" + (s.Venue.Length > 0 ? $" ({s.Venue})" : ""),
        "shop" => $"shop {s.Vendor}".TrimEnd(),
        "talk" or "vendor" => $"{s.Do} {s.Name}".TrimEnd(),
        "window" => $"window {s.Type}" + (s.Method.Length > 0 ? $".{s.Method}" : ""),
        "click" => $"click {s.Name}" + (s.Index > 0 ? $" #{s.Index}" : ""),
        "nav" => $"nav {s.Dir} x{s.Count}",
        "scroll" => $"scroll to {s.To:0.##}" + (s.Name.Length > 0 ? $" ({s.Name})" : ""),
        "shot" => $"shot {s.Name}",
        _ => s.Do,
    };

    private void Note(string text) => notes.Add(text);

    /// <summary>The step's action, run at its first frame (its cost shows in the next frames' times).</summary>
    private void Begin(StepRequest s)
    {
        switch (s.Do.ToLowerInvariant())
        {
            case "menu":
                if (!Enum.TryParse(s.Tab, true, out InGameMenuTab tab)) { Note($"unknown tab '{s.Tab}'"); break; }
                if (InGameMenuUi.Instance is not { } menu) { Note("in-game menu not found"); break; }
                menu.OpenOnTab(tab);
                break;
            case "pause":
                if (PauseMenu() is not { } pm) { Note("pause menu not found"); break; }
                if (!pm.IsOpen) pm.Open();
                break;
            case "save":
            case "load":
                if (PauseMenu() is not { } m) { Note("pause menu not found"); break; }
                if (!m.IsOpen) m.Open();
                // the logo screen's routine clears this flag at its very end; skipped logos (FastBoot) can leave it set,
                // and Save / Load then do nothing (MainMenuUI.OpenLoadWindow / OpenSaveWindow return early)
                if (LogoScreenUI.isVisible && UnityEngine.Object.FindObjectOfType(Il2CppType.Of<LogoScreenUI>()) is null)
                {
                    LogoScreenUI.isVisible = false;
                    Note("LOGO FLAG STUCK (cleared for the test)");
                }
                Button b = s.Do.Equals("save", StringComparison.OrdinalIgnoreCase) ? m.saveButton : m.loadButton;
                if (b is null || !b.interactable) { Note($"{s.Do} button not usable"); break; }
                var calls = new List<string>();
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                    calls.Add($"{b.onClick.GetPersistentTarget(i)?.GetIl2CppType().Name}.{b.onClick.GetPersistentMethodName(i)}");
                Note($"button {b.name} active {b.IsActive()} calls [{string.Join(", ", calls)}]");
                b.onClick.Invoke();
                UIPanel child = s.Do.Equals("save", StringComparison.OrdinalIgnoreCase) ? m.savePanel : m.loadPanel;
                Note($"panel visible {child?.IsVisible}, logo screen flag {LogoScreenUI.isVisible}");
                break;
            case "shop":
                OpenShop(s.Vendor);
                break;
            case "venue":
                OpenVenue(s.Tab, s.Venue);
                break;
            case "talk":
            case "vendor":
                Interaction(s);
                break;
            case "window":
                OpenWindow(s);
                break;
            case "click":
                Click(s.Name, s.Index, s.Text);
                break;
            case "nav":
                EnsureSelection(s.Start.Equals("list", StringComparison.OrdinalIgnoreCase));
                break;
            case "gamepad":
            case "mouse":
                UseGamepad(s.Do.Equals("gamepad", StringComparison.OrdinalIgnoreCase));
                break;
            case "scroll":
                StartScroll(s);
                break;
            case "shot":
                string file = Path.Combine(shotDir, Safe(s.Name) + ".png");
                ScreenCapture.CaptureScreenshot(file);
                Note("-> " + Path.GetFileName(file));
                break;
            case "close":
                Close();
                break;
            case "closeall":
                for (int i = 0; i < 6 && Closable() is not null; i++) Close();
                break;
            case "wait":
                break;
            default:
                Note($"unknown step '{s.Do}'");
                break;
        }
    }

    private bool checkedFrame;

    private void StepTick(float dt)
    {
        // the frame that paid for the content checks is not the step's
        if (checkedFrame)
        {
            if (leftGame != null) Finish("left the game at " + leftGame);
            else NextStep();
            return;
        }
        frames.Add(dt); // the action ran in the previous Update: this frame time is the first one it costs
        stepFrame++;
        string what = step.Do.ToLowerInvariant();
        // the shown texts are checked in the step after an action (its own frames are measured)
        if (stepFrame == 2 && what is "wait" or "shot") CheckTexts();
        if (what == "nav" && stepFrame % Math.Max(1, step.Every) == 0 && navDone < step.Count) NavMove();
        if (what == "scroll") ScrollTick();
        if (phaseTime >= stepLength && stepFrame >= 5 && (what != "nav" || navDone >= step.Count))
        {
            CheckContent();
            checkedFrame = true;
        }
    }

    private void EndStep()
    {
        if (frames.Count > 0)
        {
            result.FirstMs = Math.Round(frames.Take(3).Max() * 1000, 1);
            result.MaxMs = Math.Round(frames.Max() * 1000, 1);
            result.AvgMs = Math.Round(frames.Average() * 1000, 2);
            result.Over25 = frames.Count(x => x * 1000 > HitchMs);
            result.Frames = frames.Count;
            if (result.MaxMs > SlowFrameMs) Note($"SLOW FRAME {result.MaxMs:F0} ms");
        }
        if (step.Do.Equals("nav", StringComparison.OrdinalIgnoreCase)) NavReport();
        if (PerformanceBehaviour.TickTimes is { } ticks && ticks.Where(x => x.Value >= 2).ToList() is { Count: > 0 } slow)
            Note("ticks " + string.Join(", ", slow.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:F0} ms")));
        PerformanceBehaviour.TickTimes = null;
        if (request.Time.Count > 0 && MethodTimers.Take() is { Length: > 0 } timed) Note("timed " + timed);
        result.Errors = errors;
        result.Top = Top() is { } t ? t.gameObject.name : "-";
        if (stepErrors.Count > 0) Note("errors: " + string.Join(" | ", stepErrors.Take(3)));
        stepErrors.Clear();
        result.Note = string.Join("; ", notes);
        results.Add(result);
        Plugin.Log.LogMessage($"UI test step {result.N} {result.Step}: first {result.FirstMs} ms, max {result.MaxMs} ms, " +
                              $">25 ms {result.Over25}, errors {result.Errors}, top {result.Top}" +
                              (result.Note.Length > 0 ? $" — {result.Note}" : ""));
        result = null;
    }

    // ---- windows

    /// <summary>The visible panel opened last (UIManager's open panels), null when only the HUD is shown.</summary>
    private static UIPanel Top()
    {
        var open = Singleton<UIManager>.Instance?._openPanels;
        if (open is null) return null;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            UIPanel p = open[i];
            // shown for real (not faded out), and not a HUD part (quest HUD: closing it is not a window test)
            if (Direct.Alive(p) && p.IsVisible && Visible(p.gameObject) && !IsHud(p)) return p;
        }
        return null;
    }

    private static readonly string[] HudPanels = { "HeadsUpDisplayUI", "WorldStateDisplayUI", "NotificationHudUi", "ActiveJournalEntriesUi" };

    /// <summary>Parts of the HUD that UIManager lists as open panels: never a window to test or close.</summary>
    private static bool IsHud(UIPanel p) =>
        p.gameObject.name.Contains("HUD", StringComparison.OrdinalIgnoreCase) || HudPanels.Contains(p.GetIl2CppType().Name);

    /// <summary>The top window, else the last one a "window" step opened if still shown (some are not in the open list).</summary>
    private UIPanel Closable() => Top() ?? (Direct.Alive(lastWindow) && lastWindow.IsVisible ? lastWindow : null);

    private void Close()
    {
        UIPanel top = Closable();
        if (top is null) { Note("nothing to close"); return; }
        UIWindow w = top.TryCast<UIWindow>();
        if (w is not null) w.Close(); else top.Hide();
    }

    private static MainMenuUI pauseMenu;

    private static MainMenuUI PauseMenu()
    {
        if (Direct.Alive(pauseMenu)) return pauseMenu;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MainMenuUI>(), true))
            if (o.TryCast<MainMenuUI>() is { } m && !m.title) return pauseMenu = m;
        return null;
    }

    private void OpenShop(string name)
    {
        Vendor[] vendors = Singleton<EconomyManager>.Instance?.Vendors;
        if (vendors is null || vendors.Length == 0) { Note("no vendor"); return; }
        Vendor v = name.Length == 0 ? vendors[0]
            : vendors.FirstOrDefault(x => x.VendorName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true ||
                                          x.Id?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        if (v is null) { Note($"vendor '{name}' not found ({string.Join(", ", vendors.Take(12).Select(x => x.VendorName))} ...)"); return; }
        ShopUINew shop = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ShopUINew>(), true))
            if ((shop = o.TryCast<ShopUINew>()) is not null) break;
        if (shop is null) { Note("shop window not found"); return; }
        string Pools() => string.Join(" ", shop.GetComponentsInChildren<ItemListUI>(true).Select(l =>
            $"{PathOf(l.transform, 3)} {l._itemDisplayInstances?.Count}/{l._displayedInstanceCount}"));
        Note("pools before " + Pools());
        shop.uiOpenRequest.Invoke(v);
        Note("after " + Pools());
        Note($"vendor {v.VendorName}");
    }

    /// <summary>The player's venue named so, else the one with most reviews (null + note when none).</summary>
    private Venue PlayerVenue(string name)
    {
        // the player's venues only: reading the reviews of a venue without runtime data logs a game error
        var owned = new Il2CppSystem.Collections.Generic.List<Venue>();
        Singleton<PlayerManager>.Instance?.LocalPlayer?.GetOwnedVenues(owned);
        var venues = new List<Venue>();
        foreach (Venue x in owned) venues.Add(x);
        if (venues.Count == 0) { Note("the player owns no venue"); return null; }
        Venue v = name.Length > 0
            ? venues.FirstOrDefault(x => x.name.Contains(name, StringComparison.OrdinalIgnoreCase))
            : venues.OrderByDescending(x => x.Reviews?.Count ?? 0).First();
        if (v is null) Note($"venue '{name}' not found");
        return v;
    }

    private void OpenVenue(string tabName, string name)
    {
        if (!Enum.TryParse(tabName.Length > 0 ? tabName : "Overview", true, out VenueWindow.Tab tab)) { Note($"unknown tab '{tabName}'"); return; }
        Venue v = PlayerVenue(name);
        if (v is null) return;
        if (!FindWindow(nameof(VenueWindow), out _, out _, out UIPanel panel) || panel.TryCast<VenueWindow>() is not { } window) return;
        window.Open(v, tab, false, null);
        Note($"venue {v.name} ({v.Reviews?.Count ?? 0} reviews)");
    }

    private UIPanel lastWindow;

    /// <summary>
    /// Any game window by its class name: its instance in the scene (inactive ones too), opened by its own opener
    /// (public or private, both reachable through the interop wrapper) with arguments the tool can fill: the player's
    /// venue, the bool flag, an empty list, null for callbacks / a person, a recipe asset or a view found in the scene.
    /// A window gone or an opener changed after a game update shows in the step's note.
    /// </summary>
    private void OpenWindow(StepRequest s)
    {
        if (!FindWindow(s.Type, out Type type, out object window, out UIPanel panel)) return;
        string[] names = s.Method.Length > 0 ? new[] { s.Method } : new[] { "Open", "Show" };
        string missing = null;
        foreach (string name in names)
        {
            // the opener with the most parameters the tool can fill
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         .Where(x => x.Name == name && !x.IsGenericMethod).OrderByDescending(x => x.GetParameters().Length))
            {
                var args = new List<object>();
                string need = null;
                foreach (ParameterInfo p in m.GetParameters())
                {
                    if (!TryArgument(p.ParameterType, s, out object a)) { need = p.ParameterType.Name; break; }
                    args.Add(a);
                }
                if (need != null) { missing ??= $"{name}({need})"; continue; }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { m.Invoke(window, args.ToArray()); }
                catch (TargetInvocationException e) { Note($"{name} failed: {e.InnerException?.Message}"); return; }
                lastWindow = panel;
                Note($"{name}({string.Join(", ", args.Select(Shown))}) {watch.Elapsed.TotalMilliseconds:F0} ms" +
                     (panel is not null ? $", visible {panel.IsVisible}" : ""));
                return;
            }
        }
        Note("NO OPENER" + (missing != null ? $" (cannot fill {missing})" : ""));
    }

    private readonly Dictionary<string, (Type type, object window, UIPanel panel)> windows = new();

    /// <summary>
    /// The window of a "window" step, searched once (a scene-wide search of inactive objects costs ~35 ms: done for
    /// all the request's windows before the first step, so it is not measured as the window's opening).
    /// </summary>
    private bool FindWindow(string name, out Type type, out object window, out UIPanel panel)
    {
        window = null;
        panel = null;
        if (windows.TryGetValue(name, out var w) && (w.panel is null || Direct.Alive(w.panel)))
        {
            (type, window, panel) = w;
            if (window is null) Note($"no {name} in the scene");
            return window is not null;
        }
        type = GameType(name);
        if (type is null) { Note($"WINDOW TYPE '{name}' NOT FOUND"); return false; }
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(type), true);
        if (found.Length > 0)
        {
            window = Activator.CreateInstance(type, found[0].Pointer);
            panel = found[0].TryCast<UIPanel>();
        }
        windows[name] = (type, window, panel);
        if (window is null) Note($"no {name} in the scene");
        return window is not null;
    }

    private void FindAllWindows()
    {
        foreach (StepRequest s in request.Steps)
        {
            string name = s.Do.ToLowerInvariant() switch { "window" => s.Type, "venue" => nameof(VenueWindow), _ => null };
            if (name != null && !windows.ContainsKey(name)) FindWindow(name, out _, out _, out _);
        }
        notes.Clear();
    }

    private bool TryArgument(Type t, StepRequest s, out object value)
    {
        value = null;
        if (t == typeof(bool)) { value = s.Flag; return true; }
        if (typeof(BaseProperty).IsAssignableFrom(t))
        {
            Venue v = PlayerVenue(s.Venue);
            value = v;
            return v is not null && t.IsAssignableFrom(typeof(Venue));
        }
        if (typeof(Il2CppSystem.Delegate).IsAssignableFrom(t) || t == typeof(Nivalis.GhostSystem.Ai.Person)) return true; // null
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Il2CppSystem.Collections.Generic.List<>))
        {
            value = Activator.CreateInstance(t);
            return true;
        }
        bool component = typeof(Component).IsAssignableFrom(t);
        if (!component && t != typeof(Nivalis.CraftingSystem.MealRecipeDefinition)) return false;
        // a view in the scene (vending / composting machine), or an asset loaded by the game (recipe)
        var objects = component ? UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t), false) : Resources.FindObjectsOfTypeAll(Il2CppType.From(t));
        if (objects.Length == 0) return false;
        value = Activator.CreateInstance(t, objects[0].Pointer);
        return true;
    }

    private static string Shown(object a) => a switch
    {
        null => "null",
        UnityEngine.Object o => o.name,
        _ => a.ToString(),
    };

    private static readonly Dictionary<string, Type> gameTypes = new();

    /// <summary>A game window class (UIPanel subclass) by its name, any namespace.</summary>
    private static Type GameType(string name)
    {
        if (gameTypes.TryGetValue(name, out Type t)) return t;
        Type[] all;
        try { all = typeof(UIPanel).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { all = e.Types.Where(x => x is not null).ToArray(); }
        return gameTypes[name] = all.FirstOrDefault(x => x.Name == name && typeof(UIPanel).IsAssignableFrom(x));
    }

    private void Click(string name, int index, string text = "")
    {
        UIPanel top = Top();
        if (top is null) { Note("no window"); return; }
        var matches = top.GetComponentsInChildren<Selectable>(false)
            .Where(x => x.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        x.transform.parent is { } p && p.name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        Selectable byText = text.Length > 0 ? matches.FirstOrDefault(x => LabelOf(x) == text) : null;
        if (byText is null && index >= matches.Count)
        {
            // list what can be clicked, so a run with a wrong name tells the right one
            var names = top.GetComponentsInChildren<Selectable>(false).Where(x => x.IsInteractable())
                .Select(x => $"{x.transform.parent?.name}/{x.gameObject.name}").Distinct().Take(40);
            Note($"'{name}' #{index} not found ({matches.Count}); clickable: {string.Join(", ", names)}");
            return;
        }
        if (text.Length > 0 && byText is null) Note($"no '{name}' shows '{text}': #{index} pressed");
        Selectable s = byText ?? matches[index];
        if (s.TryCast<Toggle>() is { } t) t.isOn = !t.isOn || t.group is not null;
        else if (s.TryCast<Button>() is { } b) b.onClick.Invoke();
        else if (s.TryCast<TMPro.TMP_Dropdown>() is { options.Count: > 0 } d)
        {
            // each click picks the next option, so a few clicks walk every sort choice
            d.value = (d.value + 1) % d.options.Count;
            Note($"'{name}' option {d.value}/{d.options.Count}: {d.options[d.value].text}");
        }
        else { Note($"'{name}' is a {s.GetIl2CppType().Name}"); return; }
        EventSystem.current?.SetSelectedGameObject(s.gameObject);
    }

    /// <summary>The first three texts a button / toggle shows (a row's name, a save's day, place and time...).</summary>
    private static string LabelOf(Selectable s)
    {
        var parts = new List<string>();
        foreach (TMPro.TMP_Text t in s.GetComponentsInChildren<TMPro.TMP_Text>(false))
            if (t.text?.Trim() is { Length: > 0 } x && parts.Count < 3) parts.Add(x.Length > 40 ? x[..40] : x);
        return string.Join(" / ", parts);
    }

    // ---- gamepad navigation

    private int navDone, navHidden, navStuck, navLost;
    private string navFirstHidden, navFirstLost;

    private void EnsureSelection(bool inList)
    {
        navDone = navHidden = navStuck = navLost = 0;
        navTrace.Clear();
        navFirstHidden = navFirstLost = null;
        EventSystem es = EventSystem.current;
        if (es is null) { Note("no event system"); return; }
        if (inList && Longest(Top(), "") is { } list && list.content is { } content)
        {
            Selectable row = content.GetComponentsInChildren<Selectable>(false)
                .FirstOrDefault(x => x.IsInteractable() && Visible(x.gameObject));
            if (row is not null) { es.SetSelectedGameObject(row.gameObject); Note("selected " + PathOf(row.transform, 2)); return; }
        }
        if (Selected() is { } g && g.activeInHierarchy) { Note("from " + g.name); return; }
        GameObject first = Top()?.FirstSelected;
        if (!Direct.Alive(first) || !first.activeInHierarchy)
            first = Top()?.GetComponentsInChildren<Selectable>(false).FirstOrDefault(x => x.IsInteractable())?.gameObject;
        if (first is null) { Note("nothing selectable"); return; }
        es.SetSelectedGameObject(first);
        Note("selected " + first.name);
    }

    private void NavMove()
    {
        navDone++;
        EventSystem es = EventSystem.current;
        GameObject from = Selected();
        if (from is null) { navLost++; return; }
        MoveDirection dir = step.Dir.ToLowerInvariant() switch
        {
            "up" => MoveDirection.Up, "left" => MoveDirection.Left, "right" => MoveDirection.Right, _ => MoveDirection.Down,
        };
        var data = new AxisEventData(es) { moveDir = dir };
        data.moveVector = dir switch
        {
            MoveDirection.Up => Vector2.up, MoveDirection.Left => Vector2.left, MoveDirection.Right => Vector2.right, _ => Vector2.down,
        };
        try { ExecuteEvents.Execute(from, data, ExecuteEvents.moveHandler); }
        catch (Exception e)
        {
            if (navDone == 1) Note("move event failed, Selectable.OnMove used (" + e.Message + ")");
            from.GetComponent<Selectable>()?.OnMove(data);
        }
        GameObject to = Selected();
        if (navTrace.Count < 16) navTrace.Add(to is null ? "-" : ShortRow(to));
        if (to is null) { navLost++; navFirstLost ??= $"after {PathOf(from.transform, 2)} (move {navDone})"; return; }
        if (to.Pointer == from.Pointer) { navStuck++; return; }
        if (!Visible(to)) { navHidden++; navFirstHidden ??= $"{to.name} (move {navDone})"; }
    }

    private readonly List<string> navTrace = new();

    /// <summary>The selected object, or null once the game destroyed it (a dialogue choice pressed).</summary>
    private static GameObject Selected() =>
        EventSystem.current?.currentSelectedGameObject is { } g && Direct.Alive(g) ? g : null;

    /// <summary>A selected object as "name" or, for a list row, "name[sibling]:first text".</summary>
    private static string ShortRow(GameObject go)
    {
        string text = go.GetComponentInChildren<TMPro.TMP_Text>(false)?.text?.Trim() ?? "";
        if (text.Length > 14) text = text[..14];
        bool row = go.GetComponentInParent<ScrollRect>() is not null;
        return row ? $"{go.name.Replace("P_Element_ScrollView_", "")}[{go.transform.GetSiblingIndex()}]:{text}" : go.name;
    }

    private void NavReport()
    {
        GameObject sel = Selected();
        Note($"moves {navDone}, stayed {navStuck}, hidden selections {navHidden}" +
             (navFirstHidden != null ? $" first {navFirstHidden}" : "") + $", ends on {(sel is null ? "nothing" : PathOf(sel.transform, 3))}");
        if (navTrace.Count > 0) Note("path " + string.Join(" > ", navTrace));
        if (navHidden > 0) Note("SELECTION HIDDEN");
        if (navLost > 0) Note($"SELECTION LOST {navLost}x, first {navFirstLost}");
    }

    /// <summary>Notes the template texts shown in the top window (see DefaultPlaceholders).</summary>
    private void CheckTexts()
    {
        if (Closable() is not { } top) return;
        var found = new List<string>();
        foreach (TMPro.TMP_Text t in top.GetComponentsInChildren<TMPro.TMP_Text>(false))
        {
            string text = t.text;
            if (string.IsNullOrEmpty(text) || !Visible(t.gameObject)) continue;
            string hit = DefaultPlaceholders.Concat(request.Placeholders)
                .FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
            if (hit != null) found.Add($"'{hit}' in {PathOf(t.transform, 3)}");
        }
        if (found.Count == 0) return;
        placeholderSteps++;
        Note($"PLACEHOLDER TEXT {found.Count}x: {string.Join(", ", found.Take(3))}");
        Plugin.Log.LogWarning($"UiBench: template text shown in {top.gameObject.name}: {string.Join(", ", found.Take(3))}");
    }

    private int placeholderSteps;

    /// <summary>Selected object shown: active, not faded out, and its centre inside the viewport of its scroll list.</summary>
    private static bool Visible(GameObject go)
    {
        if (!go.activeInHierarchy) return false;
        foreach (CanvasGroup g in go.GetComponentsInParent<CanvasGroup>())
        {
            if (g.alpha <= 0.01f) return false;
            if (g.ignoreParentGroups) break;
        }
        ScrollRect sr = go.GetComponentInParent<ScrollRect>();
        if (sr is null) return true;
        RectTransform view = sr.viewport ?? sr.GetComponent<RectTransform>();
        RectTransform rt = go.GetComponent<RectTransform>();
        if (view is null || rt is null) return true;
        Vector3 centre = rt.TransformPoint(rt.rect.center);
        Vector3 local = view.InverseTransformPoint(centre);
        return view.rect.Contains(new Vector2(local.x, local.y));
    }

    private static string PathOf(Transform t, int depth)
    {
        var parts = new List<string>();
        for (; t is not null && depth-- > 0; t = t.parent) parts.Insert(0, t.name);
        return string.Join("/", parts);
    }

    // ---- scrolling

    private ScrollRect scroll;
    private float scrollFrom;

    private void StartScroll(StepRequest s)
    {
        scroll = null;
        UIPanel top = Top();
        if (top is null) { Note("no window"); return; }
        scroll = Longest(top, s.Name);
        if (scroll is null) { Note("no scroll list"); return; }
        scrollFrom = scroll.verticalNormalizedPosition;
        Note($"list {PathOf(scroll.transform, 2)}, content {scroll.content?.rect.height ?? 0:F0} high");
    }

    /// <summary>The active scroll list of a window with the highest content (named so, or its parent, if a name is given).</summary>
    private static ScrollRect Longest(UIPanel top, string name) =>
        top?.GetComponentsInChildren<ScrollRect>(false)
            .Where(x => name.Length == 0 || x.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        x.transform.parent is { } p && p.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.content is { } c ? c.rect.height : 0).FirstOrDefault();

    // ---- input device

    private static Gamepad virtualPad;

    /// <summary>
    /// Switches the game to its controller scheme the way a real controller does (PlayerInputManager.SwitchControlScheme
    /// with a gamepad device: IsController, button icons, panels' OnControllerConnected, first selections), with a
    /// virtual Xbox controller added for the run (its button icons); "mouse" switches back to keyboard and mouse.
    /// </summary>
    private void UseGamepad(bool on)
    {
        PlayerInputManager input = Singleton<PlayerInputManager>.Instance;
        if (input is null) { Note("no input manager"); return; }
        if (on && virtualPad is null) virtualPad = InputSystem.AddDevice("XInputControllerWindows", "UiBenchPad")?.TryCast<Gamepad>();
        InputDevice device = on ? virtualPad : Keyboard.current;
        if (device is null) { Note("no device"); return; }
        input.SwitchControlScheme(device);
        Note($"controller {input.IsController}");
    }

    private InputDevice mouseOff;

    /// <summary>
    /// The real mouse is switched off in the game for the steps: the game takes the focus at launch, and the cursor
    /// resting over a list selected the row under it (a save "selected" in one run and not the other, 2026-10-08).
    /// Also clears the logo screen flag FastBoot can leave set (Save / Load buttons then do nothing).
    /// </summary>
    private void IsolateInput()
    {
        if (Mouse.current is { enabled: true } m)
        {
            InputSystem.DisableDevice(m);
            mouseOff = m;
        }
        if (LogoScreenUI.isVisible && UnityEngine.Object.FindObjectOfType(Il2CppType.Of<LogoScreenUI>()) is null)
        {
            LogoScreenUI.isVisible = false;
            Plugin.Log.LogMessage("UI test: logo screen flag was stuck (cleared)");
        }
    }

    private void RestoreInput()
    {
        if (mouseOff is null) return;
        try { InputSystem.EnableDevice(mouseOff); } catch (Exception e) { Plugin.Log.LogWarning($"UiBench: mouse not enabled again ({e.Message})"); }
        mouseOff = null;
    }

    private static void RemovePad()
    {
        if (virtualPad is null) return;
        try
        {
            if (Singleton<PlayerInputManager>.Instance is { } input && Keyboard.current is { } kb) input.SwitchControlScheme(kb);
            InputSystem.RemoveDevice(virtualPad);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"UiBench: virtual gamepad not removed ({e.Message})"); }
        virtualPad = null;
    }

    private void ScrollTick()
    {
        if (scroll is null || !Direct.Alive(scroll)) return;
        float k = Mathf.Clamp01(phaseTime / Math.Max(0.05f, step.Over));
        scroll.verticalNormalizedPosition = Mathf.Lerp(scrollFrom, Mathf.Clamp01(step.To < 0 ? 0 : step.To), k);
    }

    // ---- errors (Unity log + BepInEx log)

    private int errors;
    private readonly List<string> stepErrors = new();
    private Application.LogCallback logCallback; // kept alive: Unity holds only the native delegate
    private ErrorListener listener;

    private void ListenLogs()
    {
        logCallback = DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnUnityLog));
        Application.add_logMessageReceived(logCallback);
        listener = new ErrorListener(this);
        BepInEx.Logging.Logger.Listeners.Add(listener);
    }

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type is LogType.Error or LogType.Exception or LogType.Assert) Count("Unity: " + condition);
    }

    private void Count(string message)
    {
        if (phase != Phase.Step) return;
        errors++;
        message = message.Length > 160 ? message[..160] : message;
        if (!stepErrors.Contains(message)) stepErrors.Add(message);
    }

    private sealed class ErrorListener : ILogListener
    {
        private readonly UiBench owner;
        public ErrorListener(UiBench owner) => this.owner = owner;
        public LogLevel LogLevelFilter => LogLevel.Error | LogLevel.Fatal;

        public void LogEvent(object sender, LogEventArgs e)
        {
            // Unity's own messages reach BepInEx too: counted once, from Unity's callback
            if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) == 0 || e.Source.SourceName.StartsWith("Unity")) return;
            owner.Count($"{e.Source.SourceName}: {e.Data}");
        }

        public void Dispose() { }
    }

    // ---- end

    private void Finish(string why)
    {
        if (result != null) EndStep();
        phase = Phase.Idle;
        RemovePad();
        RestoreInput();
        Plugin.MasterEnabled.Value = masterSaved;
        foreach (var (entry, saved) in disabled) entry.Value = saved;
        Plugin.MasterEnabled.ConfigFile.SaveOnConfigSet = true;
        if (listener != null) { BepInEx.Logging.Logger.Listeners.Remove(listener); listener = null; }
        if (logCallback != null) { Application.remove_logMessageReceived(logCallback); logCallback = null; }
        if (why != null) dev.Report("UI test " + why);
        WriteResults(why);
        try { File.Move(requestFile, requestFile + ".done", true); } catch { }
        if (request.Quit) Application.Quit();
    }

    private void WriteResults(string why)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# UI test {runName}");
        sb.AppendLine();
        sb.AppendLine($"Save '{request.Save}', mod {(request.Master ? "on" : "off")}" +
                      (request.Disable.Count > 0 ? $" except {string.Join(", ", request.Disable)}" : "") + $", game {Application.version}, " +
                      $"screen {Screen.width}x{Screen.height}" + (why != null ? $" — stopped: {why}" : ""));
        sb.AppendLine();
        if (request.Monkey is { } mk) { sb.AppendLine($"Monkey seed {mk.Seed}: {drawn} step(s) drawn, {flaggedSteps} flagged; replay: {runName}.steps.json"); sb.AppendLine(); }
        if (blocked > 0) { sb.AppendLine($"Game calls blocked (no save during tests): {blocked} ({string.Join(", ", blockedCalls.Distinct())})"); sb.AppendLine(); }
        if (placeholderSteps > 0) { sb.AppendLine($"**TEMPLATE TEXT SHOWN in {placeholderSteps} step(s)** (see PLACEHOLDER TEXT notes)"); sb.AppendLine(); }
        sb.AppendLine("Mods: " + string.Join(", ", BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Values
            .Select(p => p.Metadata.Name)));
        sb.AppendLine();
        sb.AppendLine("| # | step | first ms | max ms | >25 ms | avg ms | errors | top window | notes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (StepResult r in results)
            sb.AppendLine($"| {r.N} | {r.Step} | {r.FirstMs} | {r.MaxMs} | {r.Over25} | {r.AvgMs} | {r.Errors} | {r.Top} | " +
                          $"{r.Note?.Replace("|", "/")}{(r.Templates is { Count: > 0 } tp ? " TEMPLATE " + string.Join(", ", tp.Take(3)) : "")} |");
        string dir = Path.Combine(folder, "ui");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, runName + ".md"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, runName + ".json"),
            JsonSerializer.Serialize(new { run = runName, save = request.Save, master = request.Master, why, steps = results },
                new JsonSerializerOptions(json) { WriteIndented = true }));
        // the steps really played, as a request that replays them (monkey off)
        File.WriteAllText(Path.Combine(dir, runName + ".steps.json"),
            JsonSerializer.Serialize(new { save = request.Save, settle = request.Settle, quit = true, steps = request.Steps },
                new JsonSerializerOptions(json) { WriteIndented = true }));
        Plugin.Log.LogMessage($"UI test results: bench/ui/{runName}.md");
    }

    private static string Safe(string name) =>
        string.Concat((name.Length > 0 ? name : "shot").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static PlayerCharacterController Player()
    {
        try
        {
            PlayerCharacterController c = Singleton<PlayerManager>.Instance?.LocalPlayer?.Character?.Controller;
            return c is not null && Direct.Alive(c) ? c : null;
        }
        catch { return null; }
    }
}
