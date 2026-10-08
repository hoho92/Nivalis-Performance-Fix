using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis.UI;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The recipes tab (RecipeUnlocksUiPanel) shows the selected recipe in a details panel switched by
/// ToggleDetailsPanel(show) (details.SetActive(show), placeholder.SetActive(!show); decompiled 2026-10-07).
/// Selecting another recipe first deselects the old one (RecipeSelectedListener(old, false) hides the panel), then
/// selects the new one (shows it again); a category change hides it, fills the list and selects the first recipe
/// (shows it again). So every click switched the whole panel (tags, ingredients, equipment, texts) off and on in
/// the same frame: ~4 ms of OnDisable/OnEnable per click (PIX 2026-10-07).
/// Now a hide is held until the next tick: if the panel was shown again meanwhile, nothing happened; else (a
/// category with no unlocked recipe, the tab closing) it is hidden then, one frame later.
/// </summary>
internal sealed class RecipeDetails : Feature
{
    public override string Name => "Recipe details without flicker";
    protected override string Section => "RecipeDetails";
    protected override string Description =>
        "Do not switch the recipe details panel off and on again at each recipe or category click.";

    private static RecipeDetails self;
    private static readonly Dictionary<IntPtr, RecipeUnlocksUiPanel> pending = new();
    private static readonly List<RecipeUnlocksUiPanel> now = new();
    private static bool hiding;

    protected override string TryInstall()
    {
        if (Il2CppClassPointerStore<RecipeUnlocksUiPanel>.NativeClassPtr == IntPtr.Zero) return "recipes panel class not found";
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(RecipeUnlocksUiPanel), "ToggleDetailsPanel"),
            prefix: new HarmonyMethod(typeof(RecipeDetails), nameof(TogglePrefix)));
        return null;
    }

    private static bool TogglePrefix(RecipeUnlocksUiPanel __instance, bool isSomethingSelected)
    {
        if (hiding || self == null || !self.Active || __instance is null) return true;
        if (isSomethingSelected)
        {
            pending.Remove(__instance.Pointer); // shown again in the same frame: the hide never happens
            return true;
        }
        pending[__instance.Pointer] = __instance;
        return false;
    }

    public override void Tick() => HidePending();

    protected override void SwitchedOff() => HidePending(); // a held hide still happens

    private static void HidePending()
    {
        if (pending.Count == 0) return;
        now.Clear();
        now.AddRange(pending.Values);
        pending.Clear();
        hiding = true;
        try
        {
            foreach (RecipeUnlocksUiPanel panel in now)
                if (Direct.Alive(panel)) panel.ToggleDetailsPanel(false);
        }
        finally { hiding = false; }
    }
}
