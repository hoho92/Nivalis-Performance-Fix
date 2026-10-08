using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem.Ai;
using UnityEngine;
using UnityEngine.AI;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: which NPCs enable their NavMeshAgent away from the NavMesh. Unity then logs "Failed to create
/// agent because it is not close enough to the NavMesh" with a stack trace, every time: in Central Canyon ~2800 per
/// 80 s route (route benchmark 2026-10-07), from CharacterMovement.EnableAgent in MoveAction.Perform. Hooked there:
/// for each failure, the character, its position, its action (state machine) and schedule action (agent ghost), the
/// distance to the nearest NavMesh; a summary every ReportSeconds while failures happen (BepInEx log).
/// The hook saw 1 failure while Unity logged 2843 (call sites with EnableAgent inlined by the C++ compiler), so the
/// probe also counts Unity's own warnings (Application.logMessageReceived) and, while they come, scans the scene's
/// agents every ScanSeconds for enabled ones that are off the NavMesh ("seen off" below).
/// </summary>
internal static class AgentProbe
{
    private const float ReportSeconds = 20f, SampleRadius = 30f, ScanSeconds = 0.5f;
    private const string Warning = "not close enough to the NavMesh";

    private sealed class Entry
    {
        public int Failures, Enables, SeenOff;
        public Vector3 Position;
        public string Action, Schedule;
        public float NavMeshDistance = -1;
        public Vector3 NearestNavMesh;
        public string Surroundings = "";
    }

    private static readonly Dictionary<string, Entry> entries = new();
    private static int failures, enables;
    private static float windowStart = -1, lastScan;
    private static int logged, loggedAtScan;
    private static Application.LogCallback logCallback; // kept alive: Unity holds only the native delegate

    internal static void Start()
    {
        try
        {
            new Harmony(Plugin.Guid + ".agentprobe").Patch(
                AccessTools.Method(typeof(CharacterMovement), nameof(CharacterMovement.EnableAgent)),
                postfix: new HarmonyMethod(typeof(AgentProbe), nameof(EnableAgentPostfix)));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"AgentProbe unavailable: {e.Message}"); }
        try
        {
            logCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Application.LogCallback>(
                new Action<string, string, LogType>(OnLog));
            Application.add_logMessageReceived(logCallback);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"AgentProbe: Unity log not followed ({e.Message})"); }
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if ((type != LogType.Warning && type != LogType.Error) || condition == null || !condition.Contains(Warning)) return;
        logged++;
        if (windowStart < 0) windowStart = Time.realtimeSinceStartup;
    }

    /// <summary>Enabled agents off the NavMesh right now (dev only: FindObjectsOfType costs a few ms).</summary>
    private static void Scan()
    {
        foreach (NavMeshAgent agent in UnityEngine.Object.FindObjectsOfType<NavMeshAgent>())
        {
            if (!agent.enabled || agent.isOnNavMesh) continue;
            Character c = agent.GetComponentInParent<Character>();
            string name = c is not null ? c.name : agent.gameObject.name;
            if (!entries.TryGetValue(name, out Entry e)) entries[name] = e = new Entry();
            e.Position = agent.transform.position;
            if (++e.SeenOff == 1 || e.SeenOff % 20 == 0) Describe(c, e);
        }
    }

    private static void EnableAgentPostfix(CharacterMovement __instance, bool enable)
    {
        if (!enable) return;
        try
        {
            NavMeshAgent agent = __instance.Agent;
            if (agent is null) return;
            enables++;
            Character c = __instance.character;
            string name = c is not null ? c.name : __instance.name;
            if (!entries.TryGetValue(name, out Entry e)) entries[name] = e = new Entry();
            e.Enables++;
            if (agent.isOnNavMesh) return;
            failures++;
            if (windowStart < 0) windowStart = Time.realtimeSinceStartup;
            e.Failures++;
            e.Position = __instance.transform.position;
            if (e.Failures == 1 || e.Failures % 100 == 0) Describe(c, e);
        }
        catch (Exception ex) { Plugin.Log.LogDebug($"AgentProbe: {ex.Message}"); }
    }

    /// <summary>The costly part (names, NavMesh search): first failure of a character, then every 100th.</summary>
    private static void Describe(Character c, Entry e)
    {
        try { e.Action = c?.state?.current is { } a ? a.name : "-"; } catch { e.Action = "?"; }
        try
        {
            AgentGhost ghost = c?.agent is { } view ? view.MyGhost : null;
            e.Schedule = ghost?.CurrentAction?.Type is { } t ? t.ToString() : "-";
        }
        catch { e.Schedule = "?"; }
        if (NavMesh.SamplePosition(e.Position, out NavMeshHit hit, SampleRadius, NavMesh.AllAreas))
        {
            e.NavMeshDistance = Vector3.Distance(e.Position, hit.position);
            e.NearestNavMesh = hit.position;
        }
        else e.NavMeshDistance = -1;
        // what is under / over the character: floor or ceiling it may be stuck in
        e.Surroundings = $"floor {Ray(e.Position + Vector3.up * 0.5f, Vector3.down)}, ceiling {Ray(e.Position + Vector3.up * 0.5f, Vector3.up)}";
        try
        {
            if (c?.Movement is { } m) e.Surroundings += $", agentOnly {m.AgentOnly}, obstacle {(m.Obstacle is { } o ? o.enabled.ToString() : "-")}";
        }
        catch { }
    }

    private static string Ray(Vector3 from, Vector3 dir)
    {
        RaycastHit hit;
        if (!Physics.Raycast(from, dir, out hit, 40f)) return "none within 40 m";
        Collider col = hit.collider;
        string what = col is not null ? (col.transform.parent is { } p ? p.name + "/" : "") + col.name : "?";
        return $"{hit.distance:F1} m (y {hit.point.y:F1}, {what})";
    }

    /// <summary>Once per frame (developer tools): the summary when a window with failures is over.</summary>
    internal static void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (logged > loggedAtScan && now - lastScan >= ScanSeconds)
        {
            lastScan = now;
            loggedAtScan = logged;
            try { Scan(); } catch (Exception ex) { Plugin.Log.LogDebug($"AgentProbe scan: {ex.Message}"); }
        }
        if (windowStart < 0 || now - windowStart < ReportSeconds) return;
        var sb = new StringBuilder($"Agent probe: {logged} Unity warning(s), {failures} failure(s) seen by the hook in {ReportSeconds:F0} s " +
                                   $"({enables} enables in all):");
        foreach (var (name, e) in entries.Where(kv => kv.Value.Failures + kv.Value.SeenOff > 0)
                     .OrderByDescending(kv => kv.Value.Failures + kv.Value.SeenOff).Take(10))
            sb.Append($"\n  {name}: {e.Failures} failure(s) / {e.Enables} enable(s), seen off {e.SeenOff} scan(s), at ({e.Position.x:F1} {e.Position.y:F1} {e.Position.z:F1}), " +
                      $"action {e.Action}, schedule {e.Schedule}, nearest NavMesh " +
                      (e.NavMeshDistance < 0 ? $"none within {SampleRadius:F0} m"
                          : $"{e.NavMeshDistance:F2} m at ({e.NearestNavMesh.x:F1} {e.NearestNavMesh.y:F1} {e.NearestNavMesh.z:F1})") +
                      (e.Surroundings.Length > 0 ? ", " + e.Surroundings : ""));
        Plugin.Log.LogInfo(sb.ToString());
        entries.Clear();
        failures = enables = logged = loggedAtScan = 0;
        windowStart = -1;
    }
}
