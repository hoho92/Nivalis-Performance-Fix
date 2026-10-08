using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Menu lists (ItemListUI) named in the config get LazyRows (rows around the view only, uneven heights allowed)
/// without a feature of their own: opening a menu filled and laid out every row at once (quest journal: 36 quests,
/// 52-79 ms frame; bag: 27 items, 38-44 ms; F6 2026-10-07), which also delayed and jerked its opening animation.
/// - The game fills a list with AddItem from index 0 (after BeginUpdate, which cannot be hooked: its Nullable
///   argument breaks the Il2CppInterop trampoline): the first AddItem of a named list starts the lazy rebuild
///   (LazyRows.Begin, rows outside the view parked first), its EndUpdate ends it and the range then follows the view.
/// - Lists are matched by the name of their object and its parent ("JournalEntries/Scroll View"), so a game update
///   that renames one only stops the optimization for it. Grids and lists rebuilt by other features are left alone.
/// - Rows created in advance: the first opening of the journal in a session created its 37 rows (52 ms of a 108 ms
///   frame, F6 2026-10-07). After each scene change the named lists are found and RowPrebuilder makes their rows
///   (parked at once), only behind the loading screen (the game's UI exists ~4 s before it hides); the search runs
///   there or while the game is paused (menus): nothing at all is done in play. The count is raised when a list
///   needs more.
///   Lists named in PreparedLists only get their rows made in advance (recipes: 32 rows made at the first opening,
///   48 ms frame; F6 2026-10-07; its ingredients and equipment: 4 more, 2026-10-08), their rows left
///   inactive like rows the game keeps unused. Same for the quests of the save/load details (25 rows made at the first opening of the Save window, 38 ms frame), limited to 30 rows
///   ('=30': inactive rows left in a rows container cost at each of its layouts).
/// - Bag filters (PlayerInventoryUI.OnFiltersChanged), as for the shops (ListTools.Deferred): handled once at the next
///   tick and only if the filters really changed. The lists' scrollbars are set to simple auto-hide (ListTools).
/// </summary>
internal sealed class LazyLists : Feature
{
    public override string Name => "Menu lists without freeze";
    protected override string Section => "LazyLists";
    protected override string Description =>
        "Keep only the visible rows of the listed menu lists active (quest journal, bag), so opening these menus " +
        "does not freeze and their opening animation stays smooth.";

    private ConfigEntry<string> names, preparedNames;
    private ConfigEntry<int> marginRows, rows;
    private const int DefaultVisible = 12;
    private const int SearchTries = 40;
    private int sceneHandle, delay, tries;
    private bool found;
    // names found in this scene: the search goes on until every named list is found (the save details' quest list
    // lives in the global UI and was found first, before the in-game menu existed: journal, bag and recipe rows were
    // never made, UI test 2026-10-08)
    private readonly HashSet<string> foundNames = new();
    private static readonly ListTools.Deferred<PlayerInventoryUI> bagFilters =
        new(ui => ListTools.FilterState(ui.itemFiltering, ui.toggleGroup));
    private static bool runningBagFilters;
    private static LazyLists self;
    private static readonly HashSet<string> wanted = new();
    private static readonly Dictionary<string, int> prepared = new();  // name -> rows to make (0: the Rows setting)
    private static readonly string[] OldPrepared =
    {
        "Wrapper/ChooseRecipe", "Wrapper/ChooseRecipe;Holder/Scroll View - QuestSection=30",
        // never RecipesMainPanel/RecipeTrees (the recipe category tiles): made in advance, the recipes tab opened
        // with no category set up, an empty list and "Recipe title" (2026-10-08 test build)
        "Wrapper/ChooseRecipe;Holder/Scroll View - QuestSection=30;RecipesMainPanel/RecipeTrees=16;" +
        "DetailsWrapper/DefaultIngredients=8;DetailsWrapper/RequiredEquipment=6",
    };
    // ingredients and equipment of the recipe details: 4 rows made at the first opening of the recipes tab (UI test 2026-10-08)
    private const string DefaultPrepared = "Wrapper/ChooseRecipe;Holder/Scroll View - QuestSection=30;" +
                                           "DetailsWrapper/DefaultIngredients=8;DetailsWrapper/RequiredEquipment=6";
    private static readonly Dictionary<IntPtr, bool> matched = new();   // by ItemListUI: named in the config
    private static ItemListUI current;                                  // started by this feature
    private static ScrollRect currentScroll;
    private static int rangeBegin, rangeEnd;

