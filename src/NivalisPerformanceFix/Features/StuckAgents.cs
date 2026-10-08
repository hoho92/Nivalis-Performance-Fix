using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.AI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// An NPC whose walk starts where there is no NavMesh (seen: a customer inside a Central Canyon interior, 14 m under
/// the street, 2026-10-07) keeps its NavMeshAgent enabled off the NavMesh, and Unity logs "Failed to create agent
/// because it is not close enough to the NavMesh" ~32 times per second (each with a stack trace) for as long as the
/// zone is loaded: ~2800 warnings per 80 s, and the NPC never moves again.
/// Nothing is done while that warning does not appear (Unity's log is followed). When it does, the scene's agents are
/// checked at most once per second; an agent still enabled off the NavMesh StuckSeconds later is warped to the
/// nearest NavMesh point (within MaxDistance), so its walk goes on from there.
/// </summary>
internal sealed class StuckAgents : Feature
{
    public override string Name => "Stuck NPCs put back on their path";
    protected override string Section => "StuckNpcs";
    protected override string Description =>
        "An NPC stuck where it cannot walk (Unity log spammed with 'not close enough to the NavMesh') is moved to the nearest walkable point.";

    private const string Warning = "not close enough to the NavMesh";
    private const float ScanSeconds = 1f, SlowScanSeconds = 15f;

    private ConfigEntry<float> stuckSeconds, maxDistance;
    private Application.LogCallback logCallback; // kept alive: Unity holds only the native delegate
    private volatile bool warned;
    private float lastScan, nextScan;
    private readonly Dictionary<IntPtr, float> offSince = new();

    protected override void BindSettings(ConfigFile config)
    {
        stuckSeconds = config.Bind(Section, "StuckSeconds", 3f,
            new ConfigDescription("Seconds an NPC must stay stuck before it is moved.", new AcceptableValueRange<float>(1f, 60f)));
        maxDistance = config.Bind(Section, "MaxDistance", 50f,
            new ConfigDescription("Farthest walkable point (m) an NPC may be moved to.", new AcceptableValueRange<float>(5f, 200f)));
    }

    protected override string TryInstall()
    {
        logCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Application.LogCallback>(
            new Action<string, string, LogType>(OnLog));
        Application.add_logMessageReceived(logCallback);
        return null;
    }

    private void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Warning && condition != null && condition.Contains(Warning)) warned = true;
    }

    public override void Tick()
    {
        if (!warned || !Active) return;
        float now = Time.realtimeSinceStartup;
        if (now - lastScan < ScanSeconds || now < nextScan) return;
        lastScan = now;
        warned = false;

        var stillOff = new HashSet<IntPtr>();
        foreach (NavMeshAgent agent in UnityEngine.Object.FindObjectsOfType<NavMeshAgent>())
        {
            if (!agent.enabled || agent.isOnNavMesh) continue;
            IntPtr key = agent.Pointer;
            stillOff.Add(key);
            if (!offSince.TryGetValue(key, out float since)) { offSince[key] = now; continue; }
            if (now - since < stuckSeconds.Value) continue;
            Vector3 from = agent.transform.position;
            if (!NavMesh.SamplePosition(from, out NavMeshHit hit, maxDistance.Value, NavMesh.AllAreas))
            {
                offSince[key] = now + 60f; // nothing walkable around: retry in a minute
                continue;
            }
            bool moved = agent.Warp(hit.position);
            offSince.Remove(key);
            stillOff.Remove(key);
            Plugin.Log.LogInfo($"{Name}: {agent.gameObject.name} stuck at ({from.x:F1} {from.y:F1} {from.z:F1}) " +
                               $"{(moved ? "moved" : "could not be moved")} to ({hit.position.x:F1} {hit.position.y:F1} {hit.position.z:F1})");
        }
        // forget agents that recovered by themselves
        var gone = new List<IntPtr>();
        foreach (IntPtr key in offSince.Keys) if (!stillOff.Contains(key)) gone.Add(key);
        foreach (IntPtr key in gone) offSince.Remove(key);
        if (offSince.Count > 0) warned = true; // keep checking until they are moved
        // an agent with nothing walkable around goes on logging the warning: while only such agents are left, the
        // scan waits for the first retry (at most SlowScanSeconds, so a new stuck NPC is still found), not 1 s
        nextScan = 0;
        bool onlyWaiting = offSince.Count > 0;
        float firstRetry = float.MaxValue;
        foreach (float since in offSince.Values)
        {
            if (since <= now) { onlyWaiting = false; break; }
            firstRetry = Math.Min(firstRetry, since + stuckSeconds.Value);
        }
        if (onlyWaiting) nextScan = Math.Min(firstRetry, now + SlowScanSeconds);
    }
}
