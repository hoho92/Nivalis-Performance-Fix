using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nivalis;
using Nivalis.UI;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool (FadeProbeKey): what one step of a panel fade costs. The pause menu fades its CanvasGroup alpha,
/// and each alpha change tells the whole hierarchy below it (CanvasGroup changed → Selectable.OnCanvasGroupChanged);
/// PIX showed ~40% of the main thread during that fade with only 19 active Selectables out of 1219 (the rest: the
/// save / load rows, inactive). This times alpha changes on P_PauseMenuUI as it is, then with its hidden child panels
/// (save / load popups...) switched off for the measurement, and logs the hierarchy sizes. Everything is restored
/// within the same frame.
/// </summary>
internal static class FadeProbe
{
    private const int Steps = 20;

    internal static void Run()
    {
        try
        {
            UIPanel menu = null;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<UIPanel>(), true))
                if (o.name == "P_PauseMenuUI") { menu = o.Cast<UIPanel>(); break; }
            if (menu is null) { Plugin.Log.LogMessage("FadeProbe: no P_PauseMenuUI (load a game first)"); return; }
            CanvasGroup group = menu._canvasGroup ?? menu.GetComponent<CanvasGroup>();
            if (group is null) { Plugin.Log.LogMessage("FadeProbe: P_PauseMenuUI has no CanvasGroup"); return; }

            Transform root = menu.transform;
            Plugin.Log.LogMessage($"FadeProbe: P_PauseMenuUI active={menu.gameObject.activeInHierarchy} alpha={group.alpha:F2}, " +
                                  $"transforms {Count<Transform>(root, false)} active / {Count<Transform>(root, true)}, " +
                                  $"behaviours {Count<MonoBehaviour>(root, false)} / {Count<MonoBehaviour>(root, true)}, " +
                                  $"selectables {Count<Selectable>(root, false)} / {Count<Selectable>(root, true)}");

            LogHeavy(root, "", 2);

            double asIs = Time(group);

            // child panels that are hidden (invisible) but whose GameObject is still active
            var off = new List<GameObject>();
            foreach (UIPanel p in root.GetComponentsInChildren<UIPanel>(false))
                if (p.Pointer != menu.Pointer && !p.IsVisible && p.gameObject.activeSelf)
                    off.Add(p.gameObject);
            var names = new List<string>();
            foreach (GameObject go in off) { names.Add(go.name); go.SetActive(false); }
            double without;
            try { without = Time(group); }
            finally { foreach (GameObject go in off) go.SetActive(true); }

            Plugin.Log.LogMessage($"FadeProbe: one alpha step {asIs:F3} ms as is, {without:F3} ms with hidden sub-panels off " +
                                  $"({off.Count}: {string.Join(", ", names)})");

            // the row containers of the lists (save / load rows, mostly inactive) moved out of the menu for the
            // measurement: does the fade walk inactive objects too?
            var holder = new GameObject("NivalisPerformanceFix probe");
            holder.SetActive(false);
            var moved = new List<(Transform t, Transform parent, int index)>();
            int movedTransforms = 0;
            try
            {
                foreach (ItemListUI list in root.GetComponentsInChildren<ItemListUI>(true))
                {
                    Transform rows = list._itemDisplayParent;
                    if (rows is null || rows.parent is null) continue;
                    movedTransforms += Count<Transform>(rows, true);
                    moved.Add((rows, rows.parent, rows.GetSiblingIndex()));
                    rows.SetParent(holder.transform, false);
                }
                double outside = Time(group);
                Plugin.Log.LogMessage($"FadeProbe: one alpha step {outside:F3} ms with the rows of {moved.Count} lists " +
                                      $"({movedTransforms} transforms) outside the menu");
            }
            finally
            {
                foreach (var (t, parent, index) in moved)
                {
                    t.SetParent(parent, false);
                    t.SetSiblingIndex(index);
                }
                UnityEngine.Object.Destroy(holder);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"FadeProbe: {e}");
        }
    }

    /// <summary>Median ms of one alpha change (alternating two values around the current one, then restored).</summary>
    private static double Time(CanvasGroup group)
    {
        float alpha = group.alpha;
        float other = alpha > 0.5f ? alpha - 0.01f : alpha + 0.01f;
        var ms = new double[Steps];
        var watch = new Stopwatch();
        for (int i = 0; i < Steps; i++)
        {
            watch.Restart();
            group.alpha = i % 2 == 0 ? other : alpha;
            ms[i] = watch.Elapsed.TotalMilliseconds;
        }
        group.alpha = alpha;
        Array.Sort(ms);
        return ms[Steps / 2];
    }

    /// <summary>Active children holding at least 40 active transforms, two levels deep (what the fade really walks).</summary>
    private static void LogHeavy(Transform t, string indent, int depth)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            Transform c = t.GetChild(i);
            if (!c.gameObject.activeInHierarchy) continue;
            int n = Count<Transform>(c, false);
            if (n < 40) continue;
            var cg = c.GetComponent<CanvasGroup>();
            Plugin.Log.LogMessage($"FadeProbe:   {indent}{c.name}: {n} transforms, {Count<MonoBehaviour>(c, false)} behaviours" +
                                  (cg is null ? "" : $", own CanvasGroup alpha {cg.alpha:F2}"));
            if (depth > 1) LogHeavy(c, indent + "  ", depth - 1);
        }
    }

    private static int Count<T>(Transform root, bool inactive) where T : Component =>
        root.GetComponentsInChildren<T>(inactive).Length;
}
