using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Creates the row objects of game list windows (ItemListUI) in advance, at most one row per frame for all
/// features together (fewer on slow PCs: a row that took N x 4 ms is followed by N frames without one): a window creates its rows the first time it needs them (Instantiate + Awake + OnEnable,
/// ~3 ms each), which froze the first opening of the Save window or of a shop. The rows are made exactly like the
/// game does (ItemListUI.CreateNewItemDisplay, which adds them to the list's pool) and left inactive, like rows
/// the game keeps unused: its next AddItem reuses them (by index, so a row appended while the window is shown is
/// harmless).
/// Rows are made only when nobody sees it (ListTools.Quiet): behind the loading screen, several rows per frame up
/// to LoadingMsPerFrame, or while the game is paused (menus) one per frame as above, only for lists whose feature
/// gave a Ready condition (contacts, reviews, saves with their window hidden; rows to be parked are made outside any
/// canvas, see Staging): an open menu is still seen (save rows made in the list while a shop
/// was open gave 18-39 ms frames, F6 2026-10-07); never in play.
/// A list can also wait for a condition (e.g. the game paused) and hand each new row to its feature (e.g. to park it).
/// Grid lists (cells in row containers) are grown through the game's own AddItem, past the shown cells, so a new row
/// container is made exactly as the game does; it is then hidden like the game hides unused rows in EndUpdate
/// (CanvasGroup alpha 0, not interactable, no raycasts, LayoutElement.ignoreLayout).
/// </summary>
internal static class RowPrebuilder
{
    private sealed class Entry
    {
        public ItemListUI List;
        public int Wanted;
        public string Owner;
        public Func<bool> Ready;          // null: always
        public int MadeCount;              // rows made so far, and their time (log: cost per row of each list)
        public double MadeMs;
        public Action<GameObject> Made;   // null: left inactive in the rows container
        public bool Grid;                 // the feature asked for a grid list
        public double LastMs;             // time the last row of this list took
        public int Priority;              // lower first (see Insert)
    }

    /// <summary>
    /// Order of the lists, by how often their window is opened: the loading screen is held 2 s at most and could not
    /// make every row (saves of 2 windows ~680 ms on their own); queued in arrival order, the ~430 save rows went
    /// first and the contacts never had a turn (UI test 2026-10-08).
    /// </summary>
    public const int MenuLists = 0, Contacts = 1, Shops = 2, Saves = 3;

    private static void Insert(Entry e)
    {
        int i = queue.FindIndex(x => x.Priority > e.Priority);
        if (i < 0) queue.Add(e); else queue.Insert(i, e);
    }

    private static readonly List<Entry> queue = new();
    private static readonly HashSet<IntPtr> seen = new();
    private static int lastFrame = -1, scene, wait;
    private static readonly Stopwatch watch = new();
    // a row may cost up to this per frame on average: ~3 ms rows go one per frame, slower PCs space them out
    private const double MsPerFrame = 4.0;
    // behind the loading screen frames are not seen: rows until this much time is spent in the frame
    private static int loadFrames, loadRows;
    private static double loadMs;
    private const double LoadingMsPerFrame = 250.0; // the game itself stalls for hundreds of ms there
    private static bool wasLoading;
    private static readonly Stopwatch frameWatch = new();

    /// <summary>Queues a list to grow to <paramref name="wanted"/> rows; false if it was already queued in this scene.</summary>
    public static bool Add(ItemListUI list, int wanted, string owner, Action<GameObject> made = null, int priority = Shops,
                           Func<bool> ready = null)
    {
        Watch();
        SyncScene();
        if (list is null || !seen.Add(list.Pointer)) return false;
        Insert(new Entry { List = list, Wanted = wanted, Owner = owner, Made = made, Priority = priority, Ready = ready });
        return true;
    }

