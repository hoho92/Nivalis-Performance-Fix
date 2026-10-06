using System;
using HarmonyLib;
using UnityEngine;
using WhiteCat.Rendering;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The post-processing stack asks the lens flare effect "is anything visible?" twice per frame for the same camera
/// (PostProcessLayer.SetLegacyCameraFlags, then HasActiveEffects), and each LensFlareSource.AnyVisible call runs
/// PrepareRender on every flare source of the scene (222 in Metro Hub: light intensity, transform, angles):
/// 11 ms per second of main thread, half of it repeated work. The second call of the same frame and camera now
/// returns the first result; the per-source state it would recompute is the same, since nothing moves in between.
/// </summary>
internal sealed class LensFlareOnce : Feature
{
    public override string Name => "Lens flare check once per frame";
    protected override string Section => "LensFlares";
    protected override string Description =>
        "Check lens flare visibility once per frame instead of twice (same result, half the work).";

    private static LensFlareOnce self;
    private static int frame, lastFrame = -1;
    private static IntPtr lastCamera;
    private static bool lastResult;

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(LensFlareSource), nameof(LensFlareSource.AnyVisible)),
            prefix: new HarmonyMethod(typeof(LensFlareOnce), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(LensFlareOnce), nameof(Postfix)));
        return null;
    }

    public override void Tick() => frame++;

    private static bool Prefix(Camera camera, ref bool __result)
    {
        if (self == null || !self.Active || camera is null) return true;
        if (frame != lastFrame || camera.Pointer != lastCamera) return true; // first call of this frame / camera
        __result = lastResult;
        return false;
    }

    private static void Postfix(Camera camera, bool __result)
    {
        if (camera is null) return;
        lastFrame = frame;
        lastCamera = camera.Pointer;
        lastResult = __result;
    }
}
