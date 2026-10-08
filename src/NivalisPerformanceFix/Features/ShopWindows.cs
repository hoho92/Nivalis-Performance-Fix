using System;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The shop window (ShopUINew) shows two lists (ShopUiVendorPanel: the vendor's items, the player's items), each
/// rebuilt by Refresh = BeginUpdate, one AddItem + ShopItemDisplayUI.Initialize per item, EndUpdate,
/// RefreshNavigation. Refresh runs at every opening (InitializeForVendor / InitializeForPlayer), on every filter,
/// sort or search change (ItemFilteringOnOnFilterChanged) and after buying or selling.
/// - Rows created in advance: a list creates its row objects the first time it needs them (~3 ms each, ~200 ms
///   at the first shop of a session). After each scene change RowPrebuilder creates them behind the loading
///   screen or while the game is paused (never in play), parked out of the rows container like the journal's
///   (see LazyRows: inactive rows left in it cost 4-5 ms at each layout of the list, F6 2026-10-07). The count adapts: if a shop ever needs more rows than prepared, the setting is raised.
/// - Only the rows around the view are active (LazyRows): each active row cost ~2 ms at every Refresh, so with the
///   whole list active an opening, a filter change or a purchase froze the game 100-220 ms. Filters and sorting
///   keep working (they Refresh the data, once per frame: see FilterChangedPrefix); a Refresh while the window is
///   open keeps the rows around the current view; TrySelectItem (shopping list) shows the whole list before looking for the item.
/// </summary>
internal sealed class ShopWindows : Feature
{
    public override string Name => "Shop windows without freeze";
    protected override string Section => "ShopRows";
    protected override string Description =>
        "Open shops without a freeze: rows are prepared in advance (loading screen, menus) and appear as you scroll.";

    private ConfigEntry<int> rows, playerRows, visibleRows, marginRows;
    /// <summary>The player's item lists (right side of the shop): their own target, the player carries few items.</summary>
    private static readonly System.Collections.Generic.HashSet<IntPtr> playerPanels = new();
    private ConfigEntry<bool> simpleScrollbars;
    private const int DefaultVisible = 25;
    private int sceneHandle, delay, tries;
    private bool found;
    private const int SearchTries = 40;

    private static ShopWindows self;
    private static int rangeBegin, rangeEnd; // rows active after the current Refresh
    // panels whose filters changed: refreshed once at the next tick, only if the filters really changed
    private static readonly ListTools.Deferred<ShopUiVendorPanel> filters = new(Filters);

    protected override void BindSettings(ConfigFile config)
    {
        rows = config.Bind(Section, "Rows", 150,
            new ConfigDescription("Rows prepared in each list of the shop window (raised automatically when a shop needs more).",
                new AcceptableValueRange<int>(10, 1000)));
        playerRows = config.Bind(Section, "PlayerRows", 40,
            new ConfigDescription("Rows prepared in the shop's list of the player's items (raised automatically when the player carries more).",
                new AcceptableValueRange<int>(10, 1000)));
        visibleRows = config.Bind(Section, "VisibleRows", 0,
            new ConfigDescription("Shop rows shown when a list opens (0 = automatic: the rows that fit in the list + the margin).",
                new AcceptableValueRange<int>(0, 200)));
        marginRows = config.Bind(Section, "MarginRows", 4,
            new ConfigDescription("Shop rows kept ready above and below the visible ones.", new AcceptableValueRange<int>(1, 50)));
        simpleScrollbars = config.Bind(Section, "SimpleScrollbars", true,
            "Shop scrollbars hide when not needed without resizing the list (Unity otherwise lays the list out 2-3 times to decide).");
    }