    /// <summary>
    /// Queues (or updates) a list to grow to <paramref name="wanted"/> rows, built only on frames where
    /// <paramref name="ready"/> is true; each new row goes to <paramref name="made"/>.
    /// </summary>
    public static void Grow(ItemListUI list, int wanted, string owner, Func<bool> ready, Action<GameObject> made,
                            bool grid = false, int priority = Shops)
    {
        Watch();
        SyncScene();
        if (list is null) return;
        seen.Add(list.Pointer);
        foreach (Entry e in queue)
            if (e.List.Pointer == list.Pointer) { e.Wanted = Math.Max(e.Wanted, wanted); return; }
        Insert(new Entry { List = list, Wanted = wanted, Owner = owner, Ready = ready, Made = made, Grid = grid, Priority = priority });
    }

    /// <summary>
    /// The loading screen is watched, and held while rows remain to make that only a loading screen allows (lists
    /// without a Ready condition: shops, saves, menu lists). Lists with one (contacts, reviews) go on while paused:
    /// waiting for them kept the screen up to its 2 s limit (UI test 2026-10-08).
    /// </summary>
    private static void Watch()
    {
        ListTools.WatchLoadingScreen();
        ListTools.LoadingWork ??= () => Missing(loadingOnly: true) > 0;
    }

    /// <summary>New scene: forget the queued lists (the windows may have been replaced; features queue them again).</summary>
    private static void SyncScene()
    {
        int handle = ListTools.LoadingIndex;
        if (handle == scene) return;
        scene = handle;
        queue.Clear();
        seen.Clear();
    }

    /// <summary>Creates one missing row (once per frame, whichever feature calls first).</summary>
    public static void Step()
    {
        int frame = Time.frameCount;
        if (frame == lastFrame) return;
        lastFrame = frame;
        SyncScene();
        if (wasLoading && !ListTools.Loading && queue.Count > 0) // the loading screen just hid
            Plugin.Log.LogInfo($"Rows made in advance: {Missing()} row(s) still to make after the loading screen ({Breakdown()}); " +
                               $"loading frames with work {loadFrames}, rows {loadRows}, {loadMs:F0} ms");
        if (ListTools.Loading && !wasLoading) { loadFrames = loadRows = 0; loadMs = 0; }
        wasLoading = ListTools.Loading;
        if (!ListTools.Quiet) return; // never in play
        if (ListTools.Loading)
        {
            frameWatch.Restart();
            int made = 0;
            while (frameWatch.Elapsed.TotalMilliseconds < LoadingMsPerFrame && MakeOne()) made++;
            if (made > 0) { loadFrames++; loadRows += made; loadMs += frameWatch.Elapsed.TotalMilliseconds; }
            wait = 0;
            return;
        }
        if (wait > 0) { wait--; return; }
        MakeOne();
    }

    private static int Missing(bool loadingOnly = false)
    {
        int n = 0;
        foreach (Entry e in queue)
            if (Direct.Alive(e.List) && e.List._itemDisplayInstances is { } rows && (loadingOnly ? e.Ready == null : e.Ready == null || e.Ready()))
                n += Math.Max(0, e.Wanted - rows.Count);
        return n;
    }

    private static string Breakdown() => string.Join(", ", queue
        .Where(e => Direct.Alive(e.List) && e.List._itemDisplayInstances is { } rows && rows.Count < e.Wanted)
        .Select(e => $"{e.Owner} {e.List.name} {e.Wanted - e.List._itemDisplayInstances.Count}" +
                     (e.MadeCount > 0 ? $" (made {e.MadeCount}, {e.MadeMs / e.MadeCount:F1} ms each)" : " (none made)")));

