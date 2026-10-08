using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Localization;
using Nivalis.UI;
using Nivalis.UI.InGameMenu;
using NivalisPerformanceFix.Features;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// UI test, monkey part: "monkey" {seed, actions} in ui.json makes the steps at run time, each drawn at random among
/// what the screen offers at that moment (a window to open when none is shown; a visible button / toggle to press, a
/// gamepad move, a scroll, a tab or a close when one is). The same seed draws the same sequence from the same state;
/// the steps really played are written to bench/ui/&lt;run&gt;.steps.json, a request that replays them exactly (a
/// second launch with the mod off replays the run of the mod on: tools/monkeycompare.py then keeps what only one shows).
/// Every step ends with content checks on a frame of its own (not measured): template texts shown (texts the game's
/// prefabs carry before any code fills them), the shown texts and visible rows of each list (compared between runs),
/// a window still shown after closeAll; a screenshot at each window's first display and at each flagged step.
/// The game can never save, delete a save or load another one while a UI test runs (patched out, counted).
/// </summary>
internal sealed partial class UiBench
{
    private sealed class MonkeyRequest
    {
        public int Seed { get; set; } = 1;
        public int Actions { get; set; } = 100;
        /// <summary>Length of each drawn step in seconds.</summary>
        public float S { get; set; } = 0.8f;
        /// <summary>Screenshots at first displays and flagged steps.</summary>
        public bool Shots { get; set; } = true;
    }

    private const float SlowFrameMs = 100f;

    /// <summary>Names (object, parent or shown label) the monkey never presses: leaving the game or the save, settings
    /// that outlive the run (resolution, display mode).</summary>
    private static readonly string[] NeverPress =
        { "quit", "exit", "desktop", "title", "mainmenu", "main menu", "delete", "setting", "option", "resolution", "display", "fullscreen" };

    private static readonly string[] MenuTabs = Enum.GetNames(typeof(InGameMenuTab));
    private static readonly string[] VenueTabs = { "Overview", "Inventory", "Reviews", "Staff" };

    private System.Random rng;
    private int drawn;

    // ---- drawing the next step

    /// <summary>The next monkey step, drawn from what is shown now; null when the run has its count.</summary>
    private StepRequest DrawStep()
    {
        MonkeyRequest m = request.Monkey;
        if (m is null || drawn >= m.Actions) return null;
        rng ??= new System.Random(m.Seed);
        drawn++;
        StepRequest s = Closable() is { } top ? DrawInWindow(top) : DrawOpen();
        if (s.S <= 0 && s.Do is not ("nav" or "scroll")) s.S = m.S;
        return s;
    }

    private StepRequest DrawOpen()
    {
        int r = rng.Next(100);
        if (r < 30) return new StepRequest { Do = "menu", Tab = Pick(MenuTabs) };
        if (r < 38) return new StepRequest { Do = "shop", Vendor = PickVendor() };
        // the player's own way: the interaction with a character (dialogue) or a vendor's stall (shop)
        if (r < 50 && PickNear("talk") is { } talk) return talk;
        if (r < 56 && PickNear("vendor") is { } stall) return stall;
        if (r < 66) return new StepRequest { Do = "venue", Tab = Pick(VenueTabs) };
        if (r < 75) return new StepRequest { Do = "pause" };
        if (r < 82) return new StepRequest { Do = "save" };
        if (r < 89) return new StepRequest { Do = "load" };
        if (r < 95) return new StepRequest { Do = rng.Next(2) == 0 ? "gamepad" : "mouse" };
        return new StepRequest { Do = "wait" };
    }

    private StepRequest DrawInWindow(UIPanel top)
    {
        int r = rng.Next(100);
        if (r < 45 && PickClick(top) is { } click) return click;
        if (r < 60)
            return new StepRequest
            {
                Do = "nav", Dir = rng.Next(4) switch { 0 => "up", 1 => "left", 2 => "right", _ => "down" },
                Count = 1 + rng.Next(12), Every = 3 + rng.Next(4), Start = rng.Next(2) == 0 ? "list" : "",
            };
        if (r < 72) return new StepRequest { Do = "scroll", To = (float)Math.Round(rng.NextDouble(), 2), Over = (float)Math.Round(0.2 + rng.NextDouble() * 1.3, 1) };
        if (r < 82) return new StepRequest { Do = "close" };
        if (r < 87) return new StepRequest { Do = "closeAll" };
        if (r < 95 && InGameMenuUi.Instance is { } menu && menu.IsVisible) return new StepRequest { Do = "menu", Tab = Pick(MenuTabs) };
        if (r < 97) return new StepRequest { Do = rng.Next(2) == 0 ? "gamepad" : "mouse" };
        return new StepRequest { Do = "wait" };
    }

