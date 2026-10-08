using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Nivalis;
using Nivalis.UI;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The Save and Load windows are hidden with a CanvasGroup, so their rows (one per save, ~36 objects each, kept for
/// the next opening) stay inside the pause menu. Every step of the pause menu fade (CanvasGroup alpha) is sent to the
/// whole hierarchy below it, inactive objects included: 4.4 ms per frame during the whole fade with 194 saves,
/// 0.04 ms without the rows (FadeProbe, 2026-10-07; the game alone: 0.07 ms, then 2.1 ms after one opening of the
/// Save window). While such a window is closed, its rows container is moved under a holder outside the menu (same
/// canvas, its own CanvasGroup kept at alpha 0); it is put back in place just before the window shows again.
/// When the list keeps its unused rows parked (LazyRows holder, inactive), only that holder is moved, once, and it
/// stays out of the menu: LazyRows moves single rows between it and the list as they come into view. Moving the
/// whole container at each opening and closing cost 30-49 ms each way (its ~20 active rows), moving the holder
/// still 24-41 ms (every row part is marked for layout when moved; F6 2026-10-07).
/// </summary>
internal sealed class SaveRowsParking : Feature
{
    public override string Name => "Save rows out of the pause menu";
    protected override string Section => "SaveRowsParking";
    protected override string Description =>
        "Keep the rows of the closed Save and Load windows out of the pause menu, so its fade does not walk " +
        "thousands of hidden objects at every frame (smooth pause menu with many saves).";

    private sealed class Window
    {
        public UIPanel Panel;
        public ItemListUI List;
        public Transform Rows, Parent;  // rows container and its place while in use
        public int Index;
        public Transform Holder;        // non-null while parked
        public bool HolderOnly;         // only the LazyRows holder is parked: it stays out (no unpark at Show)
        public bool Outside;            // the LazyRows holder already lives outside the window: nothing to park
    }

    private static SaveRowsParking self;
    private static readonly Dictionary<IntPtr, Window> windows = new();   // by panel
    private static readonly HashSet<IntPtr> parkedLists = new();          // ItemListUI pointers
    private static readonly HashSet<IntPtr> others = new();               // panels that are not Save / Load windows
    private static readonly List<IntPtr> dead = new();

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(UIPanel), "SetVisible"),
            prefix: new HarmonyMethod(typeof(SaveRowsParking), nameof(SetVisiblePrefix)) { priority = Priority.First });
        // Show rebuilds (or reuses) the rows before the panel is made visible: the rows must be back first
        foreach (Type t in new[] { typeof(LoadUI), typeof(SaveUI) })
            Plugin.Harmony.Patch(AccessTools.Method(t, "Show"),
                prefix: new HarmonyMethod(typeof(SaveRowsParking), nameof(ShowPrefix)) { priority = Priority.First });
        return null;
    }

    /// <summary>Rows of <paramref name="list"/> are parked (out of view, positions meaningless).</summary>
    public static bool IsParked(ItemListUI list) => list is not null && parkedLists.Contains(list.Pointer);

    private static void ShowPrefix(UIPanel __instance) => Unpark(__instance);

    private static void SetVisiblePrefix(UIPanel __instance, bool isVisible)
    {
        if (self == null) return;
        if (isVisible) { Unpark(__instance); return; }
        // some HUD panels are hidden again at every frame: each panel is checked once
        if (!self.Active || windows.ContainsKey(__instance.Pointer) || others.Contains(__instance.Pointer)) return;
        try
        {
            ItemListUI list = __instance.TryCast<LoadUI>()?.slotList ?? __instance.TryCast<SaveUI>()?.slotList;
            if (list is null) { others.Add(__instance.Pointer); return; }
            windows[__instance.Pointer] = new Window { Panel = __instance, List = list };
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self.Name}: {e.Message}");
        }
    }

    public override void Tick()
    {
        if (others.Count > 500) others.Clear(); // panels of unloaded scenes
        if (windows.Count == 0) return;
        dead.Clear();
        foreach (var (key, w) in windows)
        {
            try
            {
                if (!Direct.Alive(w.Panel) || !Direct.Alive(w.List)) { dead.Add(key); continue; }
                if (!Active) { Unpark(w); continue; }
                if (w.Holder is null && !w.Outside && !w.Panel.IsVisible && Faded(w.Panel)) Park(w);
                // parked whole before its rows were made in advance: behind the loading screen, swap for the
                // holder only (else the first opening moves the whole container back, ~40 ms)
                else if (w.Holder is not null && !w.HolderOnly && ListTools.Loading && LazyRows.HolderOf(w.List) is not null)
                {
                    Unpark(w);
                    Park(w);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: {e.Message}, rows put back");
                try { Unpark(w); } catch (Exception) { }
                dead.Add(key);
            }
        }
        foreach (IntPtr key in dead)
        {
            if (windows.TryGetValue(key, out var w))
            {
                parkedLists.Remove(w.List?.Pointer ?? IntPtr.Zero);
                if (Direct.Alive(w.Holder)) UnityEngine.Object.Destroy(w.Holder.gameObject); // its window is gone
            }
            windows.Remove(key);
        }
    }

    protected override void SwitchedOff()
    {
        foreach (Window w in windows.Values)
            try { if (Direct.Alive(w.List)) Unpark(w); } catch (Exception) { }
    }

    /// <summary>The closing fade is over (rows must stay while the window is still fading out).</summary>
    private static bool Faded(UIPanel p)
    {
        CanvasGroup g = p._canvasGroup;
        return g is null || g.alpha <= 0f;
    }

    private void Park(Window w)
    {
        Transform lazyHolder = LazyRows.HolderOf(w.List);
        if (lazyHolder is not null && !lazyHolder.IsChildOf(w.Panel.transform)) { w.Outside = true; return; }
        Transform rows = lazyHolder ?? w.List._itemDisplayParent;
        if (rows is null || rows.parent is null) return;
        Canvas canvas = w.Panel.GetComponentInParent<Canvas>();
        Transform root = canvas is null ? null : canvas.rootCanvas.transform;
        if (root is null) return;
        var watch = Stopwatch.StartNew();

        var holder = new GameObject("NivalisPerformanceFix parked rows", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
        holder.transform.SetParent(root, false);
        var group = holder.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        w.Rows = rows;
        w.Parent = rows.parent;
        w.Index = rows.GetSiblingIndex();
        rows.SetParent(holder.transform, false);
        w.Holder = holder.transform;
        w.HolderOnly = lazyHolder is not null;
        if (!w.HolderOnly) parkedLists.Add(w.List.Pointer); // the active rows stay in place: the list keeps working
        Dev.LayoutLog.Note($"{Name}: {w.Panel.name} rows parked ({watch.Elapsed.TotalMilliseconds:F1} ms)");
    }

    private static void Unpark(UIPanel panel)
    {
        if (panel is not null && windows.TryGetValue(panel.Pointer, out var w) && !w.HolderOnly) Unpark(w);
    }

    private static void Unpark(Window w)
    {
        if (w.Holder is null) return;
        var watch = Stopwatch.StartNew();
        parkedLists.Remove(w.List.Pointer);
        if (Direct.Alive(w.Rows) && Direct.Alive(w.Parent))
        {
            w.Rows.SetParent(w.Parent, false);
            w.Rows.SetSiblingIndex(w.Index);
        }
        if (Direct.Alive(w.Holder)) UnityEngine.Object.Destroy(w.Holder.gameObject);
        w.Holder = null;
        Dev.LayoutLog.Note($"{self?.Name}: {w.Panel?.name} rows back ({watch.Elapsed.TotalMilliseconds:F1} ms)");
    }
}
