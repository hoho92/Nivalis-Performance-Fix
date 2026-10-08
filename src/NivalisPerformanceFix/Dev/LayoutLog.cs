using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool (F6): records the UI layout work for Seconds. PIX showed menu tab changes followed by ~0.5 s of
/// layout rebuilt at every frame (ScrollRect.SetLayoutHorizontal → ForceRebuildLayoutImmediate of the content) without
/// telling which element keeps it dirty. Logs, per RectTransform path: layout rebuilds (count, ms), ScrollRect layout
/// passes (count, ms) and MarkLayoutForRebuild requests (count), plus the frames that had any of them.
/// The hooks are installed at the first use and do nothing while not recording.
/// </summary>
internal static class LayoutLog
{
    private const float Seconds = 15f;

    private sealed class Stat { public int Count; public double Ms; }

    private static bool installed, recording;
    private static float until;
    private static int firstFrame, lastFrame;
    private static readonly HashSet<int> frames = new();
    private static readonly Dictionary<IntPtr, string> paths = new();
    private static readonly Dictionary<string, Stat> rebuilds = new(), scrolls = new(), marks = new(), redraws = new();
    private static readonly Stopwatch rebuildWatch = new(), scrollWatch = new();

    /// <summary>One frame with layout work: what was rebuilt, what asked for it, which lists the game rebuilt.</summary>
    private sealed class FrameInfo
    {
        public int Rebuilds, Marks;
        public double Ms, FrameMs;            // FrameMs: whole frame (unscaled delta seen by the next frame)
        public double SlowMs;                 // slowest rebuild of the frame: its root and the root's size
        public string SlowPath;
        public Vector2 SlowSize;
        public readonly Dictionary<string, int> Marked = new();
        public readonly List<string> Events = new();
        public readonly Dictionary<string, (int count, double ms)> Timed = new();
        public RectTransform SlowRoot;
        // marked rects of the frame (cheap to record): the report tells which of them are under the slowest root
        public readonly Dictionary<IntPtr, (RectTransform rect, string path, int count)> MarkedRects = new();
    }

    /// <summary>
    /// Game methods timed while recording (per frame: calls and total ms), to see where a slow frame goes. One
    /// prefix/postfix pair per method with a start stack: a generic pair (__state, __originalMethod) crashed the game
    /// (Il2CppInterop trampolines, 2026-10-07).
    /// </summary>
    private sealed class Timer
    {
        public readonly string Key;
        private readonly Stack<long> starts = new();
        public Timer(string key) => Key = key;
        public void Begin() => starts.Push(Stopwatch.GetTimestamp());
        public void End()
        {
            if (starts.Count == 0) return;
            double ms = (Stopwatch.GetTimestamp() - starts.Pop()) * 1000.0 / Stopwatch.Frequency;
            if (!recording) return;
            var fi = Now();
            fi.Timed.TryGetValue(Key, out var v);
            fi.Timed[Key] = (v.count + 1, v.ms + ms);
        }
    }

    private static readonly Timer refreshTimer = new("CharacterWindowUi.RefreshCharacterList"),
        navTimer = new("CharacterWindowUi.UpdateManualNavigation"),
        widgetTimer = new("CharacterWindowWidgetCharacter.Initialize"),
        createTimer = new("ItemListUI.CreateNewItemDisplay"),
        filterTimer = new("VendorFilteringUI.FilterResult"),
        addTimer = new("ItemListUI.AddItem"),
        endTimer = new("ItemListUI.EndUpdate"),
        togglesTimer = new("CharacterWindowUi.TurnOffAllToggles"),
        bagFiltersTimer = new("PlayerInventoryUI.OnFiltersChanged"),
        bagRefreshTimer = new("PlayerInventoryUI.RefreshInventoryDisplay"),
        fishTimer = new("FishDatabaseUI.BeforeDisplay");
    private static void RefreshBegin() => refreshTimer.Begin();
    private static void RefreshEnd() => refreshTimer.End();
    private static void NavBegin() => navTimer.Begin();
    private static void NavEnd() => navTimer.End();
    private static void WidgetBegin() => widgetTimer.Begin();
    private static void WidgetEnd() => widgetTimer.End();
    private static void CreateBegin() => createTimer.Begin();
    private static void CreateEnd() => createTimer.End();
    private static void FilterBegin() => filterTimer.Begin();
    private static void FilterEnd() => filterTimer.End();
    private static void AddBegin() => addTimer.Begin();
    private static void AddEnd() => addTimer.End();
    private static void EndBegin() => endTimer.Begin();
    private static void EndEnd() => endTimer.End();
    private static void TogglesBegin() => togglesTimer.Begin();
    private static void TogglesEnd() => togglesTimer.End();
    private static void BagFiltersBegin() => bagFiltersTimer.Begin();
    private static void BagFiltersEnd() => bagFiltersTimer.End();
    private static void BagRefreshBegin() => bagRefreshTimer.Begin();
    private static void BagRefreshEnd() => bagRefreshTimer.End();

