using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool for the launch: counts the scene searches (Object.FindObjectsOfType / FindObjectOfType, every
/// overload ends in one of these) per searched type, with their time, from the plugin load to the BootLog report.
/// PIX (boot-000420) showed ~2.3 s of FindObjectsOfType on the main thread before the title, with unknown callers:
/// the searched type names the caller (Singleton&lt;T&gt; lookups, managers looking for their scene objects).
/// On only when bench/boot.finds exists (a clock read and a type name per search).
/// </summary>
internal static class FindProbe
{
    private sealed class Entry { public int Calls, Found; public double Ms, MaxMs, First = -1, Last; }

    private static readonly Dictionary<string, Entry> byType = new();
    private static readonly Stopwatch clock = new();
    private static int depth; // FindObjectOfType calls FindObjectsOfType: count the outer call only
    private static bool on;

    /// <summary>Asked for (bench/boot.finds): BootLog reports even with the developer tools off.</summary>
    internal static bool On => on;

    internal static void StartIfAsked()
    {
        string ask = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix", "bench", "boot.finds");
        if (!File.Exists(ask)) return;
        try
        {
            var h = new Harmony(Plugin.Guid + ".findprobe");
            var pre = new HarmonyMethod(typeof(FindProbe), nameof(Prefix));
            var post = new HarmonyMethod(typeof(FindProbe), nameof(Postfix));
            int n = 0;
            foreach (var m in typeof(UnityEngine.Object).GetMethods())
            {
                if (!m.IsStatic || m.IsGenericMethodDefinition) continue;
                if (m.Name != "FindObjectsOfType" && m.Name != "FindObjectOfType") continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(Il2CppSystem.Type)) continue;
                h.Patch(m, prefix: pre, postfix: post);
                n++;
            }
            on = true;
            clock.Start();
            BootLog.Mark($"FindProbe: {n} search methods hooked");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"FindProbe: unavailable: {e.Message}");
        }
    }

    private static void Prefix(out long __state)
    {
        __state = depth++ == 0 && on ? clock.ElapsedTicks : -1;
    }

    private static void Postfix(Il2CppSystem.Type __0, long __state, object __result)
    {
        depth--;
        if (__state < 0) return;
        try
        {
            double ms = (clock.ElapsedTicks - __state) * 1000.0 / Stopwatch.Frequency;
            string name = __0?.FullName ?? "?";
            if (!byType.TryGetValue(name, out var e)) byType[name] = e = new Entry();
            e.Calls++;
            e.Ms += ms;
            e.MaxMs = Math.Max(e.MaxMs, ms);
            double t = BootLog.Now;
            if (e.First < 0) e.First = t;
            e.Last = t;
            if (__result is Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> a) e.Found += a.Length;
            else if (__result is UnityEngine.Object o && o != null) e.Found++;
        }
        catch (Exception) { }
    }

    /// <summary>From the BootLog report: the searches so far, most expensive first, then off.</summary>
    internal static void Report(StringBuilder sb)
    {
        if (!on) return;
        on = false;
        double total = byType.Values.Sum(e => e.Ms);
        int calls = byType.Values.Sum(e => e.Calls);
        sb.AppendLine($"  scene searches (FindObject(s)OfType): {calls} calls, {total:F0} ms in all, {byType.Count} types");
        foreach (var (name, e) in byType.OrderByDescending(p => p.Value.Ms).Take(40))
            sb.AppendLine($"    {e.Ms,8:F1} ms  {e.Calls,5} calls  max {e.MaxMs,6:F1}  found {e.Found,5}  " +
                          $"t {e.First,6:F2}-{e.Last,6:F2}  {name}");
    }
}
