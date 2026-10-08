using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.UI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Helpers shared by the list features (shops, reviews, menu lists), so a game update is fixed in one place.
/// </summary>
internal static class ListTools
{
    // ---- quiet moments: work that must never cost in play ----

    private static bool watching, loading;
    private static IntPtr loadingPanel;
    private static readonly HashSet<IntPtr> otherPanels = new();

    /// <summary>Follows the loading screen (UIPanel "LoadingUI"); installed once, by the first feature that needs it.</summary>
    public static void WatchLoadingScreen()
    {
        if (watching) return;
        watching = true;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(Nivalis.UIPanel), "SetVisible"),
            postfix: new HarmonyMethod(typeof(ListTools), nameof(SetVisiblePostfix)));
        try
        {
            Plugin.Harmony.Patch(AccessTools.Method(typeof(Nivalis.LoadingScreenUI), nameof(Nivalis.LoadingScreenUI.Hide)),
                prefix: new HarmonyMethod(typeof(ListTools), nameof(HidePrefix)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Loading screen not held for prepared rows ({e.Message})");
        }
    }

    /// <summary>
    /// Frames between two searches for a window. Behind the loading screen the windows appear when a scene loads
    /// or at its very end (Hide held, see below): short gaps then, long ones the rest of the loading. A search costs
    /// 11-17 ms there (FindObjectsOfType with inactive objects); every 3 frames for the whole launch loading made
    /// 2.3 s of searches for windows not there yet (BootLog FindProbe, 2026-10-08).
    /// </summary>
    public static int SearchGap => !loading ? 30 : heldScreen is not null ? 3 : SceneJustChanged ? 5 : 60;

    /// <summary>
    /// The usual search wait (delay counted down per frame, then SearchGap): true when a search is due now.
    /// A wait started in a long gap is cut short when a window-making moment begins.
    /// </summary>
    public static bool SearchDue(ref int delay)
    {
        if (heldScreen is not null || loading && SceneJustChanged) delay = Math.Min(delay, 5);
        if (--delay > 0) return false;
        delay = SearchGap;
        return true;
    }

    private const int SceneChangeFrames = 30;
    private static int sceneFrame = -1, sceneChangeFrame = -1000, sceneSignature;

    /// <summary>A scene was loaded or unloaded in the last SceneChangeFrames frames (checked once per frame).</summary>
    private static bool SceneJustChanged
    {
        get
        {
            int frame = Time.frameCount;
            if (frame != sceneFrame)
            {
                sceneFrame = frame;
                int count = UnityEngine.SceneManagement.SceneManager.sceneCount, signature = count;
                for (int i = 0; i < count; i++)
                    signature = signature * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
                if (signature != sceneSignature) { sceneSignature = signature; sceneChangeFrame = frame; }
            }
            return frame - sceneChangeFrame < SceneChangeFrames;
        }
    }

    // ---- loading screen held while rows are made ----
    // The game makes its windows only at the very end of the loading screen: 262-327 rows (saves, shops, menus) were
    // still to make when it hid (F6 2026-10-07), so the first Save window took 470-513 ms. Its Hide is now held,
    // at least MinHoldSeconds (the features find the new windows) and while rows remain, at most MaxHoldSeconds.

    /// <summary>Work that wants the loading screen to stay (rows still to make); set by RowPrebuilder.</summary>
    public static Func<bool> LoadingWork;
    private const float MinHoldSeconds = 0.3f, MaxHoldSeconds = 2f;
    private static Nivalis.LoadingScreenUI heldScreen;
    private static float holdStart;
    private static bool releasing;

    private static bool HidePrefix(Nivalis.LoadingScreenUI __instance)
    {
        if (releasing || __instance is null || !Plugin.MasterEnabled.Value) return true;
        if (heldScreen is null)
        {
            heldScreen = __instance;
            holdStart = Time.realtimeSinceStartup;
        }
        return false; // hidden by Tick when the rows are made
    }

    // ---- the first seconds after the loading screen (user: arriving in the world is not smooth) ----
    private const float AfterLoadingSeconds = 30f;
    private static readonly List<float> afterFrames = new();
    private static float afterStart = -1;

    private static void StartAfterLoading()
    {
        afterFrames.Clear();
        if (GameObject.Find("P_MainMenu(Clone)") is not null) return; // the title screen: not arriving in the world
        afterStart = Time.realtimeSinceStartup;
    }

    /// <summary>Frame times of the first seconds in the world, logged once: where arriving hitches.</summary>
    private static void TickAfterLoading()
    {
        if (afterStart < 0) return;
        afterFrames.Add(Time.unscaledDeltaTime * 1000f);
        if (Time.realtimeSinceStartup - afterStart < AfterLoadingSeconds) return;
        ReportAfterLoading(cut: false);
    }

    private static void ReportAfterLoading(bool cut)
    {
        afterStart = -1;
        if (afterFrames.Count == 0) return;
        var sorted = new List<float>(afterFrames);
        sorted.Sort();
        float median = sorted[sorted.Count / 2], total = 0;
        var slow = new StringBuilder();
        int count = 0;
        for (int i = 0; i < afterFrames.Count; i++)
        {
            total += afterFrames[i];
            if (afterFrames[i] < Math.Max(25f, 2 * median)) continue;
            if (++count <= 40) slow.Append($"f+{i} {afterFrames[i]:F0}, ");
        }
        // one value per second: frames per second / slowest frame (FPS high at first then lower and unsteady: user)
        var seconds = new StringBuilder();
        float t = 0, worst = 0;
        int n = 0, second = 1;
        foreach (float ms in afterFrames)
        {
            t += ms; n++; worst = Math.Max(worst, ms);
            if (t < second * 1000f) continue;
            seconds.Append($"{n}/{worst:F0} ");
            n = 0; worst = 0; second++;
        }
        Plugin.Log.LogInfo($"After loading{(cut ? " (cut short: a new loading began)" : "")}: {afterFrames.Count} frames in {total / 1000:F1} s, median {median:F1} ms, " +
                           $"max {sorted[sorted.Count - 1]:F0} ms, {count} slow frame(s): {slow}\n" +
                           $"  per second (fps/slowest ms): {seconds}");
    }

    /// <summary>Once per frame (plugin behaviour): lets a held loading screen hide when the rows are made.</summary>
    public static void Tick()
    {
        TickAfterLoading();
        if (heldScreen is null) return;
        float held = Time.realtimeSinceStartup - holdStart;
        bool work = false;
        try { work = LoadingWork?.Invoke() == true; } catch (Exception) { }
        if (held < MinHoldSeconds || (work && held < MaxHoldSeconds)) return;
        var screen = heldScreen;
        heldScreen = null;
        Plugin.Log.LogInfo($"Loading screen held {held:F2} s for rows made in advance{(work ? " (time limit reached)" : "")}");
        if (!Direct.Alive(screen)) return;
        releasing = true;
        try { screen.Hide(); }
        finally { releasing = false; }
    }

    /// <summary>The loading screen's Hide is being held (rows still to make): the world is not shown yet.</summary>
    public static bool Held => heldScreen is not null;

    /// <summary>The loading screen is shown (WatchLoadingScreen must have been called).</summary>
    /// <remarks>A held screen counts: the game hides its loading panel while we hold the screen, and the work the
    /// hold waits for (rows made only behind a loading screen) was skipped for the whole hold (UI test 2026-10-08:
    /// held 2 s for nothing, the shop rows never made).</remarks>
    public static bool Loading => loading || heldScreen is not null;

    /// <summary>
    /// Number of loading screens shown so far: the "scene" the windows belong to. Searches for a scene's windows start
    /// again when it changes. Not the active scene: the game switches it again AFTER the loading screen (UI test
    /// 2026-10-08), which made every window search, row queue and scrollbar pass start over at the first pause, all
    /// in the first menu opening (13 -> 140 ms).
    /// </summary>
    public static int LoadingIndex { get; private set; }

    /// <summary>Work may be done now without being seen: behind the loading screen, or while the game is paused (menus).</summary>
    public static bool Quiet => Loading || Time.timeScale == 0;

    private static void SetVisiblePostfix(Nivalis.UIPanel __instance, bool isVisible)
    {
        try
        {
            if (__instance is null) return;
            IntPtr ptr = __instance.Pointer;
            if (ptr == loadingPanel)
            {
                if (loading && !isVisible) StartAfterLoading();
                if (!loading && isVisible && afterStart >= 0) ReportAfterLoading(cut: true); // a new loading began
                if (isVisible && !loading) LoadingIndex++;
                loading = isVisible;
                return;
            }
            if (otherPanels.Contains(ptr)) return; // other panels: name looked at once
            if (__instance.name.StartsWith("LoadingUI"))
            {
                loadingPanel = ptr;
                if (isVisible && !loading) LoadingIndex++;
                loading = isVisible;
            }
            else
            {
                if (otherPanels.Count > 1000) otherPanels.Clear();
                otherPanels.Add(ptr);
            }
        }
        catch (Exception) { }
    }

    // ---- scrollbars ----

    /// <summary>
    /// ScrollRect in AutoHideAndExpandViewport mode lays its content out again (ForceRebuildLayoutImmediate) inside
    /// its own layout pass, up to twice, to see whether the content fits without the scrollbar: ~30% of the shop's
    /// layout cost at each opening, 490 ms of a ~1.9 s reviews rebuild. AutoHide still hides an unneeded scrollbar,
    /// without resizing the view. Returns the number of scrollbars changed.
    /// </summary>
    public static int SimplifyScrollbar(ScrollRect scroll)
    {
        if (scroll is null) return 0;
        int changed = 0;
        if (scroll.verticalScrollbarVisibility == ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport)
        {
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            changed++;
        }
        if (scroll.horizontalScrollbarVisibility == ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport)
        {
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            changed++;
        }
        return changed;
    }

    private static readonly HashSet<IntPtr> grownHandles = new();

    /// <summary>
    /// Handle at least <paramref name="min"/> units tall: +min on the handle, -min on the area it slides in, so it
    /// still goes from one end of the bar to the other (a handle is view / list: a few pixels with ~1000 rows).
    /// </summary>
    public static void GrowHandle(Scrollbar bar, float min)
    {
        if (bar is null || bar.handleRect is not { } handle || handle.parent?.TryCast<RectTransform>() is not { } area) return;
        if (!grownHandles.Add(bar.Pointer)) return;
        float half = min / 2;
        handle.offsetMin -= new Vector2(0, half);
        handle.offsetMax += new Vector2(0, half);
        area.offsetMin += new Vector2(0, half);
        area.offsetMax -= new Vector2(0, half);
    }

    /// <summary>
    /// State of every filter control (search fields, dropdowns, toggles) under <paramref name="roots"/>, to tell a
    /// filter event that changes nothing (e.g. Delete held in an empty search field fires every frame); null if unreadable.
    /// </summary>
    public static string FilterState(params Component[] roots)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (Component root in roots)
            {
                if (root is null) continue;
                foreach (var field in root.GetComponentsInChildren<TMPro.TMP_InputField>(true)) sb.Append(field.text).Append('|');
                foreach (var dropdown in root.GetComponentsInChildren<TMPro.TMP_Dropdown>(true)) sb.Append(dropdown.value).Append('|');
                foreach (var toggle in root.GetComponentsInChildren<Toggle>(true)) sb.Append(toggle.isOn ? '1' : '0');
                sb.Append('#');
            }
            return sb.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Filter events of a window, run once at the next tick: a click on a category turns one toggle off and another
    /// on, two full rebuilds in one frame, the first thrown away. A window's rebuild records the filters it used
    /// (<see cref="Done"/>): a pending event whose filters did not change since is dropped.
    /// </summary>
    public sealed class Deferred<T> where T : Component
    {
        private readonly Dictionary<IntPtr, T> pending = new();
        private readonly List<T> now = new();
        private readonly Dictionary<IntPtr, string> last = new();
        private readonly Func<T, string> state;

        public Deferred(Func<T, string> state) => this.state = state;

        /// <summary>A filter event of <paramref name="window"/>: handled at the next <see cref="Run"/>.</summary>
        public void Add(T window) => pending[window.Pointer] = window;

        /// <summary><paramref name="window"/> is rebuilt now with its current filters: nothing pending for it.</summary>
        public void Done(T window)
        {
            pending.Remove(window.Pointer);
            if (last.Count > 100) last.Clear();
            last[window.Pointer] = state(window);
        }

        /// <summary>Rebuilds (<paramref name="rebuild"/>) each window with a pending event whose filters changed.</summary>
        public void Run(Action<T> rebuild)
        {
            if (pending.Count == 0) return;
            now.Clear();
            now.AddRange(pending.Values);
            pending.Clear();
            foreach (T window in now)
                if (Direct.Alive(window) &&
                    !(last.TryGetValue(window.Pointer, out string before) && before != null && before == state(window)))
                    rebuild(window);
        }
    }
}
