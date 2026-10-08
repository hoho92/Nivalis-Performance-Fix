using System;
using HarmonyLib;
using Nivalis;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: logs every UIPanel shown or hidden (UIPanel.SetVisible), with its Selectables and whether it
/// fades. A CanvasGroup alpha fade tells every Selectable below it at each frame (Selectable.OnCanvasGroupChanged),
/// so this finds which panels make their fades cost (PIX, 2026-10-07: +5 ms per frame for 0.75 s around dialogues).
/// </summary>
internal static class PanelLog
{
    internal static void Start()
    {
        try
        {
            new Harmony(Plugin.Guid + ".panellog").Patch(AccessTools.Method(typeof(UIPanel), "SetVisible"),
                postfix: new HarmonyMethod(typeof(PanelLog), nameof(Postfix)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"PanelLog: unavailable: {e.Message}");
        }
    }

    private static IntPtr lastPanel;
    private static bool lastVisible;

    private static void Postfix(UIPanel __instance, bool isVisible)
    {
        try
        {
            // some HUD panels are hidden again at every frame (P_CustomerInfo): log changes only
            if (__instance.Pointer == lastPanel && isVisible == lastVisible) return;
            lastPanel = __instance.Pointer;
            lastVisible = isVisible;
            int all = __instance.GetComponentsInChildren<Selectable>(true).Length;
            int active = __instance.GetComponentsInChildren<Selectable>(false).Length;
            bool fades = __instance._activeTween is not null;
            Plugin.Log.LogInfo($"PanelLog t={Time.realtimeSinceStartup:F2} {Path(__instance.transform)} " +
                               $"{(isVisible ? "shown" : "hidden")} selectables={active} active/{all}{(fades ? " fade" : "")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"PanelLog: {e.Message}");
        }
    }

    private static string Path(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p is not null && path.Length < 120; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
