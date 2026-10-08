using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.Fishing;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The fish database tab (FishDatabaseUI) destroyed all its rows when hidden (SetInvisibleImmediate) and, at every
/// opening, destroyed them again and instantiated one spacer + one holder per tier and one entry per fish
/// (BeforeDisplay, decompiled 2026-10-07): 56-60 ms at each opening (F6 2026-10-07).
/// Now the rows are kept when the tab is hidden; at the next opening, if the tiers still have the same fishes,
/// the game's BeforeDisplay runs with no rows to destroy and no tier to build (it still writes the progress and
/// selects the first entry), then each kept entry is refreshed (FishDatabaseEntryUI.Refresh, as the game does
/// for a new one) - only when the caught or available fishes changed since (the entries are ILocalizable: a
/// language change updates them by itself). Anything unexpected: the game builds the rows itself, as before.
/// </summary>
internal sealed class FishDatabaseRows : Feature
{
    public override string Name => "Fish database without freeze";
    protected override string Section => "FishDatabaseRows";
    protected override string Description =>
        "Keep the rows of the fish database tab instead of destroying and making them again at each opening.";

    private static FishDatabaseRows self;
    private static readonly Dictionary<IntPtr, Il2CppSystem.Collections.Generic.List<GameObject>> kept = new();
    private static Il2CppSystem.Collections.Generic.List<GameObject> stash;
    private static FishDatabase stashDb;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FishTier> stashTiers;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FishTier> noTiers;
    private static readonly Dictionary<IntPtr, long> shownState = new();   // by FishDatabaseUI: caught/available counts
    private static readonly System.Diagnostics.Stopwatch watch = new();

    protected override string TryInstall()
    {
        if (Il2CppClassPointerStore<FishDatabaseUI>.NativeClassPtr == IntPtr.Zero) return "fish database class not found";
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(FishDatabaseUI), nameof(FishDatabaseUI.SetInvisibleImmediate)),
            prefix: new HarmonyMethod(typeof(FishDatabaseRows), nameof(HidePrefix)),
            postfix: new HarmonyMethod(typeof(FishDatabaseRows), nameof(HidePostfix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(FishDatabaseUI), "BeforeDisplay"),
            prefix: new HarmonyMethod(typeof(FishDatabaseRows), nameof(ShowPrefix)),
            postfix: new HarmonyMethod(typeof(FishDatabaseRows), nameof(ShowPostfix)));
        return null;
    }

    // ---- hidden: the rows are kept (the game destroys what is in _instances: an empty list for that call) ----

    private static void HidePrefix(FishDatabaseUI __instance)
    {
        try
        {
            if (self == null || !self.Active || __instance?._instances is not { } rows || rows.Count == 0) return;
            kept[__instance.Pointer] = rows;
            __instance._instances = new Il2CppSystem.Collections.Generic.List<GameObject>();
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: rows left to the game ({e.Message})");
        }
    }

    private static void HidePostfix(FishDatabaseUI __instance)
    {
        try
        {
            if (__instance is not null && kept.TryGetValue(__instance.Pointer, out var rows)) __instance._instances = rows;
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: {e.Message}");
        }
    }

    // ---- shown: the game's BeforeDisplay with nothing to destroy or build, then the kept entries refreshed ----

    private static void ShowPrefix(FishDatabaseUI __instance)
    {
        stash = null;
        stashDb = null;
        try
        {
            if (self == null || !self.Active || __instance?._instances is not { } rows || rows.Count == 0) return;
            FishDatabase db = Singleton<FishingManager>.Instance?.database;
            if (db?.tiers is not { } tiers || !Fits(rows, tiers)) return; // the game makes them again
            stash = rows;
            stashDb = db;
            stashTiers = tiers;
            noTiers ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FishTier>(0);
            __instance._instances = new Il2CppSystem.Collections.Generic.List<GameObject>();
            db.tiers = noTiers;
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"{self?.Name}: rows made by the game ({e.Message})");
            Restore(__instance);
        }
    }

    private static void ShowPostfix(FishDatabaseUI __instance)
    {
        if (stash is null) return;
        var rows = stash;
        var tiers = stashTiers;
        var db = stashDb;
        Restore(__instance);
        try
        {
            long state = ((long)(db.catchedTypes?.Count ?? -1) << 32) | (uint)(db.availableTypes?.Count ?? -1);
            if (shownState.TryGetValue(__instance.Pointer, out long last) && last == state)
            {
                __instance.SelectFirst(); // the game selected before the entries were back
                Dev.LayoutLog.Note($"{self.Name}: rows kept, nothing caught since");
                return;
            }
            watch.Restart();
            for (int t = 0; t < tiers.Length; t++)
            {
                var entries = rows[2 * t + 1].GetComponentsInChildren<FishDatabaseEntryUI>(true);
                var types = tiers[t].types;
                for (int i = 0; i < types.Length; i++) entries[i].Refresh(types[i]);
            }
            __instance.SelectFirst(); // the game selected before the entries were back
            shownState[__instance.Pointer] = state;
            Dev.LayoutLog.Note($"{self.Name}: rows kept, entries refreshed in {watch.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{self.Name}: kept rows not refreshed, the tab makes them again next time ({e.Message})");
            kept.Remove(__instance.Pointer);
            shownState.Remove(__instance.Pointer);
        }
    }

    /// <summary>Puts the real tiers and the kept rows back after the game's BeforeDisplay (or a failure).</summary>
    private static void Restore(FishDatabaseUI ui)
    {
        try
        {
            if (stashDb is not null && stashTiers is not null) stashDb.tiers = stashTiers;
            if (stash is not null && ui is not null) ui._instances = stash;
        }
        finally
        {
            stash = null;
            stashDb = null;
        }
    }

    /// <summary>The kept rows match the tiers: a spacer and a holder per tier, the holder with one entry per fish.</summary>
    private static bool Fits(Il2CppSystem.Collections.Generic.List<GameObject> rows,
                             Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<FishTier> tiers)
    {
        if (rows.Count != 2 * tiers.Length) return false;
        for (int t = 0; t < tiers.Length; t++)
        {
            GameObject spacer = rows[2 * t], holder = rows[2 * t + 1];
            if (!Direct.Alive(spacer) || !Direct.Alive(holder) || tiers[t]?.types is not { } types) return false;
            if (holder.GetComponentsInChildren<FishDatabaseEntryUI>(true).Length != types.Length) return false;
        }
        return true;
    }
}
