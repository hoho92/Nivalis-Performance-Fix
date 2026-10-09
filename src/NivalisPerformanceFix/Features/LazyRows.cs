using System;
using System.Collections.Generic;
using HarmonyLib;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Lists (ItemListUI) that keep only the rows around the view active (a virtualized list). Every ACTIVE row costs
/// at each rebuild (binding its texts marks the layout dirty, ~2 ms per row) and when it is switched off again, so a
/// list of 100-200 rows froze its window at every opening, filter change or purchase.
/// - Rebuild: between Begin and End (the game's BeginUpdate .. AddItem* .. EndUpdate) every row is filled as usual,
///   but only the rows of the given range are active: AddItem is asked for the others as disabled, and EndUpdate is
///   replaced for that list by the same work restricted to the range (rows past the count switched off, rows in
///   the range on; the game's EndUpdate would switch on every row before the count). End returns the real count.
/// - Start: from then on the active range follows the view (scrolling, scrollbar drag, gamepad selection near the
///   edge of the range), with Margin rows on each side; rows entering it are switched on, rows far outside off.
/// - Spacers: an empty LayoutElement before and after the active rows keeps the place of the inactive ones (lists
///   laid out by a VerticalLayoutGroup with rows of one height), so the scrollbar and the view never move.
/// - Uneven rows (texts of different lengths): each row is measured when it is switched on and keeps its height;
///   rows never shown count as the average height. When a measure changes the height above the first row in the
///   view (a row entering above it, or a new average), the content is moved by the difference so the view stays on
///   the same rows. The scrollbar size is then an estimate until every row was seen. Their inactive rows are also
///   parked under an inactive holder: each layout of the list walks every child of the rows container, inactive
///   ones included (rows with a layout group), which cost ~11 ms per frame while scrolling 961 reviews.
/// - Warm lists (short menu lists): a row shown for the first time pays its texts' first mesh (TMP sub-objects),
///   ~9 ms, which made 16-19 ms frames on the first scroll of the journal (F6 2026-10-07). While the game is paused
///   and the view has not moved for WarmIdleFrames, one more row around the view is switched on every WarmGap
///   frames, until all are; rows once on stay on (the range only grows) until the next rebuild.
/// A feature switched off gets its lists fully shown again.
/// </summary>
internal static class LazyRows
{
    public sealed class State
    {
        public Feature Owner;
        public ItemListUI List;
        public ScrollRect Scroll;
        public Action OnShown;               // e.g. gamepad navigation links between the active rows
        public int Total, Begin, End, Margin;
        public float Step, Spacing;          // row height + layout spacing (0: unknown, no spacers)
        public float[] Heights;              // uneven rows: measured height by row (0: not seen); null: one height
        public float KnownSum;               // sum of the measured heights
        public int KnownCount, ChangedFrame = -1;
        public bool Warm;                    // grow the range to every row while the game is paused and the view idle
        public int MovedFrame, WarmFrame;    // last range change for the view; last warm step
        public float Average => KnownCount > 0 ? KnownSum / KnownCount : Step - Spacing;
    }

    private sealed class Spacers
    {
        public LayoutElement Top, Bottom;
        public ItemListUI List;
        public Transform Holder;   // inactive, outside the window (HolderPlace): parked rows (uneven lists)
        public int Base;           // sibling index of the first row in the rows container
        public int Keep;           // pool rows [0, Keep) never parked (see KeepInPlace)
    }

    private static readonly Dictionary<IntPtr, State> states = new();      // by ItemListUI
    private static readonly Dictionary<IntPtr, Spacers> spacers = new();
    private static readonly Dictionary<IntPtr, float> steps = new();       // last measured row step, by ItemListUI
    private static readonly List<IntPtr> keys = new();
    private static readonly HashSet<string> errors = new();
    private static bool installed;
    private static int lastFrame = -1;
    private const int WarmIdleFrames = 30, WarmGap = 3;

    private static IntPtr building;    // ItemListUI being rebuilt

    /// <summary>A rebuild started with Begin is going on for <paramref name="list"/>.</summary>
    public static bool IsBuilding(ItemListUI list) => list is not null && building == list.Pointer;
    private static int rangeBegin, rangeEnd;
    private static int realCount;      // rows the game filled

