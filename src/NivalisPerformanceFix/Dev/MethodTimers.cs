using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: times game methods named at run time ("Type.Method", type by full or short name), e.g. by the UI
/// test's "time" list: calls and total ms per method since the last Take(). One prefix/postfix pair per slot (a
/// generic pair with __state / __originalMethod crashed the game, 2026-10-07), 16 slots; every overload of a name
/// shares its slot (nested calls are fine: a start stack per slot).
/// </summary>
internal static class MethodTimers
{
    private const int Slots = 16;
    private static readonly string[] names = new string[Slots];
    private static readonly Stack<long>[] starts = Enumerable.Range(0, Slots).Select(_ => new Stack<long>()).ToArray();
    private static readonly int[] calls = new int[Slots];
    private static readonly double[] ms = new double[Slots];
    private static int used;
    private static Harmony harmony;

    /// <summary>Patches each "Type.Method" not timed yet; returns the names that could not be found.</summary>
    public static List<string> Add(IEnumerable<string> wanted)
    {
        var missing = new List<string>();
        harmony ??= new Harmony(Plugin.Guid + ".methodtimers");
        foreach (string w in wanted)
        {
            if (names.Contains(w)) continue;
            if (used >= Slots) { missing.Add(w + " (no slot left)"); continue; }
            int dot = w.LastIndexOf('.');
            Type type = dot > 0 ? FindType(w[..dot]) : null;
            var methods = type == null ? new List<System.Reflection.MethodInfo>()
                : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == w[(dot + 1)..] && !m.IsGenericMethod).ToList();
            if (methods.Count == 0) { missing.Add(w); continue; }
            // a generic struct parameter (Il2CppSystem.Nullable<int>: ItemListUI.BeginUpdate) breaks the patched
            // method's trampoline: every call threw and the game's list was left half-updated (UI test 2026-10-08)
            if (methods.Any(m => m.GetParameters().Any(p => p.ParameterType.IsValueType && p.ParameterType.IsGenericType)))
            {
                missing.Add(w + " (generic struct parameter: not patchable)");
                continue;
            }
            int slot = used++;
            names[slot] = w;
            try
            {
                foreach (var m in methods)
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(MethodTimers), "B" + slot),
                        postfix: new HarmonyMethod(typeof(MethodTimers), "E" + slot));
            }
            catch (Exception e) { missing.Add($"{w} ({e.Message})"); }
        }
        return missing;
    }

    /// <summary>"name calls ms" of the methods called since the last call, slowest first; resets the counts.</summary>
    public static string Take(double minMs = 1)
    {
        var parts = new List<(string, double)>();
        for (int i = 0; i < used; i++)
        {
            if (calls[i] > 0 && ms[i] >= minMs) parts.Add(($"{names[i]} {calls[i]}x {ms[i]:F0} ms", ms[i]));
            calls[i] = 0; ms[i] = 0;
        }
        return string.Join(", ", parts.OrderByDescending(p => p.Item2).Select(p => p.Item1));
    }

    private static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType(name, false);
            if (t != null) return t;
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Assembly-CSharp"))
            foreach (Type t in asm.GetTypes())
                if (t.Name == name) return t;
        return null;
    }

    private static void Begin(int i) => starts[i].Push(Stopwatch.GetTimestamp());

    private static void End(int i)
    {
        if (starts[i].Count == 0) return;
        ms[i] += (Stopwatch.GetTimestamp() - starts[i].Pop()) * 1000.0 / Stopwatch.Frequency;
        calls[i]++;
    }

    private static void B0() => Begin(0);
    private static void E0() => End(0);
    private static void B1() => Begin(1);
    private static void E1() => End(1);
    private static void B2() => Begin(2);
    private static void E2() => End(2);
    private static void B3() => Begin(3);
    private static void E3() => End(3);
    private static void B4() => Begin(4);
    private static void E4() => End(4);
    private static void B5() => Begin(5);
    private static void E5() => End(5);
    private static void B6() => Begin(6);
    private static void E6() => End(6);
    private static void B7() => Begin(7);
    private static void E7() => End(7);
    private static void B8() => Begin(8);
    private static void E8() => End(8);
    private static void B9() => Begin(9);
    private static void E9() => End(9);
    private static void B10() => Begin(10);
    private static void E10() => End(10);
    private static void B11() => Begin(11);
    private static void E11() => End(11);
    private static void B12() => Begin(12);
    private static void E12() => End(12);
    private static void B13() => Begin(13);
    private static void E13() => End(13);
    private static void B14() => Begin(14);
    private static void E14() => End(14);
    private static void B15() => Begin(15);
    private static void E15() => End(15);
}
