using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis.GhostSystem;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Background NPCs come from FakePoints (Nivalis.GhostSystem). Every point of a zone calls Populate (spawn a few
/// characters from the pool) or Remove (release them) at the same moment:
///  * when the zone's points are enabled (arrival): EnableTask yields one frame, then Populate;
///  * at every in-game hour: OnTimeTask waits Random.value seconds (0-1 s), then Populate or Remove.
/// Pool Get/Release reparent whole character hierarchies (Transform.SetParent -> Awake, OnTransformParentChanged on
/// their UI), so a zone's worth of points in one second gives frames of 25-50 ms (up to 200 ms in some zones).
/// We postpone the hourly Populate / Remove by a random delay of up to SpreadSeconds, so the same work is spread out.
/// The arrival Populate is left alone: it runs during the zone's loading hitch, where it costs nothing visible
/// (postponing it moved ~200 spawns into the first seconds of play: tested 2026-10-04).
/// Remove called from OnDisable (zone unloading) is never postponed, and a point's pending calls are dropped then.
/// Populate / Remove run a few times per hour per point, so the Harmony hooks cost nothing measurable.
/// </summary>
internal sealed class SpawnSpread : Feature
{
    public override string Name => "NPC spawn spreading";
    protected override string Section => "Spawns";
    protected override string Description =>
        "Spread the hourly appearance of background NPCs over a few seconds instead of one (removes spawn hitches).";

    private static SpawnSpread self;
    private ConfigEntry<float> spread;

    private readonly struct Call
    {
        public readonly FakePoint Point;
        public readonly IntPtr Ptr;
        public readonly bool Populate;
        public readonly float Due;
        public Call(FakePoint point, bool populate, float due) { Point = point; Ptr = point.Pointer; Populate = populate; Due = due; }
    }

    // in call order; a point's later call is never due before its earlier one
    private static readonly List<Call> pending = new();
    private static readonly System.Random random = new();
    private static bool running, disabling;
    // frame at which each point was last enabled: its EnableTask Populate comes one frame later
    private static readonly Dictionary<IntPtr, int> enabledFrame = new();
    private const int ArrivalFrames = 5;

    protected override void BindSettings(ConfigFile config)
    {
        spread = config.Bind(Section, "SpreadSeconds", 5f,
            new ConfigDescription("NPC appearances are spread over this many seconds.",
                new AcceptableValueRange<float>(0.5f, 15f)));
    }

    protected override string TryInstall()
    {
        var populate = AccessTools.Method(typeof(FakePoint), "Populate");
        var remove = AccessTools.Method(typeof(FakePoint), "Remove");
        var enable = AccessTools.Method(typeof(FakePoint), "OnEnable");
        var disable = AccessTools.Method(typeof(FakePoint), "OnDisable");
        if (populate == null || remove == null || enable == null || disable == null) return "FakePoint methods not found";
        self = this;
        Plugin.Harmony.Patch(populate, prefix: new HarmonyMethod(typeof(SpawnSpread), nameof(PopulatePrefix)));
        Plugin.Harmony.Patch(remove, prefix: new HarmonyMethod(typeof(SpawnSpread), nameof(RemovePrefix)));
        Plugin.Harmony.Patch(enable, postfix: new HarmonyMethod(typeof(SpawnSpread), nameof(EnablePostfix)));
        Plugin.Harmony.Patch(disable, prefix: new HarmonyMethod(typeof(SpawnSpread), nameof(DisablePrefix)),
            finalizer: new HarmonyMethod(typeof(SpawnSpread), nameof(DisableFinalizer)));
        return null;
    }

    private static bool PopulatePrefix(FakePoint __instance) => !Postpone(__instance, true);
    private static bool RemovePrefix(FakePoint __instance) => !Postpone(__instance, false);

    private static bool Postpone(FakePoint point, bool populate)
    {
        if (running || disabling || self == null || !self.Active) return false;
        try
        {
            float due = Time.unscaledTime + (float)random.NextDouble() * Math.Clamp(self.spread.Value, 0.5f, 15f);
            IntPtr ptr = point.Pointer;
            if (populate && enabledFrame.TryGetValue(ptr, out int f) && Time.frameCount - f <= ArrivalFrames) return false;
            foreach (Call c in pending)
                if (c.Ptr == ptr && c.Due > due) due = c.Due;
            pending.Add(new Call(point, populate, due));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{self.Name}: {e.Message}");
            return false;
        }
    }

    private static void EnablePostfix(FakePoint __instance) => enabledFrame[__instance.Pointer] = Time.frameCount;

    private static void DisablePrefix(FakePoint __instance)
    {
        disabling = true;
        IntPtr ptr = __instance.Pointer;
        enabledFrame.Remove(ptr);
        pending.RemoveAll(c => c.Ptr == ptr);
    }

    private static Exception DisableFinalizer(Exception __exception)
    {
        disabling = false;
        return __exception;
    }

    public override void Tick()
    {
        if (pending.Count == 0) return;
        float now = Direct.UnscaledTime;
        bool all = !Active; // switched off: run what is waiting now
        for (int i = 0; i < pending.Count;)
        {
            Call c = pending[i];
            if (!all && c.Due > now) { i++; continue; }
            pending.RemoveAt(i);
            if (c.Point == null || !c.Point.isActiveAndEnabled) continue; // destroyed / disabled meanwhile
            running = true;
            try
            {
                if (c.Populate) c.Point.Populate();
                else c.Point.Remove();
            }
            catch (Exception e) { Plugin.Log.LogError($"{Name}: {e.Message}"); }
            finally { running = false; }
        }
    }
}
