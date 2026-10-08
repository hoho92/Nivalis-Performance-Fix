using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis;
using Nivalis.UI;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Two HUD elements made Unity redraw them at every frame for nothing (LayoutLog with redraw counts, Meridian
/// Market route, 15 s = 2269 frames):
///  * compass cardinal letters (CompassWorldSideMarker.SetPosition): the game moves them by setting anchorMin then
///    anchorMax; between the two calls the rect has another width, so Unity sees a size change and rebuilds the text
///    mesh and its layout (~1700 times per letter). SetPosition is inlined in NavigationUI.LateUpdate (no hook), so
///    each letter gets an inactive proxy RectTransform that the game moves instead (its _transform field), and after
///    LateUpdate the letter is put at the same place with its anchors pinned at x = 0 and the anchored position
///    moved (x = anchor x parent width + anchored x): no size change, no redraw;
///  * venue opening hours (VenueOpenTimeDisplayUI.Update): the text was set at every frame (TMP SetText always marks
///    the text dirty, even when unchanged: 2167 redraws). Its Update now runs every RefreshSeconds, and at once when
///    the panel shows another venue;
///  * autosave icon (DayTransitionController/NewDayLabel/SavingWrapper/AutosaveIcon): its Animator ran at every
///    frame while the label is invisible (CanvasGroup alpha 0: ~360 redraws per 15 s, the only hidden UI animator
///    found). It is paused while the label's alpha is 0 and resumes as soon as the label shows.
/// </summary>
internal sealed class HudRedraws : Feature
{
    public override string Name => "HUD redraws";
    protected override string Section => "HudRedraws";
    protected override string Description =>
        "Stop the compass letters and the venue opening hours of the HUD from being redrawn at every frame.";

    private static HudRedraws self;
    private static ConfigEntry<float> refreshSeconds;
    private static readonly Dictionary<IntPtr, (float time, IntPtr venue)> lastHours = new();
    private static readonly Dictionary<IntPtr, (CompassWorldSideMarker marker, RectTransform real, RectTransform proxy)> letters = new();

    private const string AutosavePath =
        "----- UI/----- UI_Child/GameplayUI(Clone)/DayTransitionController/NewDayLabel/SavingWrapper/AutosaveIcon";
    private Animator autosave;
    private CanvasGroup autosaveGroup;
    private float nextFind;

    public override void Tick()
    {
        if (!Native.Direct.Alive(autosave))
        {
            autosave = null;
            if (!Active || Time.unscaledTime < nextFind) return;
            nextFind = Time.unscaledTime + 5f; // GameObject.Find walks the scene: rarely, until found
            GameObject go = GameObject.Find(AutosavePath);
            if (go == null) return;
            autosave = go.GetComponent<Animator>();
            autosaveGroup = go.GetComponentInParent<CanvasGroup>();
            if (autosave == null || autosaveGroup == null) { autosave = null; return; }
        }
        bool run = !Active || !Native.Direct.Alive(autosaveGroup) || autosaveGroup.alpha > 0f;
        if (autosave.enabled != run) autosave.enabled = run;
    }

    protected override void SwitchedOff()
    {
        RestoreLetters();
        if (Native.Direct.Alive(autosave) && !autosave.enabled) autosave.enabled = true;
    }

    protected override void BindSettings(ConfigFile config)
    {
        refreshSeconds = config.Bind(Section, "RefreshSeconds", 0.25f,
            new ConfigDescription("How often the venue opening hours text is refreshed (the game: every frame).",
                new AcceptableValueRange<float>(0.05f, 2f)));
    }

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(NavigationUI), "LateUpdate"),
            postfix: new HarmonyMethod(typeof(HudRedraws), nameof(CompassPostfix)));
        Plugin.Harmony.Patch(AccessTools.Method(typeof(VenueOpenTimeDisplayUI), "Update"),
            prefix: new HarmonyMethod(typeof(HudRedraws), nameof(HoursUpdatePrefix)));
        return null;
    }

    private static void CompassPostfix(NavigationUI __instance)
    {
        if (self is not { Active: true }) { RestoreLetters(); return; }
        var list = __instance._cardinalMarkerInstances;
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            CompassWorldSideMarker m = list[i];
            if (m == null) continue;
            if (!letters.TryGetValue(m.Pointer, out var e) || !Native.Direct.Alive(e.proxy) || !Native.Direct.Alive(e.real))
            {
                RectTransform real = m._transform;
                RectTransform parent = real != null ? real.parent?.TryCast<RectTransform>() : null;
                if (parent == null) continue;
                var go = new GameObject("NivalisPerformanceFix compass proxy", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
                go.SetActive(false);
                RectTransform proxy = go.GetComponent<RectTransform>();
                proxy.SetParent(parent, false);
                proxy.anchorMin = real.anchorMin; proxy.anchorMax = real.anchorMax; proxy.pivot = real.pivot;
                proxy.sizeDelta = real.sizeDelta; proxy.anchoredPosition = real.anchoredPosition;
                m._transform = proxy;
                letters[m.Pointer] = e = (m, real, proxy);
            }
            Place(e.real, e.proxy);
        }
    }

    /// <summary>The letter where the game put its proxy: same anchor y, anchor x folded into the anchored position.</summary>
    private static void Place(RectTransform real, RectTransform proxy)
    {
        RectTransform parent = real.parent?.TryCast<RectTransform>();
        if (parent == null) return;
        Vector2 a = proxy.anchorMin;
        var anchor = new Vector2(0, a.y);
        if (real.anchorMin != anchor) real.anchorMin = anchor;
        if (real.anchorMax != new Vector2(0, proxy.anchorMax.y)) real.anchorMax = new Vector2(0, proxy.anchorMax.y);
        Vector2 p = proxy.anchoredPosition;
        var want = new Vector2(a.x * parent.rect.width + p.x, p.y);
        if (real.anchoredPosition != want) real.anchoredPosition = want;
    }

    /// <summary>Feature switched off: the game moves its letters again.</summary>
    private static void RestoreLetters()
    {
        if (letters.Count == 0) return;
        foreach (var (marker, real, proxy) in letters.Values)
        {
            if (Native.Direct.Alive(marker) && Native.Direct.Alive(real)) marker._transform = real;
            if (Native.Direct.Alive(proxy)) UnityEngine.Object.Destroy(proxy.gameObject);
        }
        letters.Clear();
    }

    private static bool HoursUpdatePrefix(VenueOpenTimeDisplayUI __instance)
    {
        if (self is not { Active: true }) return true;
        IntPtr key = __instance.Pointer;
        var venue = __instance._currentBoundLocale;
        IntPtr venuePtr = venue != null ? venue.Pointer : IntPtr.Zero;
        float now = Time.unscaledTime;
        if (lastHours.TryGetValue(key, out var last) && last.venue == venuePtr && now - last.time < refreshSeconds.Value)
            return false;
        if (lastHours.Count > 256) lastHours.Clear(); // destroyed panels
        lastHours[key] = (now, venuePtr);
        return true;
    }
}
