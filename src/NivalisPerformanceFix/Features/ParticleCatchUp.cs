using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Looping particle effects set to "pause and catch up" stop while off screen, then simulate all the time they
/// missed in one frame when they come back into view (ParticleSystem::RendererBecameVisible → Simulate, with 3D
/// noise): 50-65 ms frames when the camera turns (PIX, 2026-10-07). Those systems are switched to plain "pause":
/// they resume where they stopped, which looks the same for ambient loops (smoke, steam, fire). Non-looping
/// effects keep their mode (a burst must still end on time). Scenes are scanned again every ScanSeconds.
/// </summary>
internal sealed unsafe class ParticleCatchUp : Feature
{
    public override string Name => "Particle effects without catch-up";
    protected override string Section => "ParticleCatchUp";
    protected override string Description =>
        "Looping particle effects (smoke, steam...) resume where they stopped when they come back into view, " +
        "instead of simulating the missed time in one frame (hitch when the camera turns).";

    private const float ScanSeconds = 10f;

    private readonly HashSet<IntPtr> done = new();
    private readonly List<ParticleSystem> changed = new();
    private float nextScan = 3f;
    private bool restored;

    // ParticleSystem.MainModule is a struct holding only its ParticleSystem; the game never reads cullingMode, so
    // IL2CPP stripped the getter: the engine's own calls (ref MainModule) are used for both sides
    private static delegate* unmanaged<IntPtr*, int> getCulling;
    private static delegate* unmanaged<IntPtr*, int, void> setCulling;
    private static delegate* unmanaged<IntPtr*, byte> getLoop;

    protected override string TryInstall()
    {
        getCulling = (delegate* unmanaged<IntPtr*, int>)IL2CPP.il2cpp_resolve_icall("UnityEngine.ParticleSystem/MainModule::get_cullingMode_Injected");
        setCulling = (delegate* unmanaged<IntPtr*, int, void>)IL2CPP.il2cpp_resolve_icall("UnityEngine.ParticleSystem/MainModule::set_cullingMode_Injected");
        getLoop = (delegate* unmanaged<IntPtr*, byte>)IL2CPP.il2cpp_resolve_icall("UnityEngine.ParticleSystem/MainModule::get_loop_Injected");
        return getCulling == null || setCulling == null || getLoop == null ? "particle settings not found" : null;
    }

    private static int Culling(ParticleSystem ps) { IntPtr self = ps.Pointer; return getCulling(&self); }
    private static void SetCulling(ParticleSystem ps, ParticleSystemCullingMode mode) { IntPtr self = ps.Pointer; setCulling(&self, (int)mode); }
    private static bool Loop(ParticleSystem ps) { IntPtr self = ps.Pointer; return getLoop(&self) != 0; }

    public override void Tick()
    {
        if (!Active)
        {
            if (!restored) Restore();
            return;
        }
        restored = false;
        float now = Time.realtimeSinceStartup;
        if (now < nextScan) return;
        nextScan = now + ScanSeconds;
        Scan();
    }

    private void Scan()
    {
        var watch = Stopwatch.StartNew();
        int found = 0, fresh = 0, switched = 0, oneShot = 0;
        var modes = new int[4];
        foreach (ParticleSystem ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
        {
            found++;
            if (!done.Add(ps.Pointer)) continue;
            fresh++;
            int mode = Culling(ps);
            if (mode is >= 0 and < 4) modes[mode]++;
            if (mode != (int)ParticleSystemCullingMode.PauseAndCatchup) continue;
            if (!Loop(ps)) { oneShot++; continue; }
            SetCulling(ps, ParticleSystemCullingMode.Pause);
            changed.Add(ps);
            switched++;
        }
        if (fresh > 0)
            Plugin.Log.LogDebug($"{Name}: {fresh} new of {found} systems (automatic {modes[0]}, catch-up {modes[1]}, " +
                                $"pause {modes[2]}, always {modes[3]}; one-shot catch-up kept {oneShot}), " +
                                $"{switched} switched, {watch.Elapsed.TotalMilliseconds:F1} ms");
        if (done.Count > 20000) done.Clear(); // scenes come and go: forget dead pointers now and then
        changed.RemoveAll(ps => !Native.Direct.Alive(ps)); // systems of unloaded zones (kept only to restore them)
    }

    protected override void SwitchedOff()
    {
        if (!restored) Restore();
    }

    /// <summary>Switched off: the changed systems get their catch-up back.</summary>
    private void Restore()
    {
        restored = true;
        foreach (ParticleSystem ps in changed)
            if (Native.Direct.Alive(ps))
                SetCulling(ps, ParticleSystemCullingMode.PauseAndCatchup);
        changed.Clear();
        done.Clear();
        nextScan = 0;
    }
}
