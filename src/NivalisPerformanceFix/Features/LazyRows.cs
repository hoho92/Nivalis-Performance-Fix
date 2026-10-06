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
    }

    private sealed class Spacers
    {
        public LayoutElement Top, Bottom;
    }

    private static readonly Dictionary<IntPtr, State> states = new();      // by ItemListUI
    private static readonly Dictionary<IntPtr, Spacers> spacers = new();
    private static readonly Dictionary<IntPtr, float> steps = new();       // last measured row step, by ItemListUI
    private static readonly List<IntPtr> keys = new();
    private static readonly HashSet<string> errors = new();
    private static bool installed;
    private static int lastFrame = -1;

    private static IntPtr building;    // ItemListUI being rebuilt
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
    /// rows; the save and shop rows do not implement it, and grid lists are never rebuilt here.) Like the game's, it
    /// clears isUpdating, else the next BeginUpdate logs "ItemListUI was already being updated!".
    /// </summary>
    private static bool EndUpdatePrefix(ItemListUI __instance)
    {
        if (building == IntPtr.Zero || __instance.Pointer != building) return true;
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
            __instance.isUpdating = false; // set by BeginUpdate (game update of 2026-10-06): the next one checks it
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
                             int margin, Action onShown = null)
    {
        if (list is null) return;
        begin = Math.Max(0, begin);
        end = Math.Max(begin, end);
        if (end > total) // the list got shorter than the view's position (e.g. a smaller category): its last rows
        {
            begin = Math.Max(0, total - (end - begin));
            end = total;
        }
        if (begin == 0 && end >= total) { Forget(list.Pointer); return; }
        var s = new State
        {
            Owner = owner, List = list, Scroll = scroll, OnShown = onShown,
            Total = total, Begin = begin, End = end, Margin = margin,
        };
        states[list.Pointer] = s;
        try
        {
            for (int i = 0; i < total; i++) SetActive(list.GetItem(i)?.GameObject, i >= begin && i < end);
            Measure(s);
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
        for (int i = s.Begin; i < s.End; i++)
            if (i < begin || i >= end) SetActive(s.List.GetItem(i)?.GameObject, false);
        for (int i = begin; i < end; i++)
            if (i < s.Begin || i >= s.End) SetActive(s.List.GetItem(i)?.GameObject, true);
        s.Begin = begin;
        s.End = end;
        UpdateSpacers(s);
        s.OnShown?.Invoke();
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
                if (!s.List.gameObject.activeInHierarchy) continue;
                stage = "view";
                if (Wanted(s) is var (begin, end)) { stage = "range"; SetRange(s, begin, end); }
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
            bool tooMany = s.Begin < first - 3 * s.Margin || s.End > last + 1 + 3 * s.Margin;
            if (needMore || tooMany) return (first - s.Margin, last + 1 + s.Margin);
        }
        EventSystem es = EventSystem.current;
        GameObject sel = es is null ? null : es.currentSelectedGameObject;
        if (sel is null) return null;
        if (s.End < s.Total && IsSelected(s, sel, Math.Max(s.Begin, s.End - 2), s.End))
            return (s.Begin, s.End + s.Margin);
        if (s.Begin > 0 && IsSelected(s, sel, s.Begin, Math.Min(s.End, s.Begin + 2)))
            return (s.Begin - s.Margin, s.End);
        return null;
    }

    private static bool IsSelected(State s, GameObject sel, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            GameObject row = s.List.GetItem(i)?.GameObject;
            if (row is not null && (sel.Pointer == row.Pointer || sel.transform.IsChildOf(row.transform))) return true;
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
        if (s.List.GetItem(s.Begin)?.GameObject is not { } row) return null;
        RectTransform rt = row.GetComponent<RectTransform>();
        float scale = rt.lossyScale.y;
        if (scale <= 0) return null;
        float rowTop = rt.TransformPoint(new Vector3(0, rt.rect.yMax, 0)).y;
        float viewTop = view.TransformPoint(new Vector3(0, view.rect.yMax, 0)).y;
        float viewBottom = view.TransformPoint(new Vector3(0, view.rect.yMin, 0)).y;
        float step = s.Step * scale;
        int first = s.Begin + (int)Math.Floor((rowTop - viewTop) / step);
        int last = s.Begin + (int)Math.Ceiling((rowTop - viewBottom) / step) - 1;
        return (Math.Max(0, first), Math.Min(s.Total - 1, last));
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
        if (!spacers.TryGetValue(s.List.Pointer, out var sp)) spacers[s.List.Pointer] = sp = new Spacers();
        // [top spacer] rows Begin..End-1 [bottom spacer]: the layout adds one spacing next to each spacer
        SetSpacer(s, ref sp.Top, s.Begin, top: true);
        SetSpacer(s, ref sp.Bottom, s.Total - s.End, top: false);
    }

    private static void SetSpacer(State s, ref LayoutElement sp, int rows, bool top)
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
        float height = rows * s.Step - s.Spacing;
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
