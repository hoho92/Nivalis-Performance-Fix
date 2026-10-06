using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Creates the row objects of game list windows (ItemListUI) in advance, at most one row per frame for all
/// features together (fewer on slow PCs: a row that took N x 4 ms is followed by N frames without one): a window creates its rows the first time it needs them (Instantiate + Awake + OnEnable,
/// ~3 ms each), which froze the first opening of the Save window or of a shop. The rows are made exactly like the
/// game does (ItemListUI.CreateNewItemDisplay, which adds them to the list's pool) and left inactive, like rows
/// the game keeps unused: its next AddItem reuses them (by index, so a row appended while the window is shown is
/// harmless).
/// </summary>
internal static class RowPrebuilder
{
    private static readonly List<(ItemListUI list, int wanted, string owner)> queue = new();
    private static readonly HashSet<IntPtr> seen = new();
    private static int lastFrame = -1, scene, wait;
    private static readonly Stopwatch watch = new();
    // a row may cost up to this per frame on average: ~3 ms rows go one per frame, slower PCs space them out
    private const double MsPerFrame = 4.0;

    /// <summary>Queues a list to grow to <paramref name="wanted"/> rows; false if it was already queued in this scene.</summary>
    public static bool Add(ItemListUI list, int wanted, string owner)
    {
        SyncScene();
        if (list is null || !seen.Add(list.Pointer)) return false;
        queue.Add((list, wanted, owner));
        return true;
    }

    /// <summary>New scene: forget the queued lists (the windows may have been replaced; features queue them again).</summary>
    private static void SyncScene()
    {
        int handle = Direct.ActiveSceneHandle;
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
        if (wait > 0) { wait--; return; }
        while (queue.Count > 0)
        {
            var (l, wanted, owner) = queue[0];
            if (!Direct.Alive(l) || l._itemDisplayInstances is not { } rows || rows.Count >= wanted)
            {
                queue.RemoveAt(0);
                continue;
            }
            if (l.useGridDisplay) // rows go into row containers made by AddItem: leave those lists to the game
            {
                Plugin.Log.LogInfo($"{owner}: list '{l.name}' uses a grid, not prepared");
                queue.RemoveAt(0);
                continue;
            }
            try
            {
                watch.Restart();
                GameObject go = l.CreateNewItemDisplay(l._itemDisplayParent)?.GameObject;
                if (go is not null) go.SetActive(false); // like a row the game keeps unused
                wait = (int)(watch.Elapsed.TotalMilliseconds / MsPerFrame); // frames to skip before the next row
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{owner}: row prebuild stopped ({e.Message})");
                queue.RemoveAt(0);
            }
            return;
        }
    }
}