    public static void Install()
    {
        if (installed) return;
        installed = true;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.AddItem)),
            prefix: new HarmonyMethod(typeof(LazyRows), nameof(AddItemPrefix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.EndUpdate)),
            prefix: new HarmonyMethod(typeof(LazyRows), nameof(EndUpdatePrefix)));
    }

    // ---- rebuild ----

    /// <summary>The game is about to rebuild <paramref name="list"/>: only rows [begin, end) will be active.</summary>
    public static void Begin(ItemListUI list, int begin, int end)
    {
        building = list is not null && !list.useGridDisplay ? list.Pointer : IntPtr.Zero;
        rangeBegin = Math.Max(0, begin);
        rangeEnd = Math.Max(rangeBegin, end);
        realCount = 0;
    }

    /// <summary>The rebuild is over: the number of rows the game filled (0 if <paramref name="list"/> was not being rebuilt).</summary>
    public static int End(ItemListUI list)
    {
        if (building == IntPtr.Zero || list is null || list.Pointer != building) return 0;
        building = IntPtr.Zero;
        int count = realCount > 0 ? realCount : list._displayedInstanceCount;
        realCount = 0;
        return count;
    }

    private static void AddItemPrefix(ItemListUI __instance, ref bool asDisabled)
    {
        if (building == IntPtr.Zero || __instance.Pointer != building) return;
        int index = __instance._displayedInstanceCount;
        if (index < rangeBegin || index >= rangeEnd) asDisabled = true; // filled like the others, but not active
    }

    /// <summary>
    /// ItemListUI.EndUpdate (list mode) restricted to the active range: rows before the count are switched on, the
    /// others off, then the list's layout is marked for rebuild. (The game also calls IReturnToPoolHandler on unused
    /// rows; the save and shop rows do not implement it, and grid lists are never rebuilt here.)
    /// </summary>
    private static bool EndUpdatePrefix(ItemListUI __instance)
    {
        if (building == IntPtr.Zero || __instance.Pointer != building)
        {
            Unpark(__instance.Pointer); // the game shows its rows itself: they must be in their place
            return true;
        }
        int count = __instance._displayedInstanceCount;
        realCount = count;
        try
        {
            // the pool itself: GetItem returns null past the displayed count, so it cannot reach the unused rows
            var rows = __instance._itemDisplayInstances;
            for (int i = rows.Count - 1; i >= 0; i--)
                SetActive(rows[i]?.GameObject, i < count && i >= rangeBegin && i < rangeEnd);
            if (__instance._itemDisplayParent?.TryCast<RectTransform>() is { } parent)
                LayoutRebuilder.MarkLayoutForRebuild(parent);
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"LazyRows: EndUpdate left to the game ({e.Message})");
            building = IntPtr.Zero;
            return true;
        }
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go is not null && go.activeSelf != active) go.SetActive(active);
    }

    // ---- range following the view ----

    /// <summary>
    /// <paramref name="total"/> rows are filled and [<paramref name="begin"/>, <paramref name="end"/>) are active:
    /// from now on the active range follows the view of <paramref name="scroll"/>.
    /// </summary>
    public static void Start(Feature owner, ItemListUI list, ScrollRect scroll, int total, int begin, int end,
                             int margin, Action onShown = null, bool uneven = false, bool warm = false)
    {
        if (list is null) return;
        begin = Math.Max(0, begin);
        end = Math.Max(begin, end);
        if (end > total) // the list got shorter than the view's position (e.g. a smaller category): its last rows
        {
            begin = Math.Max(0, total - (end - begin));
            end = total;
        }
        if (begin == 0 && end >= total)
        {
            // rows filled outside the rebuild range (a range from the previous, longer list's view) were filled
            // switched off: the butcher's 9 offers stayed hidden after a 287-row shop scrolled mid-way (UI test 2026-10-08)
            bool switchedOn = false;
            for (int i = 0; i < total; i++)
                if (list.GetItem(i)?.GameObject is { activeSelf: false } row) { row.SetActive(true); switchedOn = true; }
            if (switchedOn) onShown?.Invoke(); // the game linked the gamepad navigation while they were off
            if (uneven && spacers.TryGetValue(list.Pointer, out var parked) && Direct.Alive(parked.Holder)) Shorten(list, total);
            else Forget(list.Pointer);
            return;
        }
        var s = new State
        {
            Owner = owner, List = list, Scroll = scroll, OnShown = onShown,
            Total = total, Begin = begin, End = end, Margin = margin,
            Warm = warm, MovedFrame = Time.frameCount,
        };
        states[list.Pointer] = s;
        try
        {
            for (int i = 0; i < total; i++) SetActive(list.GetItem(i)?.GameObject, i >= begin && i < end);
            Measure(s);
            if (uneven)
            {
                if (s.Step <= 0) { ShowAll(s); return; } // no VerticalLayoutGroup: rows cannot be placed
                s.Heights = new float[total];
                Arrange(s, 0, int.MaxValue);
                MeasureRows(s, begin, end, 0); // the layout at the end of the frame gives them (Verify)
            }
            UpdateSpacers(s);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{owner.Name}: rows shown all at once ({e.Message})");
            ShowAll(s);
        }
    }

    /// <summary>
    /// The active range a rebuild of <paramref name="list"/> should use now: the rows in the view plus
    /// <paramref name="margin"/> on each side, or [0, <paramref name="fallback"/>) when the list is not followed.
    /// </summary>
    public static (int begin, int end) RangeForRebuild(ItemListUI list, int margin, int fallback)
    {
        if (list is not null && states.TryGetValue(list.Pointer, out var s) && Direct.Alive(s.List) &&
            s.List.gameObject.activeInHierarchy && ViewRange(s) is var (first, last) && last >= first)
            return (Math.Max(0, first - margin), last + 1 + margin);
        return (0, fallback);
    }

    /// <summary>Rows that fill the view of <paramref name="scroll"/> plus <paramref name="margin"/> (row height from the last time).</summary>
    public static int RowsThatFit(ItemListUI list, ScrollRect scroll, int margin, int fallback)
    {
        if (list is null || !steps.TryGetValue(list.Pointer, out float step) || step <= 0 || !Direct.Alive(scroll))
            return fallback;
        RectTransform view = scroll.viewport is { } v ? v : scroll.GetComponent<RectTransform>();
        float height = view.rect.height;
        return height > 0 ? (int)Math.Ceiling(height / step) + margin : fallback;
    }

    /// <summary>Stops following <paramref name="list"/> (the caller rebuilds it or it is shown entirely).</summary>
    public static void Forget(IntPtr list)
    {
        states.Remove(list);
        Unpark(list);
        if (spacers.TryGetValue(list, out var sp))
        {
            if (Direct.Alive(sp.Top) && sp.Top.gameObject.activeSelf) sp.Top.gameObject.SetActive(false);
            if (Direct.Alive(sp.Bottom) && sp.Bottom.gameObject.activeSelf) sp.Bottom.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Stops following <paramref name="list"/> and hides its spacers, its parked rows left parked (a new opening or
    /// an empty fill: the next fill parks and places them anyway; putting them back costs for nothing).
    /// </summary>
    public static void Drop(IntPtr list)
    {
        states.Remove(list);
        if (spacers.TryGetValue(list, out var sp))
        {
            if (Direct.Alive(sp.Top) && sp.Top.gameObject.activeSelf) sp.Top.gameObject.SetActive(false);
            if (Direct.Alive(sp.Bottom) && sp.Bottom.gameObject.activeSelf) sp.Bottom.gameObject.SetActive(false);
        }
    }

    /// <summary>Shows every row of <paramref name="list"/> now (something needs a row that may be inactive).</summary>
    public static void RevealAll(ItemListUI list)
    {
        if (list is not null && states.TryGetValue(list.Pointer, out var s)) ShowAll(s);
    }

    /// <summary>Every list followed for <paramref name="owner"/> shown entirely (the feature is switched off).</summary>
    public static void ShowAllOf(Feature owner)
    {
        keys.Clear();
        foreach (var (key, s) in states) if (s.Owner == owner) keys.Add(key);
        foreach (IntPtr key in keys)
        {
            State s = states[key];
            try { if (Direct.Alive(s.List)) ShowAll(s); else Forget(key); }
            catch (Exception) { Forget(key); }
        }
    }

    private static void ShowAll(State s)
    {
        Forget(s.List.Pointer);
        for (int i = 0; i < s.Total; i++) SetActive(s.List.GetItem(i)?.GameObject, true);
        s.OnShown?.Invoke();
    }

    /// <summary>Moves the active range to [begin, end): rows entering it on, rows leaving it off.</summary>
    private static void SetRange(State s, int begin, int end)
    {
        begin = Math.Max(0, begin);
        end = Math.Min(s.Total, Math.Max(begin, end));
        if (begin == s.Begin && end == s.End) return;
        int anchor = s.Heights != null && ViewRange(s) is var (first, _) ? first : -1; // positions of the last layout
        float before = anchor > 0 ? Above(s, anchor) : 0, totalBefore = anchor > 0 ? Above(s, s.Total) : 0;
        for (int i = s.Begin; i < s.End; i++)
            if (i < begin || i >= end) SetActive(s.List.GetItem(i)?.GameObject, false);
        for (int i = begin; i < end; i++)
            if (i < s.Begin || i >= s.End) SetActive(s.List.GetItem(i)?.GameObject, true);
        int oldBegin = s.Begin, oldEnd = s.End;
        s.Begin = begin;
        s.End = end;
        if (s.Heights != null)
        {
            Arrange(s, Math.Min(oldBegin, begin), Math.Max(oldEnd, end));
            MeasureRows(s, begin, end, anchor);
        }
        UpdateSpacers(s);
        if (anchor > 0) Shift(s, Above(s, anchor) - before, Above(s, s.Total) - totalBefore);
        s.OnShown?.Invoke();
    }

    // ---- uneven rows ----

    private static float HeightOf(State s, int i) => s.Heights[i] > 0 ? s.Heights[i] : s.Average;

    /// <summary>Height taken by rows [0, <paramref name="row"/>) with their spacing, as the spacers and rows lay it out.</summary>
    private static float Above(State s, int row)
    {
        float sum = 0;
        for (int i = 0; i < row; i++) sum += HeightOf(s, i) + s.Spacing;
        return sum;
    }

    /// <summary>
    /// Rows [from, to) were just switched on. Those never measured above row <paramref name="above"/> (the first
    /// row in the view) are measured now, as they move the view; the others get their height from the layout at the
    /// end of the frame (Verify), which lays them out anyway (~0.4 ms per review row measured here).
    /// </summary>
    private static void MeasureRows(State s, int from, int to, int above)
    {
        RectTransform parent = s.List._itemDisplayParent?.TryCast<RectTransform>();
        VerticalLayoutGroup group = parent is null ? null : parent.GetComponent<VerticalLayoutGroup>();
        float width = group is not null && group.childControlWidth ? parent.rect.width - group.padding.horizontal : 0;
        for (int i = from; i < Math.Min(to, above); i++)
        {
            if (s.Heights[i] > 0) continue; // its text did not change since
            if (s.List.GetItem(i)?.GameObject is not { } go) continue;
            var rt = go.GetComponent<RectTransform>();
            // a row never laid out still has the prefab's width: its texts would wrap at the wrong place
            if (width > 0) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt); // texts were just set: their size is not known yet
            float h = LayoutUtility.GetPreferredHeight(rt);
            SetHeight(s, i, h > 0 ? h : rt.rect.height);
        }
        s.ChangedFrame = Time.frameCount;
    }

    private static void SetHeight(State s, int i, float h)
    {
        if (h <= 0) return;
        if (s.Heights[i] > 0) s.KnownSum -= s.Heights[i];
        else s.KnownCount++;
        s.Heights[i] = h;
        s.KnownSum += h;
        s.Step = s.Average + s.Spacing;
        steps[s.List.Pointer] = s.Step;
    }

    /// <summary>
    /// After the layout of a range change: the heights the rows really got (a measure can miss, e.g. a layout that
    /// stretches rows), with the spacers and the view corrected the same way.
    /// </summary>
    private static void Verify(State s)
    {
        if (s.ChangedFrame < 0 || Time.frameCount <= s.ChangedFrame) return; // laid out at the end of that frame
        s.ChangedFrame = -1;
        int anchor = ViewRange(s) is var (first, _) ? first : -1;
        float before = anchor > 0 ? Above(s, anchor) : 0, totalBefore = anchor > 0 ? Above(s, s.Total) : 0;
        bool changed = false;
        for (int i = s.Begin; i < s.End; i++)
        {
            if (s.List.GetItem(i)?.GameObject is not { } go) continue;
            float h = go.GetComponent<RectTransform>().rect.height;
            if (h > 0 && Math.Abs(h - s.Heights[i]) > 0.5f) { SetHeight(s, i, h); changed = true; }
        }
        if (!changed) return;
        UpdateSpacers(s);
        if (anchor > 0) Shift(s, Above(s, anchor) - before, Above(s, s.Total) - totalBefore);
    }

    /// <summary>
    /// The rows above the view got <paramref name="delta"/> taller and the list <paramref name="total"/>: moves the
    /// content so the view keeps its rows (a content resized around a lower pivot also moves its top up).
    /// </summary>
    private static void Shift(State s, float delta, float total)
    {
        if (!Direct.Alive(s.Scroll) || s.Scroll.content is not { } content) return;
        float move = delta - total * (1 - content.pivot.y);
        if (Math.Abs(move) < 0.01f) return;
        var d = new Vector2(0, move); // y grows as the view goes down
        content.anchoredPosition += d;
        s.Scroll.m_ContentStartPosition += d; // a drag in progress goes on from the new place
        s.Scroll.m_PrevPosition += d;         // no inertia from the move
    }

    /// <summary>Once per frame (whichever feature calls first).</summary>
    public static void Tick()
    {
        int frame = Time.frameCount;
        if (frame == lastFrame || states.Count == 0) return;
        lastFrame = frame;
        keys.Clear();
        keys.AddRange(states.Keys);
        foreach (IntPtr key in keys)
        {
            State s = states[key];
            string stage = "alive";
            try
            {
                if (!Direct.Alive(s.List)) { Forget(key); continue; }
                if (!s.Owner.Active) { ShowAll(s); continue; }
                stage = "active";
                if (!s.List.gameObject.activeInHierarchy || SaveRowsParking.IsParked(s.List)) continue;
                if (s.Heights != null) { stage = "verify"; Verify(s); }
                stage = "view";
                if (Wanted(s) is var (begin, end))
                {
                    stage = "range";
                    SetRange(s, begin, end);
                    s.MovedFrame = frame;
                }
                else if (s.Warm)
                {
                    stage = "warm";
                    WarmStep(s, frame);
                }

            }
            catch (Exception e)
            {
                if (errors.Add(stage))
                    Plugin.Log.LogWarning($"{s.Owner.Name}: rows stopped at '{stage}' (rows {s.Begin}-{s.End}/{s.Total}): {e}");
                try { if (Direct.Alive(s.List)) ShowAll(s); else Forget(key); } // never leave rows hidden
                catch (Exception) { Forget(key); }
            }
        }
    }

    /// <summary>
    /// The range the view needs, or null to keep the current one: rows in the view plus the margin, when the view
    /// reaches the edge of the active range or the range is much larger than needed; a batch beyond the edge when
    /// the selection (gamepad) reaches the first or last active rows.
    /// </summary>
    private static (int, int)? Wanted(State s)
    {
        if (ViewRange(s) is var (first, last) && last >= first)
        {
            bool needMore = (first < s.Begin && s.Begin > 0) || (last >= s.End && s.End < s.Total);
            bool tooMany = !s.Warm && (s.Begin < first - 3 * s.Margin || s.End > last + 1 + 3 * s.Margin);
            if (s.Warm && needMore) // warm rows stay on: the range only grows
                return (Math.Min(s.Begin, first - s.Margin), Math.Max(s.End, last + 1 + s.Margin));
            if (needMore || tooMany) return KeepSelection(s, first - s.Margin, last + 1 + s.Margin);
        }
        EventSystem es = EventSystem.current;
        GameObject sel = es is null ? null : es.currentSelectedGameObject;
        if (!Direct.Alive(sel)) return null; // may be a destroyed object (e.g. a dialogue choice): not C# null
        if (s.End < s.Total && IsSelected(s, sel, Math.Max(s.Begin, s.End - 2), s.End))
            return (s.Begin, s.End + s.Margin);
        if (s.Begin > 0 && IsSelected(s, sel, s.Begin, Math.Min(s.End, s.Begin + 2)))
            return (s.Begin - s.Margin, s.End);
        return null;
    }

    /// <summary>
    /// A new range never leaves out the selected row (gamepad): switching it off makes the game drop the selection,
    /// and the controller then has nothing selected (UI test 2026-10-08, Reviews tab: once after d-pad moves the list
    /// stayed mid-way with no row selected). The range is widened to keep it, with the margin around it.
    /// </summary>
    private static (int, int) KeepSelection(State s, int begin, int end)
    {
        // controller only, and near the view: with the mouse a clicked row stays selected while the wheel scrolls far
        // away, and keeping it would keep every row between it and the view on
        if (Nivalis.Singleton<Nivalis.PlayerInputManager>.Instance is not { IsController: true }) return (begin, end);
        EventSystem es = EventSystem.current;
        GameObject sel = es is null ? null : es.currentSelectedGameObject;
        if (!Direct.Alive(sel)) return (begin, end);
        int reach = 4 * s.Margin + (end - begin);
        for (int i = Math.Max(s.Begin, begin - reach); i < Math.Min(s.End, end + reach); i++)
        {
            if (i >= begin && i < end) { i = end - 1; continue; } // rows kept anyway
            if (!IsSelected(s, sel, i, i + 1)) continue;
            return (Math.Min(begin, i - s.Margin), Math.Max(end, i + 1 + s.Margin));
        }
        return (begin, end);
    }

    /// <summary>Warm list, game paused, view idle: one more row switched on, below the range first.</summary>
    private static void WarmStep(State s, int frame)
    {
        if (Time.timeScale != 0 || frame - s.MovedFrame < WarmIdleFrames || frame - s.WarmFrame < WarmGap) return;
        if (s.Begin == 0 && s.End >= s.Total) return;
        if (Direct.Alive(s.Scroll) && s.Scroll.velocity.sqrMagnitude > 1f) { s.MovedFrame = frame; return; }
        s.WarmFrame = frame;
        if (s.End < s.Total) SetRange(s, s.Begin, s.End + 1);
        else SetRange(s, s.Begin - 1, s.End);
    }

    private static bool IsSelected(State s, GameObject sel, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            GameObject row = s.List.GetItem(i)?.GameObject;
            if (Direct.Alive(row) && (sel.Pointer == row.Pointer || sel.transform.IsChildOf(row.transform))) return true;
        }
        return false;
    }

    /// <summary>
    /// Indexes of the first and last rows inside the view, from the position of the first active row (rows have one
    /// height); null when unknown. Clamped to the list.
    /// </summary>
    private static (int first, int last)? ViewRange(State s)
    {
        if (s.Step <= 0 || !Direct.Alive(s.Scroll) || s.Begin >= s.End) return null;
        RectTransform view = s.Scroll.viewport is { } v ? v : s.Scroll.GetComponent<RectTransform>();
        GameObject row = s.List.GetItem(s.Begin)?.GameObject;
        if (!Direct.Alive(row)) return null;
        RectTransform rt = row.GetComponent<RectTransform>();
        float scale = rt.lossyScale.y;
        if (scale <= 0) return null;
        float rowTop = rt.TransformPoint(new Vector3(0, rt.rect.yMax, 0)).y;
        float viewTop = view.TransformPoint(new Vector3(0, view.rect.yMax, 0)).y;
        float viewBottom = view.TransformPoint(new Vector3(0, view.rect.yMin, 0)).y;
        if (s.Heights != null) return UnevenViewRange(s, (rowTop - viewTop) / scale, (viewTop - viewBottom) / scale);
        float step = s.Step * scale;
        int first = s.Begin + (int)Math.Floor((rowTop - viewTop) / step);
        int last = s.Begin + (int)Math.Ceiling((rowTop - viewBottom) / step) - 1;
        return (Math.Max(0, first), Math.Min(s.Total - 1, last));
    }

    /// <summary>
    /// ViewRange for uneven rows. Distances go down from the top of row Begin: the view spans [top, top + height]
    /// (top is negative when the view starts above row Begin); row i spans [y, y + its height].
    /// </summary>
    private static (int first, int last) UnevenViewRange(State s, float top, float height)
    {
        int i = s.Begin;
        float y = 0;
        while (i > 0 && y > top) { i--; y -= HeightOf(s, i) + s.Spacing; }
        while (i < s.Total - 1 && y + HeightOf(s, i) <= top) { y += HeightOf(s, i) + s.Spacing; i++; }
        int first = i;
        while (i < s.Total - 1 && y + HeightOf(s, i) + s.Spacing < top + height) { y += HeightOf(s, i) + s.Spacing; i++; }
        return (first, i);
    }

    // ---- parked rows (uneven lists) ----

    private static Spacers SpacersOf(ItemListUI list)
    {
        if (!spacers.TryGetValue(list.Pointer, out var sp)) spacers[list.Pointer] = sp = new Spacers();
        sp.List = list;
        return sp;
    }

    private static Spacers SpacersOf(State s) => SpacersOf(s.List);

    /// <summary>
    /// Before the game fills an uneven list: pool rows outside [begin, end) parked now. The game fills a parked row
    /// ~50x faster than an inactive row left in the rows container (961 reviews: 7 ms instead of 340-820 ms, F6
    /// 2026-10-07).
    /// </summary>
    public static void Park(ItemListUI list, int begin, int end)
    {
        if (list is null || list.useGridDisplay) return;
        try
        {
            if (Place(list, begin, end, 0, int.MaxValue) is { } sp) Order(list, sp, begin, end);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"LazyRows: rows not parked ({e.Message})");
            Unpark(list.Pointer);
        }
    }

    /// <summary>
    /// The first <paramref name="rows"/> pool rows of <paramref name="list"/> stay in the rows container even when
    /// unused (inactive there). A shop category shorter than the view parked its unused rows and the next, longer
    /// one moved them back: each move of a row (SetParent: parent-changed messages to every part) cost ~4 ms per
    /// filter click (PIX 2026-10-07), while a few inactive rows left in the container cost little at layout.
    /// </summary>
    public static void KeepInPlace(ItemListUI list, int rows)
    {
        if (list is not null && !list.useGridDisplay) SpacersOf(list).Keep = Math.Max(0, rows);
    }

    /// <summary>The inactive holder of the parked rows of <paramref name="list"/>, or null (none parked).</summary>
    public static Transform HolderOf(ItemListUI list) =>
        list is not null && spacers.TryGetValue(list.Pointer, out var sp) && Direct.Alive(sp.Holder) ? sp.Holder : null;

    /// <summary>A row just made in advance (RowPrebuilder) for an uneven list, the last of its pool: parked at once.</summary>
    public static void ParkLastRow(ItemListUI list)
    {
        int count = list?._itemDisplayInstances?.Count ?? 0;
        if (count > 0) Place(list, 0, 0, count - 1, count);
    }

    /// <summary>Pool rows [from, to) go to the rows container if in [begin, end), else to the holder (made if needed).</summary>
    private static Spacers Place(ItemListUI list, int begin, int end, int from, int to)
    {
        Transform parent = list._itemDisplayParent;
        var rows = list._itemDisplayInstances;
        if (parent is null || rows is null) return null;
        Spacers sp = SpacersOf(list);
        if (!Direct.Alive(sp.Holder))
        {
            // created while no row is parked: the first row is still in its place
            sp.Base = rows.Count > 0 && rows[0]?.GameObject is { } first ? first.transform.GetSiblingIndex() : 0;
            var go = new GameObject("NivalisPerformanceFix parked rows");
            go.SetActive(false);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(HolderPlace(parent), false);
            go.transform.SetAsLastSibling();
            sp.Holder = go.transform;
        }
        to = Math.Min(to, rows.Count);
        for (int i = Math.Max(0, from); i < to; i++)
        {
            if (rows[i]?.GameObject is not { } go) continue;
            Transform want = (i >= begin && i < end) || i < sp.Keep ? parent : sp.Holder;
            if (go.transform.parent?.Pointer == want.Pointer) continue;
            // an active row moved re-registers its graphics (~1 ms per row): off first, it is not shown parked
            if (want.Pointer == sp.Holder.Pointer && go.activeSelf) go.SetActive(false);
            go.transform.SetParent(want, false);
        }
        return sp;
    }

    /// <summary>
    /// Where a list's parked rows are kept: next to its top canvas, outside the window. A CanvasGroup change (fade,
    /// tab hidden, window closed) walks the whole hierarchy below it, inactive objects included: ~1000 review rows
    /// parked inside the business window made each closing cost up to 28 ms (UI test 2026-10-08). Same lifetime as
    /// the window (same UI hierarchy).
    /// </summary>
    internal static Transform HolderPlace(Transform rowsContainer)
    {
        Transform top = null;
        for (Transform t = rowsContainer; t is not null; t = t.parent)
            if (t.GetComponent<Canvas>() is not null) top = t;
        if (top is null) return rowsContainer;
        return top.parent ?? top;
    }

    /// <summary>
    /// Pool rows [from, to) go to the rows container if in the active range, else to the holder. Rows leaving the
    /// view are parked at once: left switched off in the rows container, every layout pass still walked them
    /// (shop scroll layout 100-300 → 700-1000 ms per pass, UI test 2026-10-08).
    /// </summary>
    private static void Arrange(State s, int from, int to)
    {
        if (Place(s.List, s.Begin, Math.Min(s.End, s.Total), from, to) is { } sp) Order(s, sp);
    }

    /// <summary>
    /// An uneven list now short enough to be shown entirely: no longer followed, its first <paramref name="total"/>
    /// rows in place and the unused ones left parked (putting them back cost ~185 ms for 957 rows, and the next long
    /// list would fill them slowly).
    /// </summary>
    private static void Shorten(ItemListUI list, int total)
    {
        states.Remove(list.Pointer);
        if (Place(list, 0, total, 0, int.MaxValue) is not { } sp) return;
        if (Direct.Alive(sp.Top) && sp.Top.gameObject.activeSelf) sp.Top.gameObject.SetActive(false);
        if (Direct.Alive(sp.Bottom) && sp.Bottom.gameObject.activeSelf) sp.Bottom.gameObject.SetActive(false);
        Order(list, sp, 0, total);
    }

    /// <summary>Rows container order: [top spacer] rows Begin..End-1 [bottom spacer] (the holder anywhere, inactive).</summary>
    private static void Order(State s, Spacers sp) => Order(s.List, sp, s.Begin, s.End);

    private static void Order(ItemListUI list, Spacers sp, int begin, int end)
    {
        int index = sp.Base;
        if (Direct.Alive(sp.Top) && sp.Top.gameObject.activeSelf) sp.Top.transform.SetSiblingIndex(index++);
        for (int i = begin; i < end; i++)
            if (list.GetItem(i)?.GameObject is { } go) go.transform.SetSiblingIndex(index++);
        if (Direct.Alive(sp.Bottom)) sp.Bottom.transform.SetAsLastSibling();
    }

    /// <summary>Every parked row of <paramref name="list"/> back in the rows container, in the pool's order.</summary>
    private static void Unpark(IntPtr list)
    {
        if (!spacers.TryGetValue(list, out var sp) || !Direct.Alive(sp.Holder) || sp.Holder.childCount == 0) return;
        if (!Direct.Alive(sp.List) || sp.List._itemDisplayParent is not { } parent || sp.List._itemDisplayInstances is not { } rows)
            return;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i]?.GameObject is not { } go) continue;
            if (go.transform.parent?.Pointer != parent.Pointer) go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(sp.Base + i);
        }
    }

    // ---- spacers ----

    /// <summary>Row height + spacing of a list laid out by a VerticalLayoutGroup (else Step stays 0: no spacers).</summary>
    private static void Measure(State s)
    {
        Transform parent = s.List._itemDisplayParent;
        VerticalLayoutGroup group = parent is null ? null : parent.GetComponent<VerticalLayoutGroup>();
        GameObject row = s.List.GetItem(s.Begin)?.GameObject;
        if (group is null || row is null) return;
        RectTransform rt = row.GetComponent<RectTransform>();
        float h = LayoutUtility.GetPreferredHeight(rt);
        if (h <= 0) h = rt.rect.height;
        if (h <= 0) return;
        s.Spacing = group.spacing;
        s.Step = h + group.spacing;
        steps[s.List.Pointer] = s.Step;
    }

    private static void UpdateSpacers(State s)
    {
        if (s.Step <= 0) return;
        Spacers sp = SpacersOf(s);
        // [top spacer] rows Begin..End-1 [bottom spacer]: the layout adds one spacing next to each spacer
        float above = s.Heights != null ? Above(s, s.Begin) : s.Begin * s.Step;
        float below = s.Heights != null ? Above(s, s.Total) - Above(s, s.End) : (s.Total - s.End) * s.Step;
        SetSpacer(s, ref sp.Top, s.Begin, above - s.Spacing, top: true);
        SetSpacer(s, ref sp.Bottom, s.Total - s.End, below - s.Spacing, top: false);
        if (Direct.Alive(sp.Holder)) Order(s, sp);
    }

    private static void SetSpacer(State s, ref LayoutElement sp, int rows, float height, bool top)
    {
        if (!Direct.Alive(sp))
        {
            if (rows <= 0) return;
            var go = new GameObject(top ? "NivalisPerformanceFix rows above" : "NivalisPerformanceFix rows below");
            go.AddComponent<RectTransform>();
            sp = go.AddComponent<LayoutElement>();
            go.transform.SetParent(s.List._itemDisplayParent, false);
        }
        if (rows <= 0) { if (sp.gameObject.activeSelf) sp.gameObject.SetActive(false); return; }
        sp.minHeight = height;
        sp.preferredHeight = height;
        sp.GetComponent<RectTransform>().sizeDelta = new Vector2(0, height); // layouts that do not control child height
        if (!sp.gameObject.activeSelf) sp.gameObject.SetActive(true);
        if (top)
        {
            // just before the first row (the parent may hold other children before the list)
            int index = s.List.GetItem(0)?.GameObject is { } first ? first.transform.GetSiblingIndex() : 0;
            int current = sp.transform.GetSiblingIndex();
            sp.transform.SetSiblingIndex(current < index ? index - 1 : index);
        }
        else
        {
            sp.transform.SetAsLastSibling(); // rows the game creates later are appended after it
        }
    }
}
