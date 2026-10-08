using System;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis.Locale.UI;
using Nivalis.UI;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The Reviews tab of the business window (LocaleReviewOverviewTab.RefreshReviewList) shows one row per customer
/// review = BeginUpdate, one AddItem + ReviewItemDisplayUi.Show per review, EndUpdate: with a venue that has many
/// reviews, opening the tab laid out every row (~800 ms of layout, F6 layout log 2026-10-07). Only the rows around
/// the view are now active (LazyRows), the others are switched on as they come into view. Reviews have texts of
/// different lengths: LazyRows measures each row as it is switched on and counts the others at the average height
/// (with the whole list shown, a period change still took ~140 ms of layout + ~90-180 ms more when the scrollbar
/// appeared, for 961 reviews).
/// - Period toggles: choosing a period turns one toggle on and another off, and each change rebuilt the whole list
///   (two rebuilds of ~1000-2000 rows in the same frame, the first one thrown away). The list is now rebuilt once,
///   at the next tick, with the final period.
/// - Scrollbar: in AutoHideAndExpandViewport mode the ScrollRect lays its content out again inside its own layout
///   pass to see whether it fits without the scrollbar (490 ms of the ~1.9 s with 1926 reviews). It is set to plain
///   AutoHide: an unneeded scrollbar still hides, without resizing the view (as for the shops).
/// - Scrollbar handle: its size is view / list, a few pixels with ~1000 reviews (hard to grab). The handle is made
///   MinHandle units taller and its sliding area as much shorter, so it never gets smaller than that and still
///   goes from one end of the bar to the other.
/// - Rows made in advance: the game makes a row the first time a period needs it (~0.85 ms each: ~840 rows = a
///   ~0.9 s freeze the first time a long period was shown). When the business window opens, the rows its venue's
///   reviews need are made in advance (RowPrebuilder, one per frame at most, spaced out on slow PCs) and parked,
///   only while the game is paused (menus), so playing never pays for them.
/// </summary>
internal sealed class ReviewRows : Feature
{
    public override string Name => "Business reviews without freeze";
    protected override string Section => "ReviewRows";
    protected override string Description =>
        "Open the Reviews tab of the business window and change its period with less freeze when there are many reviews.";

    private ConfigEntry<int> visibleRows, marginRows;
    private const int DefaultVisible = 15;
    private const float MinHandle = 40f;

    private static ReviewRows self;
    private static int rangeBegin, rangeEnd;
    private static readonly System.Collections.Generic.Dictionary<IntPtr, LocaleReviewOverviewTab> pending = new();
    private static readonly System.Collections.Generic.List<LocaleReviewOverviewTab> pendingNow = new();

    protected override void BindSettings(ConfigFile config)
    {
        visibleRows = config.Bind(Section, "VisibleRows", 0,
            new ConfigDescription("Review rows shown when the list opens (0 = automatic: the rows that fit + the margin).",
                new AcceptableValueRange<int>(0, 200)));
        marginRows = config.Bind(Section, "MarginRows", 4,
            new ConfigDescription("Review rows kept ready above and below the visible ones.", new AcceptableValueRange<int>(1, 50)));
    }

    protected override string TryInstall()
    {
        self = this;
        LazyRows.Install();
        Plugin.Harmony.Patch(AccessTools.Method(typeof(LocaleReviewOverviewTab), "RefreshReviewList"),
            prefix: new HarmonyMethod(typeof(ReviewRows), nameof(RefreshPrefix)),
            postfix: new HarmonyMethod(typeof(ReviewRows), nameof(RefreshPostfix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(LocaleReviewOverviewTab), "OnPeriodToggle"),
            prefix: new HarmonyMethod(typeof(ReviewRows), nameof(PeriodTogglePrefix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(LocaleReviewOverviewTab), "Initialize"),
            postfix: new HarmonyMethod(typeof(ReviewRows), nameof(InitializePostfix)));
        return null;
    }

    public override void Tick()
    {
        LazyRows.Tick();
        RowPrebuilder.Step();
        RefreshPending();
    }

    protected override void SwitchedOff()
    {
        RefreshPending(); // a held period change still happens
        LazyRows.ShowAllOf(this);
    }

    private static void RefreshPending()
    {
        if (pending.Count == 0) return;
        pendingNow.Clear();
        pendingNow.AddRange(pending.Values);
        pending.Clear();
        foreach (LocaleReviewOverviewTab tab in pendingNow)
            if (Native.Direct.Alive(tab)) tab.RefreshReviewList();
    }

    /// <summary>The business window shows a venue: rows for all its reviews made in advance while the game is paused.</summary>
    private static void InitializePostfix(LocaleReviewOverviewTab __instance, Nivalis.GhostSystem.CustomerLoop.Venue venue)
    {
        try
        {
            if (self == null || !self.Active || __instance?.reviewList is not { } l || l.useGridDisplay) return;
            int wanted = venue?.Reviews?.Count ?? 0;
            int have = l._itemDisplayInstances?.Count ?? 0;
            if (wanted <= have) return;
            RowPrebuilder.Grow(l, wanted, self.Name, () => Time.timeScale == 0 && self.Active, _ => LazyRows.ParkLastRow(l));
            Plugin.Log.LogDebug($"{self.Name}: {wanted - have} review rows to prepare (paused={Time.timeScale == 0})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: rows not prepared ({e.Message})");
        }
    }

    /// <summary>OnPeriodToggle = if (!ignorePeriodChanges) RefreshReviewList(): done once at the next tick instead.</summary>
    private static bool PeriodTogglePrefix(LocaleReviewOverviewTab __instance)
    {
        if (self == null || !self.Active || __instance is null || __instance.ignorePeriodChanges) return true;
        pending[__instance.Pointer] = __instance;
        return false;
    }

    private static void SimplifyScrollbar(ScrollRect scroll)
    {
        if (scroll is null) return;
        ListTools.SimplifyScrollbar(scroll);
        ListTools.GrowHandle(scroll.verticalScrollbar, MinHandle);
    }

    private static readonly System.Diagnostics.Stopwatch refreshWatch = new();

    private static void RefreshPrefix(LocaleReviewOverviewTab __instance)
    {
        refreshWatch.Restart();
        LazyRows.Begin(null, 0, 0);
        if (self == null || !self.Active || __instance?.reviewList is not { } l) return;
        pending.Remove(__instance.Pointer); // this rebuild already uses the current period
        SimplifyScrollbar(__instance.scrollRect);
        int margin = self.marginRows.Value;
        int visible = self.visibleRows.Value > 0 ? self.visibleRows.Value
            : LazyRows.RowsThatFit(l, __instance.scrollRect, margin, DefaultVisible);
        (rangeBegin, rangeEnd) = LazyRows.RangeForRebuild(l, margin, visible);
        LazyRows.Park(l, rangeBegin, rangeEnd);
        LazyRows.Begin(l, rangeBegin, rangeEnd);
    }

    private static void RefreshPostfix(LocaleReviewOverviewTab __instance)
    {
        if (__instance?.reviewList is not { } l) return;
        int count = LazyRows.End(l);
        double filled = refreshWatch.Elapsed.TotalMilliseconds;
        if (count > 0)
            LazyRows.Start(self, l, __instance.scrollRect, count, rangeBegin, rangeEnd, self.marginRows.Value, uneven: true);
        Dev.LayoutLog.Note($"RefreshReviewList {count} rows: game {filled:F1} ms, then rows placed {refreshWatch.Elapsed.TotalMilliseconds - filled:F1} ms");
    }
}
