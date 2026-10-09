using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Nivalis;
using Nivalis.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Compatibility with "Nivalis Tracked Quests HUD" by Hvizeu (hvizeu.nivalis.trackedquestshud), which shows only
/// pinned quests on the HUD. The game's ActiveJournalEntriesUi.Refresh (on every quest update, ~every 20 s while
/// walking) re-activates every quest row (Awake/OnEnable of all texts and nested lists, layout rebuilds), then that
/// mod hides the unpinned rows again: with no pinned quest, a 40-60 ms hitch that ends with an empty HUD.
/// When no quest is pinned (and that mod hides business reminders), we skip the rebuild and just empty both
/// lists, as the game does when there are no quests. With a pinned quest the game runs unchanged.
/// Only active with versions 1.0.x of that mod: from 1.1 it skips that rebuild itself.
/// </summary>
internal sealed class QuestHudCompat : Feature
{
    private static QuestHudCompat instance;
    private ConfigEntry<bool> hideBusiness;

    public override string Name => "Tracked Quests HUD compatibility";
    protected override string Section => "QuestHud";
    protected override string Description =>
        "With Hvizeu's Tracked Quests HUD mod: skip the quest HUD rebuild (40-60 ms hitch) when no quest is pinned.";

    public override bool InstallLate => true; // that mod loads after us

    protected override string TryInstall()
    {
        if (!IL2CPPChainloader.Instance.Plugins.TryGetValue("hvizeu.nivalis.trackedquestshud", out var info) ||
            info.Instance is not BasePlugin p)
            return "not needed: Tracked Quests HUD mod not installed";
        // from 1.1 that mod skips the empty rebuild itself (hides the group instead): leave it to it. The version it
        // declares to BepInEx (shown in the log), not its DLL's: that one is only right while its author updates it
        var version = info.Metadata.Version;
        if (version != null && (version.Major > 1 || version.Major == 1 && version.Minor >= 1))
            return $"not needed: Tracked Quests HUD {version} skips the empty rebuild itself";
        if (!p.Config.TryGetEntry("HUD", "HideBusinessReminders", out hideBusiness))
            return "Tracked Quests HUD version not recognised";
        instance = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(ActiveJournalEntriesUi), "Refresh"),
            prefix: new HarmonyMethod(typeof(QuestHudCompat), nameof(RefreshPrefix)) { priority = Priority.First });
        return null;
    }

    private static bool RefreshPrefix(ActiveJournalEntriesUi __instance, bool __runOriginal)
    {
        QuestHudCompat self = instance;
        try
        {
            if (!__runOriginal) return false; // request deferred by QuestHudRefresh: decided when it runs
            if (self == null || !self.Active || !self.hideBusiness.Value) return true;
            QuestManager qm = QuestManager.Instance;
            if (qm == null) return true;
            var e = qm.ActiveQuestsWithActiveObjectives.GetEnumerator();
            var it = e.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (it.MoveNext())
            {
                RuntimeQuest q = e.Current;
                if (q != null && q.Pinned) return true; // something to show: normal rebuild
            }
            Empty(__instance.questList);
            Empty(__instance.venueQuestList);
            return false;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"{self?.Name}: {ex.Message}");
            return true;
        }
    }

    private static void Empty(ItemListUI list)
    {
        if (list == null || list.DisplayedCount == 0) return;
        list.BeginUpdate(new Il2CppSystem.Nullable<int>());
        list.EndUpdate();
    }
}