    /// <summary>Makes one missing row for the first list (by priority) that can have one; false if none could.</summary>
    private static bool MakeOne()
    {
        for (int i = 0; i < queue.Count; i++)
        {
            Entry e = queue[i];
            ItemListUI l = e.List;
            if (!Direct.Alive(l) || l._itemDisplayInstances is not { } rows || rows.Count >= e.Wanted)
            {
                queue.RemoveAt(i--);
                continue;
            }
            if (l.useGridDisplay && !e.Grid) // rows go into row containers made by AddItem: leave those lists to the game
            {
                Plugin.Log.LogInfo($"{e.Owner}: list '{l.name}' uses a grid, not prepared");
                queue.RemoveAt(i--);
                continue;
            }
            if (e.Ready != null && !e.Ready()) continue; // its turn comes later: the next list may go now
            if (!ListTools.Loading && (e.Ready == null || e.LastMs > MsPerFrame)) continue; // behind the next loading screen
            GameObject go = null;
            try
            {
                watch.Restart();
                // while paused, a row that will be parked is made outside the list (Staging): made in the shown rows
                // container it made the container lay its rows out again at each one (~1.3 ms per frame while the
                // reviews were prepared, UI test 2026-10-08); the game uses the parent only for Instantiate. Behind
                // a loading screen rows are made in the list: there their texts are prepared too (made outside any
                // canvas, shop rows paid that while scrolled, 20 frames over 25 ms)
                Transform parent = e.Made != null && !ListTools.Loading ? Staging() : l._itemDisplayParent;
                go = l.useGridDisplay ? MakeGridCell(l) : l.CreateNewItemDisplay(parent)?.GameObject;
                if (go is null) continue; // e.g. a grid the game is filling: the next list may go now
                if (!l.useGridDisplay) go.SetActive(false); // like a row the game keeps unused (grid: MakeGridCell decides)
                e.Made?.Invoke(go);
                if (go.transform.parent?.Pointer == staging?.Pointer) go.transform.SetParent(l._itemDisplayParent, false); // not parked
                e.LastMs = watch.Elapsed.TotalMilliseconds;
                e.MadeCount++;
                e.MadeMs += e.LastMs;
                wait = (int)(e.LastMs / MsPerFrame); // frames to skip before the next row
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"{e.Owner}: row prebuild stopped ({ex.Message})");
                queue.RemoveAt(i);
                // a row left in the staging object (outside every window, kept across scenes) would never be freed;
                // in its list it is an unused row of the pool, like the others
                try
                {
                    if (Direct.Alive(go) && Direct.Alive(staging) && go.transform.parent?.Pointer == staging.Pointer)
                    {
                        go.SetActive(false);
                        if (Direct.Alive(l)) go.transform.SetParent(l._itemDisplayParent, false);
                        else UnityEngine.Object.Destroy(go);
                    }
                }
                catch (Exception) { }
            }
            return true;
        }
        return false;
    }

    private static Transform staging;

    /// <summary>
    /// Active object outside every canvas where rows to be parked are made: their Awake / OnEnable run as in the
    /// list, but no layout or canvas sees them.
    /// </summary>
    private static Transform Staging()
    {
        if (Direct.Alive(staging)) return staging;
        var go = new GameObject("NivalisPerformanceFix row staging");
        go.AddComponent<RectTransform>();
        UnityEngine.Object.DontDestroyOnLoad(go);
        return staging = go.transform;
    }

    /// <summary>
    /// One more cell for a grid list, made by the game's AddItem as if the list were being filled past its shown
    /// cells (count and isUpdating put back after); row containers it had to make are hidden like unused rows.
    /// </summary>
    private static GameObject MakeGridCell(ItemListUI l)
    {
        if (l.isUpdating) return null; // the game is filling it: next frame
        int shown = l._displayedInstanceCount;
        var rowData = l._rowData;
        int rowsBefore = rowData?.Count ?? 0;
        IItemDisplayUI item;
        l._displayedInstanceCount = l._itemDisplayInstances.Count; // the next cell is a new one
        l.isUpdating = true;
        try
        {
            item = l.AddItem(true, true);
        }
        finally
        {
            l._displayedInstanceCount = shown;
            l.isUpdating = false;
        }
        if (rowData is not null)
            for (int r = rowsBefore; r < rowData.Count; r++)
            {
                var row = rowData[r];
                if (row?.CanvasGroup is { } group)
                {
                    group.alpha = 0f;
                    group.interactable = false;
                    group.blocksRaycasts = false;
                }
                if (row?.LayoutElement is { } layout) layout.ignoreLayout = true;
            }
        GameObject cell = item?.GameObject;
        // in a hidden row, the cell is left active like the cells of rows the game hid: showing it later costs no
        // activation (OnEnable of its graphics: 121 ms for 448 cells, F6 2026-10-07); in a shown row it stays off
        if (cell is not null && cell.transform.parent?.GetComponent<LayoutElement>() is { ignoreLayout: true }) cell.SetActive(true);
        return cell;
    }
}
