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
///   at the first shop of a session). A little after each scene change RowPrebuilder creates them in the
///   background. The count adapts: if a shop ever needs more rows than prepared, the setting is raised.
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
        "Open shops without a freeze: rows are prepared in the background and appear as you scroll.";

    private ConfigEntry<int> rows, visibleRows, marginRows;
    private ConfigEntry<bool> simpleScrollbars;
    private const int DefaultVisible = 25;
    private int sceneHandle, delay, tries;
    private bool found;
    private const int StartDelayFrames = 600, SearchGapFrames = 300, SearchTries = 10;

    private static ShopWindows self;
    private static int rangeBegin, rangeEnd; // rows active after the current Refresh
    // panels whose filters changed: refreshed once at the next tick (a filter click changed two toggles: two Refreshes)
    private static readonly System.Collections.Generic.Dictionary<IntPtr, ShopUiVendorPanel> pending = new();
    private static readonly System.Collections.Generic.List<ShopUiVendorPanel> pendingNow = new();
    // filters each panel was last refreshed with: a filter event that changes nothing (e.g. Delete held in an empty
    // search field fires every frame) does not rebuild the lists again
    private static readonly System.Collections.Generic.Dictionary<IntPtr, string> lastFilters = new();

    protected override void BindSettings(ConfigFile config)
    {
        rows = config.Bind(Section, "Rows", 150,
            new ConfigDescription("Rows prepared in each list of the shop window (raised automatically when a shop needs more).",
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
        Patch(nameof(ShopUiVendorPanel.Refresh), nameof(RefreshPrefix), nameof(RefreshPostfix));
        Patch(nameof(ShopUiVendorPanel.InitializeForVendor), nameof(NewOpening), null);
        Patch(nameof(ShopUiVendorPanel.InitializeForPlayer), nameof(NewOpening), null);
        Patch(nameof(ShopUiVendorPanel.TrySelectItem), nameof(TrySelectItemPrefix), null);
        Patch(nameof(ShopUiVendorPanel.ItemFilteringOnOnFilterChanged), nameof(FilterChangedPrefix), null);
        return null;
    }

    private static void Patch(string method, string prefix, string postfix) =>
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ShopUiVendorPanel), method),
            prefix: new HarmonyMethod(typeof(ShopWindows), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ShopWindows), postfix));

    /// <summary>The window opens for a vendor (or the player's side): forget the rows shown last time.</summary>
    private static void NewOpening(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is { } l) LazyRows.Forget(l.Pointer);
    }

    private static void RefreshPrefix(ShopUiVendorPanel __instance)
    {
        if (__instance is not null)
        {
            pending.Remove(__instance.Pointer); // this Refresh already uses the current filters
            lastFilters[__instance.Pointer] = Filters(__instance);
        }
        LazyRows.Begin(null, 0, 0);
        if (self == null || !self.Active || __instance?.itemsList is not { } l) return;
        int margin = self.marginRows.Value;
        int visible = self.visibleRows.Value > 0 ? self.visibleRows.Value
            : LazyRows.RowsThatFit(l, __instance.listRect, margin, DefaultVisible);
        // window open (filter, sort, purchase): the rows around the current view; opening: the top of the list
        (rangeBegin, rangeEnd) = LazyRows.RangeForRebuild(l, margin, visible);
        LazyRows.Begin(l, rangeBegin, rangeEnd);
    }

    private static void RefreshPostfix(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is not { } l) return;
        int count = LazyRows.End(l);
        if (count == 0) return;
        if (count > self.rows.Value)
        {
            self.rows.Value = Math.Min(1000, count + 20); // prepared from the next scene on
            Plugin.Log.LogInfo($"{self.Name}: a shop list needed {count} rows, now preparing {self.rows.Value}");
        }
        ShopUiVendorPanel panel = __instance;
        LazyRows.Start(self, l, panel.listRect, count, rangeBegin, rangeEnd, self.marginRows.Value,
            () => panel.RefreshNavigation()); // gamepad links between the now active rows
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
        pending[__instance.Pointer] = __instance;
        return false;
    }

    private static void RefreshPending()
    {
        if (pending.Count == 0) return;
        pendingNow.Clear();
        pendingNow.AddRange(pending.Values);
        pending.Clear();
        foreach (ShopUiVendorPanel panel in pendingNow)
            if (Direct.Alive(panel) &&
                !(lastFilters.TryGetValue(panel.Pointer, out string last) && last != null && last == Filters(panel)))
                panel.Refresh();
    }

    /// <summary>
    /// State of every control of a panel's filters (search fields, dropdowns, toggles under its InventoryItemFilteringUi
    /// and ShopUIFiltering, plus the panel's own sorting controls); null if unreadable.
    /// </summary>
    private static string Filters(ShopUiVendorPanel panel)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (Component root in new Component[] { panel.itemFiltering, panel.shopUIFiltering })
            {
                if (root is null) continue;
                foreach (var field in root.GetComponentsInChildren<TMPro.TMP_InputField>(true)) sb.Append(field.text).Append('|');
                foreach (var dropdown in root.GetComponentsInChildren<TMPro.TMP_Dropdown>(true)) sb.Append(dropdown.value).Append('|');
                foreach (var toggle in root.GetComponentsInChildren<Toggle>(true)) sb.Append(toggle.isOn ? '1' : '0');
                sb.Append('#');
            }
            sb.Append(panel.dropdown is { } pd ? pd.value : -1);
            sb.Append('|').Append(panel.upToggle is { } up && up.isOn ? '1' : '0');
            return sb.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Selecting an item from outside (shopping list): it must be among the active rows.</summary>
    private static void TrySelectItemPrefix(ShopUiVendorPanel __instance)
    {
        if (__instance?.itemsList is { } l) LazyRows.RevealAll(l);
    }

    /// <summary>
    /// ScrollRect in AutoHideAndExpandViewport mode lays its content out again (ForceRebuildLayoutImmediate) inside
    /// its own layout pass, up to twice, to see whether the content fits without the scrollbar: ~30% of the shop's
    /// layout cost at each opening. AutoHide still hides an unneeded scrollbar, without resizing the view.
    /// </summary>
    private void SimplifyScrollbars(ShopUINew shop)
    {
        int changed = 0;
        foreach (ScrollRect scroll in shop.GetComponentsInChildren<ScrollRect>(true))
        {
            if (scroll.verticalScrollbarVisibility == ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport)
            {
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                changed++;
            }
            if (scroll.horizontalScrollbarVisibility == ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport)
            {
                scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                changed++;
            }
        }
        if (changed > 0) Plugin.Log.LogInfo($"{Name}: {changed} shop scrollbar(s) set to simple auto-hide");
    }

    // ---- rows created in advance ----

    public override void Tick()
    {
        RefreshPending();
        LazyRows.Tick();
        if (!Active) return;
        int handle = Direct.ActiveSceneHandle;
        if (handle != sceneHandle) // new scene: let it settle, then look for the shop window
        {
            sceneHandle = handle;
            delay = StartDelayFrames; tries = 0; found = false;
            return;
        }
        if (found || tries >= SearchTries) { RowPrebuilder.Step(); return; }
        if (--delay > 0) return;
        delay = SearchGapFrames;
        tries++;
        try
        {
            int queued = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ShopUINew>(), true))
            {
                ShopUINew shop = o.TryCast<ShopUINew>();
                if (shop is null) continue;
                found = true;
                if (simpleScrollbars.Value) SimplifyScrollbars(shop);
                foreach (ShopUiVendorPanel panel in new[] { shop.vendorItems, shop.playerItems })
                    if (panel?.ItemsList is { } list && RowPrebuilder.Add(list, rows.Value, Name)) queued++;
            }
            if (queued > 0) Plugin.Log.LogInfo($"{Name}: preparing rows for {queued} list(s)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: shop window not found ({e.Message})");
        }
    }
}
