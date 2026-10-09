using System;
using System.Collections.Generic;
using HarmonyLib;
using Nivalis.UI;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The quest HUD (ActiveJournalEntriesUi) rebuilds all its rows (AddItem re-activates every quest row, its texts and
/// its nested objective list, then the layout is rebuilt) on each quest event: QuestManager's two update lists, the
/// venue setup reminders, the language. While serving at a venue several of them fire in the same frame: F6 layout log
/// 2026-10-09 (Update #4, one pinned quest) showed the HUD rebuilt 5 times in one frame, 73-83 ms frames every few
/// seconds (~16 ms per rebuild).
/// Refresh requests are collected and the HUD is rebuilt once, later in the same frame: after the compass
/// (NavigationUI.LateUpdate, a HUD LateUpdate, before the canvases are laid out and drawn). A request made after that
/// point runs at once, as in the game: deferred to the next frame, the HUD would show one frame of the old state (with
/// Tracked Quests HUD, whose prefix shows the hidden business reminders again at each request, a one-frame flash).
/// With the compass not updated (HUD hidden), the rebuild runs at the next Update. The HUD shows the same thing, only
/// built once.
/// </summary>
internal sealed class QuestHudRefresh : Feature
{
    private static QuestHudRefresh self;
    private static bool flushing;
    private static int flushedFrame = -1; // frame whose compass LateUpdate already rebuilt the HUD
    private static readonly List<ActiveJournalEntriesUi> pending = new();

    /// <summary>Refresh calls received and rebuilds let through since the start (UI tests compare them per step).</summary>
    internal static int Requests, Rebuilds;

    public override string Name => "Quest HUD rebuilt once per frame";
    protected override string Section => "QuestHudRefresh";
    protected override string Description =>
        "Rebuild the quest HUD once per frame when several quest updates arrive together (up to 5 rebuilds, 80 ms hitches, while serving at a venue).";

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ActiveJournalEntriesUi), "Refresh"),
            prefix: new HarmonyMethod(typeof(QuestHudRefresh), nameof(RefreshPrefix)) { priority = Priority.First });
        Plugin.Harmony.Patch(AccessTools.Method(typeof(NavigationUI), "LateUpdate"),
            postfix: new HarmonyMethod(typeof(QuestHudRefresh), nameof(CompassPostfix)));
        return null;
    }

    public override void Tick() => Flush(); // requests made after the compass's LateUpdate, or with the HUD hidden

    protected override void SwitchedOff() => Flush();

    private static bool RefreshPrefix(ActiveJournalEntriesUi __instance)
    {
        Requests++;
        if (flushing || self is not { Active: true } || flushedFrame == Time.frameCount) { Rebuilds++; return true; }
        try
        {
            if (!pending.Exists(p => p.Pointer == __instance.Pointer)) pending.Add(__instance);
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{self.Name}: {e.Message}");
            Rebuilds++;
            return true;
        }
    }

    /// <summary>Waiting requests (UI tests: the HUD is checked only when none is left).</summary>
    internal static int Pending => pending.Count;

    /// <summary>Rebuilds the HUD at once, as the game would (UI tests: compare with what is shown).</summary>
    internal static void RefreshNow(ActiveJournalEntriesUi ui)
    {
        bool was = flushing;
        flushing = true;
        try { ui.Refresh(); }
        finally { flushing = was; }
    }

    private static void CompassPostfix()
    {
        flushedFrame = Time.frameCount;
        Flush();
    }

    private static void Flush()
    {
        if (pending.Count == 0 || flushing) return;
        flushing = true;
        try
        {
            // a rebuild may ask for another one (event raised from inside): that one runs at once, as in the game
            for (int i = 0; i < pending.Count; i++)
            {
                ActiveJournalEntriesUi ui = pending[i];
                if (!Native.Direct.Alive(ui)) continue;
                ui.Refresh();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{self?.Name}: {e.Message}");
        }
        finally
        {
            pending.Clear();
            flushing = false;
        }
    }
}