    protected override void BindSettings(ConfigFile config)
    {
        names = config.Bind(Section, "Lists", "JournalEntries/Scroll View;PlayerInventory/Scroll View",
            "Lists handled, as 'parent/object' names separated by ';' (quest journal; bag).");
        preparedNames = config.Bind(Section, "PreparedLists", DefaultPrepared,
            "Lists whose rows are only made in advance (behind the loading screen or while paused), as 'parent/object' names separated by ';', " +
            "each optionally followed by '=rows' (default: the Rows setting) (recipes, recipe ingredients and equipment; quests of the save/load details).");
        if (Array.IndexOf(OldPrepared, preparedNames.Value) >= 0) preparedNames.Value = DefaultPrepared; // config written by earlier builds
        marginRows = config.Bind(Section, "MarginRows", 3,
            new ConfigDescription("Rows kept ready above and below the visible ones.", new AcceptableValueRange<int>(1, 20)));
        rows = config.Bind(Section, "Rows", 60,
            new ConfigDescription("Rows prepared in advance in each list (raised automatically when a list needs more; 0 = none).",
                new AcceptableValueRange<int>(0, 500)));
    }

    protected override string TryInstall()
    {
        self = this;
        foreach (string n in names.Value.Split(';'))
            if (n.Trim().Length > 0) wanted.Add(n.Trim());
        foreach (string n in preparedNames.Value.Split(';'))
        {
            string[] parts = n.Split('=');
            string key = parts[0].Trim();
            if (key.Length == 0) continue;
            prepared[key] = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int count) ? Math.Max(0, count) : 0;
        }
        if (wanted.Count == 0 && prepared.Count == 0) return "no list named in the config";
        LazyRows.Install();
        // before LazyRows' own AddItem prefix, which needs the rebuild started
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.AddItem)),
            prefix: new HarmonyMethod(typeof(LazyLists), nameof(AddItemPrefix)) { priority = Priority.High });
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.EndUpdate)),
            postfix: new HarmonyMethod(typeof(LazyLists), nameof(EndUpdatePostfix)));
        ListTools.WatchLoadingScreen();
        try
        {
            Plugin.Harmony.Patch(AccessTools.Method(typeof(PlayerInventoryUI), "OnFiltersChanged"),
                prefix: new HarmonyMethod(typeof(LazyLists), nameof(BagFiltersPrefix)));
            Plugin.Harmony.Patch(AccessTools.Method(typeof(PlayerInventoryUI), "RefreshInventoryDisplay"),
                prefix: new HarmonyMethod(typeof(LazyLists), nameof(BagRefreshPrefix)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: bag filters left to the game ({e.Message})");
        }
        return null;
    }

    public override void Tick()
    {
        LazyRows.Tick();
        RunBagFilters();
        if (!Active || rows.Value == 0) return;
        int handle = ListTools.LoadingIndex;
        if (handle != sceneHandle) // new scene: look for the lists again
        {
            sceneHandle = handle;
            delay = 0; tries = 0; found = false;
            foundNames.Clear();
        }
        if (!Quiet()) return; // in play: nothing, not even the search
        if (found || tries >= SearchTries) { RowPrebuilder.Step(); return; }
        if (!ListTools.SearchDue(ref delay)) return;
        if (!ListTools.Loading) tries++; // behind the loading screen the windows may come late
        try
        {
            int queued = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ItemListUI>(), true))
            {
                ItemListUI l = o.TryCast<ItemListUI>();
                if (l is null) continue;
                bool lazy = Named(l);
                int own = 0;
                if (!lazy && !(prepared.TryGetValue(Key(l), out own) && !l.useGridDisplay)) continue;
                foundNames.Add(Key(l));
                int target = own > 0 ? own : rows.Value;
                if ((l._itemDisplayInstances?.Count ?? 0) >= target) continue;
                if (lazy) RowPrebuilder.Grow(l, target, Name, null, _ => LazyRows.ParkLastRow(l), priority: RowPrebuilder.MenuLists);
                else RowPrebuilder.Grow(l, target, Name, null, null, priority: RowPrebuilder.MenuLists);
                queued++;
            }
            found = foundNames.Count >= wanted.Count + prepared.Count;
            if (queued > 0) Plugin.Log.LogInfo($"{Name}: preparing rows for {queued} list(s) ({string.Join(", ", foundNames)} found)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: lists not found ({e.Message})");
        }
    }

    private static void RunBagFilters() => bagFilters.Run(ui =>
    {
        runningBagFilters = true;
        try { ui.OnFiltersChanged(); }
        finally { runningBagFilters = false; }
    });

    protected override void SwitchedOff()
    {
        RunBagFilters(); // a held filter change still happens
        LazyRows.ShowAllOf(this);
    }

    /// <summary>Work may be done now: the loading screen hides it, or the game is paused (menus).</summary>
    private static bool Quiet() => self.Active && ListTools.Quiet;

    /// <summary>A bag filter event: handled once at the next tick (see ListTools.Deferred).</summary>
    private static bool BagFiltersPrefix(PlayerInventoryUI __instance)
    {
        if (runningBagFilters || self == null || !self.Active || __instance is null) return true;
        bagFilters.Add(__instance);
        return false;
    }

    /// <summary>The bag list is rebuilt with the current filters (opening, filter change).</summary>
    private static void BagRefreshPrefix(PlayerInventoryUI __instance)
    {
        if (__instance is not null) bagFilters.Done(__instance);
    }

    private static string Key(ItemListUI l)
    {
        Transform t = l.transform;
        return (t.parent is { } p ? p.name + "/" : "") + t.name;
    }

    private static bool Named(ItemListUI l)
    {
        if (matched.TryGetValue(l.Pointer, out bool yes)) return yes;
        yes = wanted.Contains(Key(l)) && !l.useGridDisplay;
        if (matched.Count > 500) matched.Clear();
        matched[l.Pointer] = yes;
        return yes;
    }

    /// <summary>First AddItem of a fill (index 0) of a named list: the lazy rebuild starts.</summary>
    private static void AddItemPrefix(ItemListUI __instance)
    {
        try
        {
            if (self == null || !self.Active || __instance is null || __instance._displayedInstanceCount != 0) return;
            if (LazyRows.IsBuilding(__instance) || !Named(__instance)) return;
            current = null;
            ScrollRect scroll = __instance.GetComponentInParent<ScrollRect>();
            if (scroll is null) return;
            ListTools.SimplifyScrollbar(scroll);
            int margin = self.marginRows.Value;
            int visible = LazyRows.RowsThatFit(__instance, scroll, margin, DefaultVisible);
            (rangeBegin, rangeEnd) = LazyRows.RangeForRebuild(__instance, margin, visible);
            LazyRows.Park(__instance, rangeBegin, rangeEnd);
            LazyRows.Begin(__instance, rangeBegin, rangeEnd);
            current = __instance;
            currentScroll = scroll;
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: list left to the game ({e.Message})");
            current = null;
        }
    }

    /// <summary>The fill is over (LazyRows replaced the game's EndUpdate): the range follows the view from now on.</summary>
    private static void EndUpdatePostfix(ItemListUI __instance)
    {
        if (current is null || __instance is null || __instance.Pointer != current.Pointer) return;
        ItemListUI l = current;
        current = null;
        try
        {
            int count = LazyRows.End(l);
            if (count > self.rows.Value && self.rows.Value > 0)
            {
                self.rows.Value = Math.Min(500, count + 10); // prepared from the next scene on
                Plugin.Log.LogInfo($"{self.Name}: a list needed {count} rows, now preparing {self.rows.Value}");
            }
            if (count > 0)
                LazyRows.Start(self, l, currentScroll, count, rangeBegin, rangeEnd, self.marginRows.Value, uneven: true,
                    warm: true);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: rows shown all at once ({e.Message})");
            LazyRows.RevealAll(l);
        }
    }
}
