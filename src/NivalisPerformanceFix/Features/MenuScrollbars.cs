using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Every scrollable list of the game's windows in simple auto-hide (ListTools.SimplifyScrollbar): in
/// AutoHideAndExpandViewport mode a ScrollRect lays its content out again inside its own layout pass, up to twice,
/// at every layout of the list. The skills tab paid it at each frame of its rows' animation (168 ms over ~25
/// frames, 17 ms frames; F6 2026-10-07), the recipes tab while idle. An unneeded scrollbar still hides; the view
/// just keeps its width.
/// - AutoHide itself switched the scrollbar's object on and off (ScrollRect.UpdateOneScrollbarVisibility), and a
///   child of a ScrollRect switched on or off makes the whole window's layout dirty (the ScrollRect counts as a
///   layout group): the frame after a shop filter or a list change re-laid the whole shop out (8-10 ms) or the
///   recipes tab, made dirty by 'Scrollbar Vertical' (F6 2026-10-07). An auto-hidden scrollbar now stays on and is
///   hidden by a CanvasGroup (invisible, not clickable, skipped by the gamepad navigation).
/// The scroll views are looked for only behind each loading screen: nothing is done in play nor in the menus.
/// </summary>
internal sealed class MenuScrollbars : Feature
{
    public override string Name => "Simple menu scrollbars";
    protected override string Section => "MenuScrollbars";
    protected override string Description =>
        "Scrollbars of the game's windows hide without resizing their list, which avoids laying the list out " +
        "several times (skills tab animation, recipes, shops...).";

    private const int SearchTries = 40;
    private int sceneHandle, delay, tries;
    private bool loadingPass;

    private static MenuScrollbars self;
    private static readonly Dictionary<IntPtr, CanvasGroup> groups = new();   // by Scrollbar
    private static readonly Dictionary<IntPtr, bool> shown = new();

    protected override string TryInstall()
    {
        self = this;
        ListTools.WatchLoadingScreen();
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ScrollRect), "UpdateOneScrollbarVisibility"),
            prefix: new HarmonyMethod(typeof(MenuScrollbars), nameof(VisibilityPrefix)));
        return null;
    }

    /// <summary>AutoHide: the scrollbar stays on, shown or hidden by its CanvasGroup (no layout).</summary>
    private static bool VisibilityPrefix(bool xScrollingNeeded, ScrollRect.ScrollbarVisibility scrollbarVisibility,
                                         Scrollbar scrollbar)
    {
        if (scrollbarVisibility != ScrollRect.ScrollbarVisibility.AutoHide || scrollbar is null || self == null) return true;
        if (!self.Active)
        {
            // switched off: a scrollbar hidden by its CanvasGroup is given back to the game shown (it switches the
            // object on and off itself), else a list that needs it later would have an invisible scrollbar
            if (groups.Count > 0) ShowAgain(scrollbar.Pointer);
            return true;
        }
        try
        {
            IntPtr key = scrollbar.Pointer;
            if (shown.TryGetValue(key, out bool was) && was == xScrollingNeeded && groups.TryGetValue(key, out var known) &&
                Direct.Alive(known))
                return false; // nothing changed (every frame)
            if (!groups.TryGetValue(key, out CanvasGroup group) || !Direct.Alive(group))
            {
                if (groups.Count > 500) { groups.Clear(); shown.Clear(); }
                group = scrollbar.GetComponent<CanvasGroup>() ?? scrollbar.gameObject.AddComponent<CanvasGroup>();
                groups[key] = group;
            }
            GameObject go = scrollbar.gameObject;
            if (!go.activeSelf) go.SetActive(true); // once: hidden the game's way before
            group.alpha = xScrollingNeeded ? 1f : 0f;
            group.interactable = xScrollingNeeded;
            group.blocksRaycasts = xScrollingNeeded;
            shown[key] = xScrollingNeeded;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static void ShowAgain(IntPtr key)
    {
        if (!groups.Remove(key, out CanvasGroup group)) return;
        shown.Remove(key);
        if (!Direct.Alive(group)) return;
        group.alpha = 1f;
        group.interactable = true;
        group.blocksRaycasts = true;
    }

    protected override void SwitchedOff()
    {
        foreach (IntPtr key in new List<IntPtr>(groups.Keys)) ShowAgain(key);
    }

    public override void Tick()
    {
        if (!Active) return;
        int handle = ListTools.LoadingIndex;
        if (handle != sceneHandle)
        {
            sceneHandle = handle;
            delay = 0; tries = 0; loadingPass = false;
        }
        // one pass behind each loading screen. No pass at the first pause any more: it changed nothing (all found
        // while loading) and its search of every scroll view cost ~24 ms in the first menu opening (UI test
        // 2026-10-08); a window made later keeps the CanvasGroup auto-hide patch and is simplified at the next loading
        if (!ListTools.Loading || loadingPass || tries >= SearchTries) return;
        if (!ListTools.SearchDue(ref delay)) return;
        try
        {
            int found = 0, changed = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<ScrollRect>(), true))
            {
                if (o.TryCast<ScrollRect>() is not { } scroll) continue;
                found++;
                changed += ListTools.SimplifyScrollbar(scroll);
            }
            if (found == 0) return; // the windows are not made yet
            loadingPass = true;
            if (changed > 0) Plugin.Log.LogInfo($"{Name}: {changed} scrollbar(s) of {found} scroll views set to simple auto-hide");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{Name}: scroll views not found ({e.Message})");
        }
    }
}
