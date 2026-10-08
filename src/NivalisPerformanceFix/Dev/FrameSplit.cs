using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: splits each frame's main-thread time into
///  * crowd  = Character.UpdateAll + Character.LateUpdateAll (the game's per-character loops, animation included);
///  * render = camera culling + rendering on the main thread (Camera.FireOnPreCull to FireOnPostRender, all cameras);
///  * rest   = everything else (other scripts, physics, UI, job waits, Present / GPU wait).
/// Used by the play log context and the F9/F10 reports to see where a frame goes without a PIX capture.
/// Hooks run once per frame (crowd) or once per camera (render) and only while the developer tools are on.
/// Unity's Profiling.Recorder markers cannot be used instead: their icalls are stripped from this player.
/// </summary>
internal static class FrameSplit
{
    /// <summary>Cost of the last complete frame (ms), updated by <see cref="Tick"/>.</summary>
    internal static double CrowdMs, RenderMs;

    private static double crowdAcc, renderAcc;
    private static long crowdStart, renderStart;
    private static Harmony hooks;

    /// <summary>Accumulates averages over a measurement window.</summary>
    internal sealed class Window
    {
        private double crowd, render, frame;
        private int frames;

        public void Reset() { crowd = render = frame = 0; frames = 0; }

        public void Add(float dt)
        {
            crowd += CrowdMs; render += RenderMs; frame += dt * 1000.0; frames++;
        }

        public override string ToString()
        {
            if (frames == 0 || hooks == null) return "";
            double c = crowd / frames, r = render / frames, f = frame / frames;
            return $"frame {f:F2} ms = crowd {c:F2} + render {r:F2} + rest {f - c - r:F2}";
        }
    }

    internal static void Install()
    {
        if (hooks != null) return;
        hooks = new Harmony(Plugin.Guid + ".framesplit");
        try
        {
            foreach (string name in new[] { nameof(Character.UpdateAll), nameof(Character.LateUpdateAll) })
                hooks.Patch(Method(typeof(Character), name),
                    prefix: new HarmonyMethod(typeof(FrameSplit), nameof(CrowdBegin)),
                    postfix: new HarmonyMethod(typeof(FrameSplit), nameof(CrowdEnd)));
            hooks.Patch(Method(typeof(Camera), "FireOnPreCull"), prefix: new HarmonyMethod(typeof(FrameSplit), nameof(RenderBegin)));
            hooks.Patch(Method(typeof(Camera), "FireOnPostRender"), postfix: new HarmonyMethod(typeof(FrameSplit), nameof(RenderEnd)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Frame split unavailable: {e.Message}");
            hooks.UnpatchSelf();
            hooks = null;
        }
    }

    /// <summary>Removes the hooks (photo runs: their numbers go on the Nexus page, no developer cost in them).</summary>
    internal static void Remove()
    {
        if (hooks == null) return;
        hooks.UnpatchSelf();
        hooks = null;
        CrowdMs = RenderMs = crowdAcc = renderAcc = 0;
        Plugin.Log.LogInfo("Frame split hooks removed for this launch");
    }

    private static MethodInfo Method(Type t, string name) =>
        AccessTools.DeclaredMethod(t, name) ?? throw new MissingMethodException(t.Name + "." + name);

    /// <summary>Called once per frame (DevTools.Update): closes the previous frame's totals.</summary>
    internal static void Tick()
    {
        CrowdMs = crowdAcc; RenderMs = renderAcc;
        crowdAcc = renderAcc = 0;
    }

    private static void CrowdBegin() => crowdStart = Stopwatch.GetTimestamp();
    private static void CrowdEnd() => crowdAcc += Ms(crowdStart);
    private static void RenderBegin() => renderStart = Stopwatch.GetTimestamp();

    private static void RenderEnd()
    {
        if (renderStart != 0) renderAcc += Ms(renderStart);
        renderStart = 0;
    }

    private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
}
