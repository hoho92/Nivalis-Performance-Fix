using System;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem.Ai;
using Nivalis.UI;
using Nivalis.UI.InGameMenu.CharacterWindow;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The contacts tab of the in-game menu (CharacterWindowUi) shows one grid cell per character, in row containers
/// (ItemListUI grid: cell i in row i / itemsPerRow; rows the contents do not use are hidden by the game with
/// CanvasGroup alpha 0 + LayoutElement.ignoreLayout, never switched off).
/// - Cells made in advance: the game makes a cell the first time a filter needs it (~0.7 ms each): the first opening
///   made 121 cells (84 ms of a 163 ms frame) and the first switch to a long filter 307 more (225 ms of a 352 ms
///   frame, F6 2026-10-07). When the window is shown, the cells for every known character are made in advance
///   (RowPrebuilder, one per frame at most, spaced out on slow PCs), only while the game is paused (menus).
///   The cells of the first opening (LoadingCells, set from the last first opening) are made behind the loading
///   screen and left off: the first opening made them (Instantiate, ~75 ms; PIX 2026-10-07) because the paused
///   prebuild only starts when it opens. Not more: every extra cell made the first opening slower (its row hidden).
/// - Rows around the view only (a virtualized grid, like LazyRows for lists): every active cell costs at each layout
///   of the grid (the opening animation resizes the list: 22-25 ms per frame with 448 contacts shown) and at each tab
///   change (switched on and off). Only the shown rows in the view plus Margin rows on each side are active, the
///   others are switched off and parked under an inactive holder outside the window (a layout walks every child of
///   the grid, and a closing every object of the window, inactive ones included); spacers keep the place of the shown rows that are off (rows of one height). The
///   range follows scrolling and the gamepad selection. Rows are moved while off (moving active cells cost ~1 ms
///   per row). A cell the game makes for a parked row is made after its row is put back (else its Awake would wait).
/// </summary>
internal sealed class ContactRows : Feature
{
    public override string Name => "Contacts list without freeze";
    protected override string Section => "ContactRows";
    protected override string Description =>
        "Prepare the cells of the contacts list in advance while the game is paused and keep only the rows around " +
        "the view active, so opening the list, changing its filter, scrolling or changing tab does not freeze.";

    private ConfigEntry<int> maxCells, loadingCells;
    private static bool firstFill = true;          // the next fill is the first of the scene
    private static ContactRows self;
    private static CharacterWindowUi window;
    private static ItemListUI list;               // the contacts grid
    private static Transform holder;              // parked rows (inactive, outside the window: never walked)
    private static LayoutElement topSpacer, bottomSpacer;
    private static int baseIndex = -1;            // sibling index of the first row in the grid
    private static int shownRows, begin, end, applyFrame = -1;
    private static float step, spacing;           // row height + layout spacing
    private static bool virtualized;
    private static IntPtr lastSelected;
    private const int Margin = 2;
    private int searchScene, searchWait, searchTries;
    private const int SearchTries = 40;

