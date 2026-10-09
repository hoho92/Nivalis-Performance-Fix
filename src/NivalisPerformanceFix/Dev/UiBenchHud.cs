using System.Collections.Generic;
using System.Text;
using Nivalis;
using Nivalis.UI;
using NivalisPerformanceFix.Features;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Quest HUD steps (QuestHudRefresh, 2026-10-09): "pin" toggles the pin of active quest Index, Count times in the same
/// frame (several quest events in one frame, as while serving at a venue); "hud" checks that the HUD shows what a
/// rebuild made now would show (a deferred rebuild that never ran = HUD STALE). Every step notes the HUD refresh
/// requests and rebuilds it saw.
/// </summary>
internal sealed partial class UiBench
{
    private int hudRequests, hudRebuilds;

    private void HudReport()
    {
        int req = QuestHudRefresh.Requests - hudRequests, reb = QuestHudRefresh.Rebuilds - hudRebuilds;
        if (req > 0 || reb > 0) Note($"quest HUD: {req} request(s), {reb} rebuild(s)");
    }

    private void PinQuest(int index, int count) => Note(QuestHudTest.Pin(index, count));

    private void CheckHud(string name)
    {
        foreach (string note in QuestHudTest.Check(name)) Note(note);
    }
}

/// <summary>Quest HUD test actions shared by the UI test and the route benchmark.</summary>
internal static class QuestHudTest
{
    /// <summary>Toggles the pin of active quest <paramref name="index"/>, <paramref name="count"/> times in this frame.</summary>
    public static string Pin(int index, int count)
    {
        if (!Singleton<QuestManager>.InstanceNotNull(out QuestManager qm)) return "quest manager not found";
        var quests = new List<RuntimeQuest>();
        var e = qm.ActiveQuestsWithActiveObjectives.GetEnumerator();
        var it = e.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (it.MoveNext()) if (e.Current != null) quests.Add(e.Current);
        if (index < 0 || index >= quests.Count) return $"no active quest {index} ({quests.Count})";
        RuntimeQuest q = quests[index];
        for (int i = 0; i < count; i++) q.Pinned = !q.Pinned;
        return $"quest {index}/{quests.Count} pinned {q.Pinned} after {count} toggle(s)";
    }

    /// <summary>"HUD ok" when the HUD shows what a rebuild made now shows, else "HUD STALE" with both.</summary>
    public static List<string> Check(string name)
    {
        var notes = new List<string>();
        var huds = UnityEngine.Object.FindObjectsOfType<ActiveJournalEntriesUi>(true);
        if (huds.Length == 0) { notes.Add("quest HUD not found"); return notes; }
        if (QuestHudRefresh.Pending > 0) { notes.Add($"HUD check skipped: {QuestHudRefresh.Pending} rebuild(s) waiting"); return notes; }
        foreach (ActiveJournalEntriesUi ui in huds)
        {
            string shown = HudText(ui);
            QuestHudRefresh.RefreshNow(ui);
            string fresh = HudText(ui);
            notes.Add(shown == fresh ? $"HUD ok {name}: {shown}" : $"HUD STALE {name}: shown [{shown}] / rebuilt [{fresh}]");
        }
        return notes;
    }

    /// <summary>What the HUD shows: group alpha, then each displayed row (active or not) with its texts.</summary>
    private static string HudText(ActiveJournalEntriesUi ui)
    {
        var sb = new StringBuilder();
        sb.Append(ui.gameObject.activeInHierarchy ? "on" : "off");
        if (ui.entriesGroup != null) sb.Append($" a{ui.entriesGroup.alpha:F1}");
        Rows(sb, "Q", ui.questList);
        Rows(sb, "V", ui.venueQuestList);
        return sb.ToString();
    }

    private static void Rows(StringBuilder sb, string tag, ItemListUI list)
    {
        if (list == null) return;
        sb.Append($" {tag}{list.DisplayedCount}:");
        for (int i = 0; i < list.DisplayedCount; i++)
        {
            UnityEngine.GameObject go = list.GetItem(i)?.GameObject;
            if (go == null) { sb.Append(" [null]"); continue; }
            sb.Append(go.activeSelf ? " [" : " [hidden ");
            var texts = new List<string>();
            foreach (TMPro.TMP_Text t in go.GetComponentsInChildren<TMPro.TMP_Text>(!go.activeSelf)) // hidden rows: their texts too
                if (!string.IsNullOrWhiteSpace(t.text)) texts.Add(t.text.Trim());
            sb.Append(string.Join(" | ", texts)).Append(']');
        }
    }
}