    private static void TimeMethod(Harmony h, Type type, string method, string begin, string end)
    {
        try
        {
            h.Patch(AccessTools.Method(type, method),
                prefix: new HarmonyMethod(typeof(LayoutLog), begin), postfix: new HarmonyMethod(typeof(LayoutLog), end));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"LayoutLog: {type.Name}.{method} not timed: {e.Message}"); }
    }
    private static readonly SortedDictionary<int, FrameInfo> timeline = new();
    private static readonly List<(int frame, float ms)> frameTimes = new(); // every recorded frame

    private static FrameInfo Now()
    {
        int f = Time.frameCount;
        if (!timeline.TryGetValue(f, out var fi)) timeline[f] = fi = new FrameInfo();
        return fi;
    }

    internal static void Toggle()
    {
        if (recording) { Report("stopped"); return; }
        try
        {
            if (!installed)
            {
                var h = new Harmony(Plugin.Guid + ".layoutlog");
                h.Patch(AccessTools.Method(typeof(LayoutRebuilder), nameof(LayoutRebuilder.Rebuild)),
                    prefix: new HarmonyMethod(typeof(LayoutLog), nameof(RebuildPrefix)),
                    postfix: new HarmonyMethod(typeof(LayoutLog), nameof(RebuildPostfix)));
                h.Patch(AccessTools.Method(typeof(ScrollRect), nameof(ScrollRect.SetLayoutHorizontal)),
                    prefix: new HarmonyMethod(typeof(LayoutLog), nameof(ScrollPrefix)),
                    postfix: new HarmonyMethod(typeof(LayoutLog), nameof(ScrollPostfix)));
                h.Patch(AccessTools.Method(typeof(LayoutRebuilder), nameof(LayoutRebuilder.MarkLayoutForRebuild)),
                    prefix: new HarmonyMethod(typeof(LayoutLog), nameof(MarkPrefix)));
                // graphics marked for redraw (mesh / material rebuilt, canvas re-batched): TMP overrides the vertices one
                foreach (var (type, method) in new[] { (typeof(Graphic), nameof(Graphic.SetVerticesDirty)),
                             (typeof(Graphic), nameof(Graphic.SetMaterialDirty)),
                             (typeof(TMPro.TextMeshProUGUI), nameof(TMPro.TextMeshProUGUI.SetVerticesDirty)) })
                    try
                    {
                        h.Patch(AccessTools.DeclaredMethod(type, method), prefix: new HarmonyMethod(typeof(LayoutLog), nameof(RedrawPrefix)));
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"LayoutLog: {type.Name}.{method} not counted: {e.Message}"); }
                // not BeginUpdate: its Nullable<int> argument breaks the Il2CppInterop trampoline (NullReferenceException)
                h.Patch(AccessTools.Method(typeof(Nivalis.UI.ItemListUI), nameof(Nivalis.UI.ItemListUI.EndUpdate)),
                    prefix: new HarmonyMethod(typeof(LayoutLog), nameof(EndUpdatePrefix)));
                Type window = typeof(Nivalis.UI.InGameMenu.CharacterWindow.CharacterWindowUi);
                TimeMethod(h, window, "RefreshCharacterList", nameof(RefreshBegin), nameof(RefreshEnd));
                TimeMethod(h, window, "UpdateManualNavigation", nameof(NavBegin), nameof(NavEnd));
                TimeMethod(h, typeof(Nivalis.UI.CharacterWindowWidgetCharacter), "Initialize", nameof(WidgetBegin), nameof(WidgetEnd));
                TimeMethod(h, typeof(Nivalis.UI.ItemListUI), "CreateNewItemDisplay", nameof(CreateBegin), nameof(CreateEnd));
                TimeMethod(h, typeof(Nivalis.Locale.UI.VendorFilteringUI), "FilterResult", nameof(FilterBegin), nameof(FilterEnd));
                TimeMethod(h, typeof(Nivalis.UI.ItemListUI), "AddItem", nameof(AddBegin), nameof(AddEnd));
                TimeMethod(h, typeof(Nivalis.UI.ItemListUI), "EndUpdate", nameof(EndBegin), nameof(EndEnd));
                TimeMethod(h, window, "TurnOffAllToggles", nameof(TogglesBegin), nameof(TogglesEnd));
                TimeMethod(h, typeof(Nivalis.UI.PlayerInventoryUI), "OnFiltersChanged", nameof(BagFiltersBegin), nameof(BagFiltersEnd));
                TimeMethod(h, typeof(Nivalis.FishDatabaseUI), "BeforeDisplay", nameof(FishBegin), nameof(FishEnd));
                TimeMethod(h, typeof(Nivalis.UI.PlayerInventoryUI), "RefreshInventoryDisplay", nameof(BagRefreshBegin), nameof(BagRefreshEnd));
                installed = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"LayoutLog: unavailable: {e.Message}");
            return;
        }
        rebuilds.Clear(); scrolls.Clear(); marks.Clear(); redraws.Clear(); frames.Clear(); timeline.Clear(); frameTimes.Clear();
        firstFrame = Time.frameCount;
        until = Time.realtimeSinceStartup + Seconds;
        recording = true;
        Plugin.Log.LogMessage($"LayoutLog: recording {Seconds:F0} s (F6 again to stop)");
    }

    internal static void Update()
    {
        if (!recording) return;
        float dt = Time.unscaledDeltaTime * 1000;
        if (Time.frameCount > firstFrame + 1) frameTimes.Add((Time.frameCount - 1, dt)); // the previous frame
        if (timeline.TryGetValue(Time.frameCount - 1, out var prev)) prev.FrameMs = dt;
        if (Time.realtimeSinceStartup >= until) Report("done");
    }

    /// <summary>A line in the timeline of the current frame (features timing their own work).</summary>
    internal static void Note(string text)
    {
        if (recording) Now().Events.Add(text);
    }

    private static void RebuildPrefix() { if (recording) rebuildWatch.Restart(); }

    private static void RebuildPostfix(LayoutRebuilder __instance)
    {
        if (!recording) return;
        double ms = rebuildWatch.Elapsed.TotalMilliseconds;
        Add(rebuilds, __instance.m_ToRebuild, ms);
        var fi = Now();
        fi.Rebuilds++;
        fi.Ms += ms;
        if (ms > fi.SlowMs && __instance.m_ToRebuild is { } root)
        {
            // the size tells an animation that resizes the root at each frame (opening of a menu tab)
            fi.SlowMs = ms;
            fi.SlowPath = Path(root);
            fi.SlowSize = root.rect.size;
            fi.SlowRoot = root;
        }
    }

    private static void EndUpdatePrefix(Nivalis.UI.ItemListUI __instance)
    {
        if (recording) Now().Events.Add($"EndUpdate {Path(__instance.transform)} ({__instance._displayedInstanceCount} rows)");
    }

    private static void ScrollPrefix() { if (recording) scrollWatch.Restart(); }

    private static void ScrollPostfix(ScrollRect __instance)
    {
        if (!recording) return;
        Add(scrolls, __instance.transform, scrollWatch.Elapsed.TotalMilliseconds);
    }

    private static void MarkPrefix(RectTransform rect)
    {
        if (!recording || rect is null) return;
        Add(marks, rect, 0);
        var fi = Now();
        fi.Marks++;
        string p = Path(rect);
        fi.Marked[p] = fi.Marked.TryGetValue(p, out int n) ? n + 1 : 1;
        fi.MarkedRects[rect.Pointer] = fi.MarkedRects.TryGetValue(rect.Pointer, out var m)
            ? (m.rect, m.path, m.count + 1) : (rect, p, 1);
    }

    private static void RedrawPrefix(Graphic __instance)
    {
        if (!recording || __instance is null) return;
        Add(redraws, __instance.transform, 0);
    }

    /// <summary>
    /// The marked rects of a frame under its slowest rebuild root (or the root itself): the changes that made it
    /// dirty. Worked out at report time only (walking parents at each mark slowed big windows while recording).
    /// </summary>
    private static string Causes(FrameInfo fi)
    {
        RectTransform root = fi.SlowRoot;
        if (!Native.Direct.Alive(root)) return null;
        var found = new List<(string path, int count)>();
        int checks = 0;
        foreach (var (rect, path, count) in fi.MarkedRects.Values)
        {
            if (++checks > 5000) break;
            if (!Native.Direct.Alive(rect)) continue;
            if (rect.Pointer == root.Pointer || rect.IsChildOf(root)) found.Add((path, count));
        }
        if (found.Count == 0) return null;
        return string.Join(", ", found.OrderByDescending(c => c.count).Take(4).Select(c => $"{c.count} x {c.path}"));
    }

    private static void FishBegin() => fishTimer.Begin();
    private static void FishEnd() => fishTimer.End();

    private static void Add(Dictionary<string, Stat> d, Transform t, double ms)
    {
        if (t is null) return;
        string path = Path(t);
        if (!d.TryGetValue(path, out var s)) d[path] = s = new Stat();
        s.Count++;
        s.Ms += ms;
        frames.Add(Time.frameCount);
        lastFrame = Time.frameCount;
    }

    private static string Path(Transform t)
    {
        if (paths.TryGetValue(t.Pointer, out string p)) return p;
        p = t.name;
        int depth = 0;
        for (Transform q = t.parent; q is not null && depth < 5; q = q.parent, depth++) p = q.name + "/" + p;
        if (paths.Count > 5000) paths.Clear();
        return paths[t.Pointer] = p;
    }

    private static void Report(string why)
    {
        recording = false;
        var sb = new StringBuilder();
        sb.Append($"LayoutLog {why}: {Time.frameCount - firstFrame} frames, {frames.Count} with layout work\n");
        AppendFrameTimes(sb);
        Section(sb, "layout rebuilds", rebuilds, true);
        Section(sb, "ScrollRect layout", scrolls, true);
        Section(sb, "marked for rebuild", marks, false);
        Section(sb, "graphics marked for redraw (vertices / material)", redraws, false);
        sb.Append("  timeline (frames with a rebuild over 1 ms or a list update):\n");
        int shown = 0;
        foreach (var (f, fi) in timeline)
        {
            // frames with only small timed work (e.g. one row prepared in advance) are left out unless the frame was slow
            if (fi.Ms < 1 && fi.Events.Count == 0 && fi.FrameMs < 30 && fi.Timed.Values.Sum(v => v.ms) < 5) continue;
            if (++shown > 200) { sb.Append("    ...\n"); break; }
            var top = fi.Marked.OrderByDescending(kv => kv.Value).FirstOrDefault();
            sb.Append($"    f+{f - firstFrame,-5} frame {fi.FrameMs,6:F1} ms, rebuild {fi.Rebuilds,3} x {fi.Ms,7:F1} ms, marks {fi.Marks,6}" +
                      (top.Key is null ? "" : $" (top {top.Value} {top.Key})") + "\n");
            if (fi.SlowPath is not null && fi.SlowMs >= 1)
            {
                sb.Append($"             slowest {fi.SlowMs:F1} ms: {fi.SlowPath} ({fi.SlowSize.x:F0} x {fi.SlowSize.y:F0})\n");
                if (Causes(fi) is { } causes) sb.Append("               made dirty by: " + causes + "\n");
            }
            foreach (string e in fi.Events) sb.Append($"             {e}\n");
            foreach (var (name, v) in fi.Timed.Where(kv => kv.Value.ms >= 0.5).OrderByDescending(kv => kv.Value.ms))
                sb.Append($"             {name}: {v.count} x, {v.ms:F1} ms\n");
        }
        Plugin.Log.LogMessage(sb.ToString());
    }

    private static void Section(StringBuilder sb, string title, Dictionary<string, Stat> d, bool timed)
    {
        sb.Append($"  {title} ({d.Values.Sum(s => s.Count)} total{(timed ? $", {d.Values.Sum(s => s.Ms):F1} ms" : "")}):\n");
        foreach (var (path, s) in d.OrderByDescending(kv => timed ? kv.Value.Ms : kv.Value.Count).Take(15))
            sb.Append($"    {s.Count,6}{(timed ? $" {s.Ms,8:F1} ms" : "")}  {path}\n");
    }

    /// <summary>
    /// Frame times of the whole recording (stutter that is not layout, e.g. an animation that jerks on one menu):
    /// average, median, 95th percentile, max, frames over 1.5x the median, and the slowest frames with the gap
    /// between them (a regular gap points to periodic work).
    /// </summary>
    private static void AppendFrameTimes(StringBuilder sb)
    {
        if (frameTimes.Count < 10) return;
        var sorted = frameTimes.Select(f => f.ms).OrderBy(m => m).ToList();
        float median = sorted[sorted.Count / 2], p95 = sorted[(int)(sorted.Count * 0.95)];
        int slow = sorted.Count(m => m > median * 1.5f);
        sb.Append($"  frame times: avg {sorted.Average():F1} ms, median {median:F1}, p95 {p95:F1}, max {sorted[^1]:F1}, " +
                  $"{slow} frames over 1.5x median\n");
        var worst = frameTimes.Where(f => f.ms > median * 1.5f).OrderByDescending(f => f.ms).Take(30).OrderBy(f => f.frame).ToList();
        if (worst.Count == 0) return;
        sb.Append("  slow frames (f+N ms, +gap in frames): ");
        int last = -1;
        foreach (var (frame, ms) in worst)
        {
            sb.Append($"f+{frame - firstFrame} {ms:F0}" + (last < 0 ? "" : $" (+{frame - last})") + ", ");
            last = frame;
        }
        sb.Append('\n');
    }
}