    protected override void BindSettings(ConfigFile config)
    {
        maxCells = config.Bind(Section, "MaxPreparedCells", 600,
            new ConfigDescription("Most contact cells prepared in advance (0 = none).", new AcceptableValueRange<int>(0, 3000)));
        loadingCells = config.Bind(Section, "LoadingCells", 130,
            new ConfigDescription("Contact cells made behind the loading screen: the ones the first opening shows (set automatically). " +
                                  "The others are made while the game is paused.", new AcceptableValueRange<int>(0, 3000)));
    }

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(CharacterWindowUi), "BeforeDisplay"),
            prefix: new HarmonyMethod(typeof(ContactRows), nameof(BeforeDisplayPrefix)),
            postfix: new HarmonyMethod(typeof(ContactRows), nameof(BeforeDisplayPostfix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.EndUpdate)),
            postfix: new HarmonyMethod(typeof(ContactRows), nameof(EndUpdatePostfix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ItemListUI), nameof(ItemListUI.AddItem)),
            prefix: new HarmonyMethod(typeof(ContactRows), nameof(AddItemPrefix)));
        return null;
    }

    // ---- game hooks ----

    /// <summary>
    /// First opening: the rows are parked before the game fills the grid, so the cells out of view are filled in
    /// inactive rows (no text or graphics work) like at the next openings; filled in the shown grid, the ~120 cells
    /// cost ~50 ms (RefreshCharacterList) + 24 ms (EndUpdate), and only ~18 of them stayed active (UI test 2026-10-08).
    /// </summary>
    private static void BeforeDisplayPrefix(CharacterWindowUi __instance)
    {
        try
        {
            if (self == null || !self.Active || virtualized || __instance?.characterWidgetList is not { } l) return;
            if (list is not null && list.Pointer != l.Pointer) Forget();
            window = __instance;
            list = l;
            if (l._rowData is not { Count: > 0 } rows || l._itemDisplayParent is not { } grid || !Prepare(grid, rows)) return;
            shownRows = 0;
            Apply(0, 0); // every row parked: the game's EndUpdate (Sync) brings the rows of the view back
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static void BeforeDisplayPostfix(CharacterWindowUi __instance)
    {
        try
        {
            if (self == null || !self.Active || __instance?.characterWidgetList is not { } l) return;
            if (list is not null && list.Pointer != l.Pointer) Forget();
            window = __instance;
            list = l;
            int people = Singleton<PersonDataManager>.Instance?.PotentialPeopleCount ?? 0;
            int wanted = Math.Min(people, self.maxCells.Value);
            int have = l._itemDisplayInstances?.Count ?? 0;
            Plugin.Log.LogDebug($"{self.Name}: {people} people, {have} cells, {Math.Max(0, wanted - have)} to prepare (paused={Time.timeScale == 0})");
            if (wanted <= have) return;
            RowPrebuilder.Grow(l, wanted, self.Name, Ready, Made, grid: true, RowPrebuilder.Contacts);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: cells not prepared ({e.Message})");
        }
    }

    /// <summary>The game has filled the grid (its scroll is back at the top): rows from the top of the view.</summary>
    private static void EndUpdatePostfix(ItemListUI __instance)
    {
        if (self == null || !self.Active || list is null || __instance is null || __instance.Pointer != list.Pointer) return;
        if (firstFill)
        {
            firstFill = false;
            int shown = __instance._displayedInstanceCount;
            if (shown > 0 && Math.Abs(shown - self.loadingCells.Value) > 10) self.loadingCells.Value = shown + 5;
        }
        Sync(keep: false);
    }

    /// <summary>
    /// AddItem about to make a new cell (past the pool) in a row that may be parked: the row is put back and on first,
    /// so the cell wakes up like the game's (Awake); the next Sync parks it again.
    /// </summary>
    private static void AddItemPrefix(ItemListUI __instance)
    {
        if (!virtualized || list is null || __instance.Pointer != list.Pointer) return;
        try
        {
            if (__instance._itemDisplayInstances is not { } pool || __instance._displayedInstanceCount < pool.Count) return;
            int index = __instance._displayedInstanceCount;
            Unpark(index / Math.Max(1, __instance.itemsPerRow));
            if (__instance.useDifferentItemsIn4by3) Unpark(index / Math.Max(1, __instance.itemsPerRowIn4by3));
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static void Unpark(int r)
    {
        if (list._rowData is not { } rows || r < 0 || r >= rows.Count || rows[r]?.Transform is not { } t) return;
        if (t.parent?.Pointer != list._itemDisplayParent.Pointer) t.SetParent(list._itemDisplayParent, false);
        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
    }

    /// <summary>Cells may be made now: the game is paused (menus) or behind the loading screen.</summary>
    private static bool Ready() => self.Active && (Time.timeScale == 0 || ListTools.Loading);

    /// <summary>
    /// A cell made in advance. Before the grid was ever shown (loading screen) its rows cannot be parked yet: the
    /// cell is switched off like an unused cell of the game, else the first opening switched off the ~480 cells it
    /// does not show, all at once (EndUpdate 176 ms, F6 2026-10-07).
    /// </summary>
    private static void Made(GameObject cell)
    {
        if (virtualized) Sync(keep: true);
        else if (cell is not null && cell.activeSelf) cell.SetActive(false);
    }

    /// <summary>Behind the loading screen: the contacts grid of the new scene gets its cells made in advance.</summary>
    private void SearchWhileLoading()
    {
        int scene = ListTools.LoadingIndex;
        if (scene != searchScene) { searchScene = scene; searchTries = 0; searchWait = 0; firstFill = true; }
        if (!ListTools.Loading || searchTries >= SearchTries || !ListTools.SearchDue(ref searchWait)) return;
        try
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<CharacterWindowUi>(), true))
            {
                if (o.TryCast<CharacterWindowUi>()?.characterWidgetList is not { } l || !l.useGridDisplay) continue;
                searchTries = SearchTries; // found
                // only the cells of the first opening: every cell more costs there (its row hidden by the game's
                // EndUpdate: 600 cells made, 121 shown -> EndUpdate 102 ms, F6 2026-10-07); the rest while paused
                int wanted = Math.Min(loadingCells.Value, maxCells.Value);
                if (wanted <= (l._itemDisplayInstances?.Count ?? 0)) return;
                RowPrebuilder.Grow(l, wanted, Name, Ready, Made, grid: true, RowPrebuilder.Contacts);
                Plugin.Log.LogInfo($"{Name}: preparing {wanted} contact cells behind the loading screen");
                return;
            }
            searchTries++;
        }
        catch (Exception e)
        {
            searchTries = SearchTries;
            Plugin.Log.LogDebug($"{Name}: contacts window not found ({e.Message})");
        }
    }

    // ---- rows around the view ----

    public override void Tick()
    {
        if (Active && maxCells.Value > 0) SearchWhileLoading();
        RowPrebuilder.Step();
        if (!virtualized) return;
        try
        {
            if (!Direct.Alive(list)) { Forget(); return; }
            if (!Active) { Restore(); return; }
            if (!list.gameObject.activeInHierarchy || Time.frameCount <= applyFrame) return; // laid out at the end of that frame
            var rows = list._rowData;
            // the real row height once laid out (the first measure comes from a row's last size)
            if (begin < end && rows[begin]?.Transform?.TryCast<RectTransform>() is { } rt && rt.rect.height > 0 &&
                Math.Abs(rt.rect.height + spacing - step) > 0.5f)
            {
                step = rt.rect.height + spacing;
                Apply(begin, end);
                return;
            }
            if (ViewRows() is var (first, last))
            {
                bool needMore = (first < begin && begin > 0) || (last >= end && end < shownRows);
                bool tooMany = begin < first - 3 * Margin || end > last + 1 + 3 * Margin;
                if (needMore || tooMany)
                {
                    var (b, e) = KeepSelection(rows, first - Margin, last + 1 + Margin);
                    b = Math.Max(0, Math.Min(b, shownRows));
                    e = Math.Max(b, Math.Min(e, shownRows));
                    if (b == begin && e == end) { FollowSelection(rows); return; } // kept as it is for the selection
                    Apply(b, e);
                    lastSelected = IntPtr.Zero; // the selection is looked at again next frame (it may sit on the new edge)
                    return;
                }
            }
            FollowSelection(rows);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// The gamepad selection went to a row near the edge of the active ones or outside them (the game's navigation
    /// links all cells): the range moves around it.
    /// </summary>
    private static void FollowSelection(Il2CppSystem.Collections.Generic.List<ItemListUI.RowData> rows)
    {
        GameObject sel = EventSystem.current is { } es ? es.currentSelectedGameObject : null;
        if (!Direct.Alive(sel)) { lastSelected = IntPtr.Zero; return; }
        if (sel.Pointer == lastSelected) return;
        lastSelected = sel.Pointer;
        for (int r = 0; r < shownRows; r++)
        {
            if (rows[r]?.Transform is not { } t || !sel.transform.IsChildOf(t)) continue;
            if ((r <= begin && begin > 0) || (r >= end - 1 && end < shownRows) || r < begin || r >= end)
            {
                int fit = RowsThatFit();
                Apply(r - fit / 2 - Margin, r + fit / 2 + 1 + Margin);
            }
            return;
        }
    }

    /// <summary>
    /// A range taken from the view never leaves out the gamepad's selected row: when the view had not followed the
    /// selection yet, the range shrank back to the view, the cells below the selection stayed off and the d-pad
    /// could not go down any more (UI test 2026-10-08: stuck after 23 of 47 rows, once).
    /// </summary>
    private static (int, int) KeepSelection(Il2CppSystem.Collections.Generic.List<ItemListUI.RowData> rows, int b, int e)
    {
        if (Nivalis.Singleton<Nivalis.PlayerInputManager>.Instance is not { IsController: true }) return (b, e);
        GameObject sel = EventSystem.current is { } es ? es.currentSelectedGameObject : null;
        if (!Direct.Alive(sel)) return (b, e);
        for (int r = Math.Max(0, begin); r < Math.Min(end, shownRows); r++)
        {
            if (rows[r]?.Transform is not { } t || !sel.transform.IsChildOf(t)) continue;
            return (Math.Min(b, r - Margin), Math.Max(e, r + 1 + Margin));
        }
        return (b, e);
    }

    /// <summary>First and last shown rows inside the view, from the place of the first active row (rows of one height).</summary>
    private static (int first, int last)? ViewRows()
    {
        if (step <= 0 || begin >= end || window?.charactersListScrollRect is not { } scroll || !Direct.Alive(scroll)) return null;
        if (list._rowData[begin]?.Transform?.TryCast<RectTransform>() is not { } rt) return null;
        RectTransform view = scroll.viewport is { } v ? v : scroll.GetComponent<RectTransform>();
        float scale = rt.lossyScale.y;
        if (scale <= 0) return null;
        float rowTop = rt.TransformPoint(new Vector3(0, rt.rect.yMax, 0)).y;
        float viewTop = view.TransformPoint(new Vector3(0, view.rect.yMax, 0)).y;
        float viewBottom = view.TransformPoint(new Vector3(0, view.rect.yMin, 0)).y;
        float s = step * scale;
        int first = begin + (int)Math.Floor((rowTop - viewTop) / s);
        int last = begin + (int)Math.Ceiling((rowTop - viewBottom) / s) - 1;
        return (Math.Max(0, first), Math.Min(shownRows - 1, last));
    }

    private static int RowsThatFit()
    {
        if (step <= 0 || window?.charactersListScrollRect is not { } scroll) return 6;
        RectTransform view = scroll.viewport is { } v ? v : scroll.GetComponent<RectTransform>();
        return Math.Max(1, (int)Math.Ceiling(view.rect.height / step));
    }

    /// <summary>The game changed the grid (filled it, or a cell was made in advance): rows and spacers to match.</summary>
    private static void Sync(bool keep)
    {
        try
        {
            if (!Direct.Alive(list) || list._rowData is not { } rows || list._itemDisplayParent is not { } grid) return;
            if (!Prepare(grid, rows)) return;
            shownRows = 0;
            for (int r = 0; r < rows.Count; r++)
                if (rows[r] is { } row && Shown(row)) shownRows = r + 1;
            if (keep && virtualized) Apply(begin, end);
            else Apply(0, RowsThatFit() + Margin);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>Shown by the game: not ignored by the layout (its EndUpdate sets ignoreLayout on the rows it hides).</summary>
    private static bool Shown(ItemListUI.RowData row) =>
        row.LayoutElement is { } le ? !le.ignoreLayout : row.CanvasGroup is not { } g || g.alpha > 0;

    /// <summary>Holder, spacers, first row place, row height: false if the grid is not laid out as expected.</summary>
    private static bool Prepare(Transform grid, Il2CppSystem.Collections.Generic.List<ItemListUI.RowData> rows)
    {
        if (grid.GetComponent<VerticalLayoutGroup>() is not { } group) return false;
        spacing = group.spacing;
        if (!Direct.Alive(holder))
        {
            // made while no row is parked: the first row is still in its place
            if (rows.Count == 0 || rows[0]?.Transform is not { } first) return false;
            baseIndex = first.GetSiblingIndex();
            // outside the window (see LazyRows.HolderPlace): parked inside the grid, the ~600 prepared cells were
            // walked at every closing of the in-game menu, whatever its tab (+7-10 ms, UI test 2026-10-08)
            var go = new GameObject("NivalisPerformanceFix parked rows", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
            go.SetActive(false);
            go.transform.SetParent(LazyRows.HolderPlace(grid), false);
            holder = go.transform;
            topSpacer = MakeSpacer(grid, "NivalisPerformanceFix rows above");
            bottomSpacer = MakeSpacer(grid, "NivalisPerformanceFix rows below");
        }
        if (step <= 0)
            for (int r = 0; r < rows.Count && step <= 0; r++) // a row's last laid out size (checked again in Tick)
                if (rows[r]?.Transform?.TryCast<RectTransform>() is { } rt && rt.rect.height > 0) step = rt.rect.height + spacing;
        return step > 0;
    }

    private static LayoutElement MakeSpacer(Transform grid, string name)
    {
        var go = new GameObject(name, Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
        go.SetActive(false);
        go.transform.SetParent(grid, false);
        return go.AddComponent<LayoutElement>();
    }

    /// <summary>Shown rows [b, e) active in the grid, every other row off and parked, spacers for the shown ones that are off.</summary>
    private static void Apply(int b, int e)
    {
        var rows = list._rowData;
        Transform grid = list._itemDisplayParent;
        b = Math.Max(0, Math.Min(b, shownRows));
        e = Math.Max(b, Math.Min(e, shownRows));
        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r]?.Transform is not { } t) continue;
            GameObject go = t.gameObject;
            if (r >= b && r < e)
            {
                if (t.parent?.Pointer != grid.Pointer) t.SetParent(grid, false); // moved while off: cheap
                if (!go.activeSelf) go.SetActive(true);
            }
            else
            {
                if (go.activeSelf) go.SetActive(false);
                if (t.parent?.Pointer != holder.Pointer) t.SetParent(holder, false);
            }
        }
        SetSpacer(topSpacer, b);
        SetSpacer(bottomSpacer, shownRows - e);
        int index = baseIndex;
        if (topSpacer.gameObject.activeSelf) topSpacer.transform.SetSiblingIndex(index++);
        for (int r = b; r < e; r++)
            if (rows[r]?.Transform is { } t) t.SetSiblingIndex(index++);
        if (bottomSpacer.gameObject.activeSelf) bottomSpacer.transform.SetSiblingIndex(index++);
        begin = b;
        end = e;
        virtualized = true;
        applyFrame = Time.frameCount;
    }

    private static void SetSpacer(LayoutElement sp, int rowsOff)
    {
        if (rowsOff <= 0) { if (sp.gameObject.activeSelf) sp.gameObject.SetActive(false); return; }
        float height = rowsOff * step - spacing; // the layout adds one spacing next to the spacer
        sp.minHeight = height;
        sp.preferredHeight = height;
        sp.GetComponent<RectTransform>().sizeDelta = new Vector2(0, height);
        if (!sp.gameObject.activeSelf) sp.gameObject.SetActive(true);
    }

    // ---- back to the game's grid ----

    /// <summary>Every row back in the grid, on and in order, spacers off (feature switched off or something went wrong).</summary>
    private static void Restore()
    {
        virtualized = false;
        if (!Direct.Alive(list) || list._rowData is not { } rows || list._itemDisplayParent is not { } grid) return;
        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r]?.Transform is not { } t) continue;
            if (t.parent?.Pointer != grid.Pointer) t.SetParent(grid, false);
            if (baseIndex >= 0) t.SetSiblingIndex(baseIndex + r);
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
        }
        if (Direct.Alive(topSpacer)) topSpacer.gameObject.SetActive(false);
        if (Direct.Alive(bottomSpacer)) bottomSpacer.gameObject.SetActive(false);
    }

    protected override void SwitchedOff()
    {
        if (virtualized) Restore();
    }

    private static void Fail(Exception e)
    {
        Plugin.Log.LogWarning($"{self?.Name}: rows given back to the game ({e.Message})");
        try { Restore(); } catch (Exception) { virtualized = false; }
        self?.DisableForSession(e.Message);
    }

    /// <summary>The window is gone (new scene) or replaced: its objects go with it.</summary>
    private static void Forget()
    {
        if (Direct.Alive(list)) Restore();
        virtualized = false;
        window = null;
        list = null;
        holder = null;
        topSpacer = bottomSpacer = null;
        baseIndex = -1;
        step = 0;
    }
}