    protected override string TryInstall()
    {
        if (Il2CppClassPointerStore<ShopUINew>.NativeClassPtr == IntPtr.Zero) return "shop window class not found";
        self = this;
        LazyRows.Install();
        ListTools.WatchLoadingScreen();
        Patch(nameof(ShopUiVendorPanel.Refresh), nameof(RefreshPrefix), nameof(RefreshPostfix));
        Patch(nameof(ShopUiVendorPanel.InitializeForVendor), nameof(NewOpening), null);
        Patch(nameof(ShopUiVendorPanel.InitializeForPlayer), nameof(PlayerOpening), null);
        Patch(nameof(ShopUiVendorPanel.TrySelectItem), nameof(TrySelectItemPrefix), null);
        Patch(nameof(ShopUiVendorPanel.ItemFilteringOnOnFilterChanged), nameof(FilterChangedPrefix), null);
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ShopUINew), nameof(ShopUINew.FilteringChanged)),
            prefix: new HarmonyMethod(typeof(ShopWindows), nameof(ShopFilteringPrefix)));
        return null;
    }

    private static void Patch(string method, string prefix, string postfix) =>
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ShopUiVendorPanel), method),
            prefix: new HarmonyMethod(typeof(ShopWindows), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ShopWindows), postfix));

    /// <summary>The window opens for a vendor (or the player's side): forget the rows shown last time.</summary>
    private static void PlayerOpening(ShopUiVendorPanel __instance)
    {
        if (__instance is not null) playerPanels.Add(__instance.Pointer);
        NewOpening(__instance);
    }

    private static void NewOpening(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is { } l) LazyRows.Drop(l.Pointer);
    }

    private static void RefreshPrefix(ShopUiVendorPanel __instance)
    {
        if (__instance is not null) filters.Done(__instance); // this Refresh already uses the current filters
        LazyRows.Begin(null, 0, 0);
        if (self == null || !self.Active || __instance?.itemsList is not { } l) return;
        int margin = self.marginRows.Value;
        int visible = self.visibleRows.Value > 0 ? self.visibleRows.Value
            : LazyRows.RowsThatFit(l, __instance.listRect, margin, DefaultVisible);
        // window open (filter, sort, purchase): the rows around the current view; opening: the top of the list
        (rangeBegin, rangeEnd) = LazyRows.RangeForRebuild(l, margin, visible);
        LazyRows.KeepInPlace(l, visible); // rows of a short category stay in place for the next one
        LazyRows.Park(l, rangeBegin, rangeEnd); // other unused rows out of the rows container (see LazyRows)
        LazyRows.Begin(l, rangeBegin, rangeEnd);
    }

    /// <summary>
    /// Rows to prepare: at least the largest vendor's item count (+20), known from the save, instead of learning it
    /// at the first opening of that shop (the extra rows were then made in that opening: 118 rows, ~110 ms of the
    /// first shop opening, UI test 2026-10-08). The value is kept in the config like the learned one.
    /// </summary>
    private void RaiseToLargestVendor()
    {
        try
        {
            int largest = 0;
            // never .Instance: it creates the manager when there is none (title screen: a stray economy manager
            // threw at every frame and broke the next save load, UI test 2026-10-08)
            if (Nivalis.Singleton<Nivalis.Economy.EconomyManager>.InstanceNotNull(out var economy) && economy.Vendors is { } vendors)
                foreach (Nivalis.Economy.Vendor v in vendors)
                    if (v?.items is { } items) largest = Math.Max(largest, items.Count);
            if (largest + 20 <= rows.Value) return;
            rows.Value = Math.Min(1000, largest + 20);
            Plugin.Log.LogInfo($"{Name}: the largest vendor has {largest} items, now preparing {rows.Value} rows");
        }
        catch (Exception e) { Plugin.Log.LogDebug($"{Name}: vendors not read ({e.Message})"); }
    }

    private static void RefreshPostfix(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is not { } l) return;
        int count = LazyRows.End(l);
        if (count == 0) { LazyRows.Drop(l.Pointer); return; }
        // vendor lists and the player's list have their own target (the player's list showed 14 items while 287
        // rows were made for it behind the loading screen, UI test 2026-10-08)
        ConfigEntry<int> target = playerPanels.Contains(__instance.Pointer) ? self.playerRows : self.rows;
        if (count > target.Value)
        {
            target.Value = Math.Min(1000, count + 20); // prepared from the next scene on
            Plugin.Log.LogInfo($"{self.Name}: a shop list needed {count} rows, now preparing {target.Value}");
        }
        ShopUiVendorPanel panel = __instance;
        LazyRows.Start(self, l, panel.listRect, count, rangeBegin, rangeEnd, self.marginRows.Value,
            () => panel.RefreshNavigation(), uneven: true); // gamepad links between the now active rows
    }

    /// <summary>
    /// The category toggles, the search field and the sorting all call ItemFilteringOnOnFilterChanged, which only
    /// does Refresh. A click on a category turns one toggle off and another on: two full rebuilds of both lists in
    /// the same frame, the first one thrown away. The Refresh is now done once, at the next tick, with the final
    /// filters (a Refresh the game does itself in the meantime, e.g. at opening, makes it unnecessary).
    /// </summary>
    private static bool FilterChangedPrefix(ShopUiVendorPanel __instance)
    {
        if (self == null || !self.Active || __instance is null) return true;
        filters.Add(__instance);
        return false;
    }

    /// <summary>
    /// The window's own reaction to a filter change (ShopUINew.FilteringChanged: with a controller it selects the
    /// first row and shows its details) must follow the deferred Refresh, as in the game: run before it, it selected
    /// the first row of the list before the filter (details of another item than the row shown selected, UI test
    /// with the Unofficial Patch's Decorations filter and price sorting 2026-10-08). Done at the next tick, after the
    /// panels' Refresh.
    /// </summary>
    private static bool ShopFilteringPrefix(ShopUINew __instance)
    {
        if (runningShop || self == null || !self.Active || __instance is null) return true;
        pendingShop = __instance;
        return false;
    }

    private static ShopUINew pendingShop;
    private static bool runningShop;

    private static void RunPendingShop()
    {
        if (pendingShop is not { } shop) return;
        pendingShop = null;
        runningShop = true;
        try { if (Direct.Alive(shop)) shop.FilteringChanged(); }
        catch (Exception e) { Plugin.Log.LogDebug($"{self?.Name}: shop filter reaction failed ({e.Message})"); }
        finally { runningShop = false; }
    }

    /// <summary>
    /// State of every control of a panel's filters (search fields, dropdowns, toggles under its InventoryItemFilteringUi
    /// and ShopUIFiltering, plus the panel's own sorting controls); null if unreadable.
    /// </summary>
    private static string Filters(ShopUiVendorPanel panel)
    {
        try
        {
            if (ListTools.FilterState(panel.itemFiltering, panel.shopUIFiltering) is not { } controls) return null;
            int sorting = panel.dropdown is { } pd ? pd.value : -1;
            return $"{controls}{sorting}|{(panel.upToggle is { } up && up.isOn ? '1' : '0')}";
        }
        catch (Exception) { return null; }
    }

    /// <summary>Selecting an item from outside (shopping list): it must be among the active rows.</summary>
    private static void TrySelectItemPrefix(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is { } l) LazyRows.RevealAll(l);
    }

    /// <summary>Shop scrollbars in simple auto-hide (see ListTools.SimplifyScrollbar).</summary>
    private void SimplifyScrollbars(ShopUINew shop)
    {
        int changed = 0;
        foreach (ScrollRect scroll in shop.GetComponentsInChildren<ScrollRect>(true)) changed += ListTools.SimplifyScrollbar(scroll);
        if (changed > 0) Plugin.Log.LogInfo($"{Name}: {changed} shop scrollbar(s) set to simple auto-hide");
    }

    // ---- rows created in advance ----

    protected override void SwitchedOff()
    {
        filters.Run(panel => panel.Refresh()); // a held filter change still happens
        RunPendingShop();
        LazyRows.ShowAllOf(this);
    }

    public override void Tick()
    {
        filters.Run(panel => panel.Refresh());
        RunPendingShop(); // after the panels' Refresh, as in the game
        LazyRows.Tick();
        if (!Active) return;
        int handle = ListTools.LoadingIndex;
        if (handle != sceneHandle) // new scene: look for the shop window again
        {
            sceneHandle = handle;
            delay = 0; tries = 0; found = false;
        }
        if (!ListTools.Quiet) return; // in play: nothing, not even the search
        if (found || tries >= SearchTries) { RowPrebuilder.Step(); return; }
        if (!ListTools.SearchDue(ref delay)) return;
        if (!ListTools.Loading) tries++; // behind the loading screen the windows may come late
        try
        {
            int queued = 0;
            RaiseToLargestVendor();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ShopUINew>(), true))
            {
                ShopUINew shop = o.TryCast<ShopUINew>();
                if (shop is null) continue;
                found = true;
                if (simpleScrollbars.Value) SimplifyScrollbars(shop);
                if (shop.playerItems is { } mine) playerPanels.Add(mine.Pointer);
                foreach (ShopUiVendorPanel panel in new[] { shop.vendorItems, shop.playerItems })
                {
                    int wanted = panel is not null && playerPanels.Contains(panel.Pointer) ? playerRows.Value : rows.Value;
                    if (panel?.ItemsList is { } list && (list._itemDisplayInstances?.Count ?? 0) < wanted)
                    {
                        RowPrebuilder.Grow(list, wanted, Name, null, _ => LazyRows.ParkLastRow(list));
                        queued++;
                    }
                }
            }
            if (queued > 0) Plugin.Log.LogInfo($"{Name}: preparing rows for {queued} list(s)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: shop window not found ({e.Message})");
        }
    }
}