    // ---- the player's interactions (talk to a character, use a vendor's stall)

    private const int NearCount = 6;

    /// <summary>Interactions of that kind the player could use now, nearest first, with a name to find them again.</summary>
    private static List<(Component c, string name, Action run)> Interactions(string kind)
    {
        Vector3 at = Player() is { } p ? p.transform.position : Vector3.zero;
        var found = new List<(Component c, string name, Action run)>();
        if (kind == "talk")
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Nivalis.Dialogue.StartDialogueInteraction>(), false))
                if (o.TryCast<Nivalis.Dialogue.StartDialogueInteraction>() is { } d && d.IsActive && d.CanInteract())
                    found.Add((d, d._character?.Name ?? d.transform.root.name, () => Interact(d)));
        }
        else
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<VendorInteraction>(), false))
                if (o.TryCast<VendorInteraction>() is { } v && v.IsActive && v.CanInteract())
                    found.Add((v, v.Definition?.Id ?? v.transform.root.name, () => v.DoInteraction()));
        }
        return found.OrderBy(x => (x.c.transform.position - at).sqrMagnitude).ToList();
    }

    /// <summary>IInteractable.DoInteraction, implemented explicitly by the game's class (interop: a method whose
    /// name ends so).</summary>
    private static void Interact(Il2CppSystem.Object target)
    {
        var m = target.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name.EndsWith("DoInteraction") && x.GetParameters().Length == 0);
        if (m is null) throw new MissingMethodException(target.GetType().Name, "DoInteraction");
        m.Invoke(target, null);
    }

    private StepRequest PickNear(string kind)
    {
        var near = Interactions(kind).Take(NearCount).ToList();
        return near.Count == 0 ? null : new StepRequest { Do = kind, Name = near[rng.Next(near.Count)].name };
    }

    /// <summary>"talk" / "vendor" step: the interaction named so (nearest first), else the nearest one.</summary>
    private void Interaction(StepRequest s)
    {
        var all = Interactions(s.Do.ToLowerInvariant());
        if (all.Count == 0) { Note($"no {s.Do} interaction usable"); return; }
        var pick = all.FirstOrDefault(x => x.name == s.Name);
        if (pick.c is null)
        {
            pick = all[0];
            if (s.Name.Length > 0) Note($"'{s.Name}' not usable now, nearest instead");
        }
        Vector3 at = Player() is { } p ? p.transform.position : Vector3.zero;
        pick.run();
        Note($"{s.Do} {pick.name} at {Vector3.Distance(at, pick.c.transform.position):F0} m");
    }

    private string Pick(string[] from) => from[rng.Next(from.Length)];

    private string PickVendor()
    {
        var vendors = Singleton<Nivalis.Economy.EconomyManager>.Instance?.Vendors;
        if (vendors is null || vendors.Length == 0) return "";
        return vendors[rng.Next(vendors.Length)].Id ?? "";
    }

    /// <summary>A visible, usable button / toggle of the top window, named the way the "click" step finds it.</summary>
    private StepRequest PickClick(UIPanel top)
    {
        if (top.gameObject.name.Contains("Setting", StringComparison.OrdinalIgnoreCase)) return null;
        // a confirmation popup confirms what the player asked: load a save (back to the title screen first: seen in
        // the 2026-10-08 campaign, then "new game" pressed there), overwrite, delete, quit. Only its other buttons.
        bool confirm = top.TryCast<PopupDialog>() is not null || top.gameObject.name.Contains("GenericPopup", StringComparison.OrdinalIgnoreCase);
        var all = top.GetComponentsInChildren<Selectable>(false);
        var usable = all.Where(x => x.IsInteractable() && (x.TryCast<Button>() is not null || x.TryCast<Toggle>() is not null)
                                    && Visible(x.gameObject) && !Forbidden(x) && !(confirm && Confirms(x))).ToList();
        if (usable.Count == 0) return null;
        Selectable s = usable[rng.Next(usable.Count)];
        string name = s.gameObject.name;
        // the click step's own matching (object or parent named so): the index among those
        var matches = all.Where(x => x.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                     x.transform.parent is { } p && p.name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        int index = matches.FindIndex(x => x.Pointer == s.Pointer);
        return index < 0 ? null : new StepRequest { Do = "click", Name = name, Index = index, Text = LabelOf(s) };
    }

    private static readonly string[] ConfirmNames = { "accept", "confirm", "yes" };
    private static readonly string[] ConfirmLabels = { "ok", "oui", "yes", "valider", "confirmer", "accepter" };

    private static bool Confirms(Selectable s)
    {
        string label = (s.GetComponentInChildren<TMPro.TMP_Text>(false)?.text ?? "").Trim();
        return ConfirmNames.Any(w => s.gameObject.name.Contains(w, StringComparison.OrdinalIgnoreCase)) ||
               ConfirmLabels.Any(w => label.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The run left the loaded game (title screen or a loading screen): its next steps would test something else.</summary>
    private string LeftTheGame()
    {
        if (ListTools.Loading) return "a loading screen started";
        if (GameObject.Find("P_MainMenu(Clone)") is not null) return "back to the title screen";
        return null;
    }

    private static bool Forbidden(Selectable s)
    {
        string label = s.GetComponentInChildren<TMPro.TMP_Text>(false)?.text ?? "";
        string all = $"{s.gameObject.name} {s.transform.parent?.name} {label}";
        return NeverPress.Any(w => all.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // ---- content checks (end of each step, on a frame not measured)

    private string leftGame;
    private HashSet<string> templates;
    private readonly HashSet<string> shownWindows = new();
    private int flaggedSteps;

    /// <summary>
    /// The texts the game's prefabs and its never-shown windows carry before any code fills them ("Recipe title"),
    /// minus the static labels the game translates itself (same text once filled in English).
    /// </summary>
    private void HarvestTemplates()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMPro.TMP_Text>()))
        {
            // prefabs only (no scene): a hidden object of the scene may already carry the game's data ("Banor")
            if (o.TryCast<TMPro.TMP_Text>() is not { } t || t.gameObject.scene.IsValid()) continue;
            string text = t.text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 3 || !text.Any(char.IsLetter)) continue;
            if (t.GetComponent<LocalizedStaticUILabel>() is not null) continue;
            templates.Add(text);
        }
        Plugin.Log.LogMessage($"UI test: {templates.Count} template texts harvested in {watch.ElapsedMilliseconds} ms");
        var open = Singleton<UIManager>.Instance?._openPanels;
        if (open is not null)
            Plugin.Log.LogMessage("UI test: open panels at start: " + string.Join(", ", Enumerable.Range(0, open.Count).Select(i => open[i])
                .Where(x => Direct.Alive(x)).Select(x => $"{x.gameObject.name} ({x.GetIl2CppType().Name}, visible {x.IsVisible}/{Visible(x.gameObject)})")));
    }

    private void CheckContent()
    {
        if (LeftTheGame() is { } left)
        {
            Note("LEFT THE GAME: " + left);
            TakeShot($"{result.N:000}-left");
            leftGame = $"step {result.N} ({result.Step}): {left}";
            return;
        }
        UIPanel top = Closable();
        bool flagged = false;
        GameObject sel = Selected();
        result.Selected = sel is null ? "-" : PathOf(sel.transform, 3) + (sel.activeInHierarchy ? "" : " (inactive)");
        if (step.Do.Equals("closeAll", StringComparison.OrdinalIgnoreCase) && top is not null)
        {
            Note($"STILL SHOWN AFTER CLOSEALL: {top.gameObject.name}");
            flagged = true;
        }
        if (top is not null)
        {
            var texts = new SortedSet<string>(StringComparer.Ordinal);
            var hits = new List<string>();
            foreach (TMPro.TMP_Text t in top.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                string text = t.text?.Trim();
                if (string.IsNullOrEmpty(text) || !Visible(t.gameObject)) continue;
                texts.Add(text.Length > 60 ? text[..60] : text);
                if (templates is not null && templates.Contains(text) && t.GetComponent<LocalizedStaticUILabel>() is null)
                    hits.Add($"{PathOf(t.transform, 2)}='{(text.Length > 40 ? text[..40] : text)}'");
            }
            result.Texts = texts.Take(300).ToList();
            result.Templates = hits.Distinct().ToList();
            result.Lists = string.Join(" ", top.GetComponentsInChildren<ScrollRect>(false).Where(x => x.content is not null)
                .Select(x => $"{x.gameObject.name}:{VisibleRows(x)}"));
            if (top.TryCast<LoadUI>() is { slotList: { } loadList }) Note("rows " + SaveRows(loadList));
            else if (top.TryCast<SaveUI>() is { slotList: { } saveList }) Note("rows " + SaveRows(saveList));
            if (shownWindows.Add(top.gameObject.name)) TakeShot($"{result.N:000}-{Safe(top.gameObject.name)}");
        }
        if (errors > 0) flagged = true;
        if (flagged)
        {
            flaggedSteps++;
            if (result.Shot is null) TakeShot($"{result.N:000}-flag");
        }
    }

    /// <summary>First save rows of a Save / Load list: pool index, save bound, place among its siblings, active,
    /// toggle on; and the selected object.</summary>
    private static string SaveRows(ItemListUI l)
    {
        var rows = l._itemDisplayInstances;
        var parts = new List<string>();
        for (int i = 0; rows is not null && i < Math.Min(4, rows.Count); i++)
        {
            GameObject go = rows[i]?.GameObject;
            if (go is null) { parts.Add($"{i}:null"); continue; }
            SaveSlotUi slot = go.GetComponent<SaveSlotUi>();
            parts.Add($"{i}:{slot?._saveName ?? "?"}@{go.transform.GetSiblingIndex()}{(go.activeSelf ? "" : " off")}{(slot?.toggle?.isOn == true ? " ON" : "")}");
        }
        GameObject sel = Selected();
        return $"[{string.Join(", ", parts)}] count {l._displayedInstanceCount}, selected {(sel is null ? "-" : sel.GetComponent<SaveSlotUi>()?._saveName ?? sel.name)}";
    }

    /// <summary>Rows of a list shown inside its viewport.</summary>
    private static int VisibleRows(ScrollRect sr)
    {
        int n = 0;
        Transform c = sr.content;
        for (int i = 0; i < c.childCount; i++)
            if (c.GetChild(i).gameObject is { activeInHierarchy: true } g && Visible(g)) n++;
        return n;
    }

    private void TakeShot(string name)
    {
        if (request.Monkey is { Shots: false }) return;
        string file = Path.Combine(shotDir, name + ".png");
        ScreenCapture.CaptureScreenshot(file);
        result.Shot = Path.GetFileName(file);
    }

    // ---- the game never saves during a test

    private static bool guardOn, allowLoad;
    private static int blocked;
    private static readonly List<string> blockedCalls = new();

    private static void InstallSaveGuard()
    {
        var harmony = new Harmony(Plugin.Guid + ".uitest");
        var t = typeof(SerializationManager);
        // one prefix per method: a shared prefix reading __originalMethod crashed this game (MethodTimers, 2026-10-07)
        foreach (string name in new[] { "Save", "SaveGlobalSave", "SaveSettingsIfNeeded", "DeleteSave", "Load" })
            harmony.Patch(AccessTools.Method(t, name), prefix: new HarmonyMethod(typeof(UiBench), "Block" + name));
        guardOn = true;
    }

    private static bool BlockSave(ref bool __result)
    {
        if (!guardOn) return true;
        __result = false;
        Blocked("Save");
        return false;
    }

    private static bool BlockSaveGlobalSave() => !guardOn || Blocked("SaveGlobalSave");
    private static bool BlockSaveSettingsIfNeeded() => !guardOn || Blocked("SaveSettingsIfNeeded");
    private static bool BlockDeleteSave() => !guardOn || Blocked("DeleteSave");
    private static bool BlockLoad() => !guardOn || allowLoad || Blocked("Load");

    /// <summary>Counts a blocked call; false = skip the game's method.</summary>
    private static bool Blocked(string what)
    {
        blocked++;
        if (blockedCalls.Count < 20) blockedCalls.Add(what);
        Plugin.Log.LogWarning($"UI test: {what} blocked (the game never saves / loads during a test)");
        return false;
    }
}
