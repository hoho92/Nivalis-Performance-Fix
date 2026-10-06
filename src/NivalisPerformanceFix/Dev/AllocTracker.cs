using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool (AllocKey, F7): records every managed allocation of the game for a few seconds through the
/// IL2CPP profiler API (il2cpp_profiler_install_allocation) and reports the classes that allocate the most,
/// by bytes and count, split main thread / other threads. Allocations drive the garbage collector and its
/// write barrier (~19% of the main thread in Metro Hub), so this points at the code worth fixing.
/// For the classes named in AllocStackClasses, one allocation in <see cref="StackSampling"/> also records the
/// native call stack inside GameAssembly.dll (this release build keeps no IL2CPP managed stack; unwinding stops at
/// the CoreCLR callback transition, so the thread's stack memory is scanned for return addresses that follow a call
/// instruction), written as RVAs:
/// tools\allocstacks.py (workspace) turns them into method names with the Il2CppDumper output.
/// When the allocation happens under plugin code (an interop call), the CoreCLR frames follow after "| CLR".
/// Allocation events are switched on only during the window; outside it the callback is never called.
/// </summary>
internal static unsafe class AllocTracker
{
    private const int ProfileAllocations = 1 << 7; // IL2CPP_PROFILE_ALLOCATIONS
    private const int StackSampling = 16, StackFrames = 8;

    private sealed class Stat
    {
        public long Count, Bytes, MainCount, MainBytes;
    }

    private static readonly object Gate = new();
    private static Dictionary<IntPtr, Stat> stats = new();
    private static Dictionary<IntPtr, Dictionary<string, int>> stacks = new(); // klass -> "frame < frame..." -> samples
    private static readonly Dictionary<IntPtr, bool> wantStacks = new();
    private static string[] stackClasses = Array.Empty<string>();
    private static long gameAssemblyBase, gameAssemblySize;
    private static bool installed, recording;
    private static int mainThreadId;
    private static IntPtr profiler;
    private static float left, windowSeconds;
    private static Action<string> report;

    public static bool Recording => recording;

    /// <summary>Starts a window of <paramref name="seconds"/>; the report is passed to <paramref name="onReport"/>.</summary>
    public static void Start(float seconds, string stackClassList, Action<string> onReport)
    {
        if (recording) return;
        if (!installed)
        {
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                {
                    gameAssemblyBase = (long)m.BaseAddress;
                    gameAssemblySize = m.ModuleMemorySize;
                }
            profiler = Marshal.AllocHGlobal(64); // opaque Il2CppProfiler*, only used as an identity
            il2cpp_profiler_install(profiler, &OnShutdown);
            il2cpp_profiler_install_allocation(&OnAllocation);
            installed = true;
        }
        lock (Gate)
        {
            stats = new Dictionary<IntPtr, Stat>();
            stacks = new Dictionary<IntPtr, Dictionary<string, int>>();
            wantStacks.Clear();
            stackClasses = (stackClassList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        mainThreadId = Environment.CurrentManagedThreadId;
        left = windowSeconds = seconds;
        report = onReport;
        recording = true;
        il2cpp_profiler_set_events(ProfileAllocations);
        Plugin.Log.LogMessage($"Recording allocations for {seconds:F0} s...");
    }

    /// <summary>Call once per frame (main thread) with the unscaled frame time.</summary>
    public static void Update(float dt)
    {
        if (!recording || (left -= dt) > 0) return;
        il2cpp_profiler_set_events(0);
        recording = false;
        Dictionary<IntPtr, Stat> snapshot;
        Dictionary<IntPtr, Dictionary<string, int>> stackSnapshot;
        lock (Gate)
        {
            snapshot = stats; stats = new Dictionary<IntPtr, Stat>();
            stackSnapshot = stacks; stacks = new Dictionary<IntPtr, Dictionary<string, int>>();
        }
        report?.Invoke(Format(snapshot, stackSnapshot));
    }

    private static string Format(Dictionary<IntPtr, Stat> snapshot, Dictionary<IntPtr, Dictionary<string, int>> stackSnapshot)
    {
        double seconds = Math.Max(0.001, windowSeconds);
        long count = snapshot.Values.Sum(s => s.Count), bytes = snapshot.Values.Sum(s => s.Bytes);
        long mainBytes = snapshot.Values.Sum(s => s.MainBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"Allocations over {seconds:F0} s: {count / seconds:F0}/s, {bytes / seconds / 1024:F0} KB/s " +
                      $"({bytes / seconds * 60 / 1048576:F1} MB/min), main thread {100.0 * mainBytes / Math.Max(1, bytes):F0}% of bytes");
        sb.AppendLine("     KB/s   count/s   main%  class");
        foreach (var kv in snapshot.OrderByDescending(kv => kv.Value.Bytes).Take(40))
        {
            Stat s = kv.Value;
            sb.AppendLine($"  {s.Bytes / seconds / 1024,7:F1}  {s.Count / seconds,8:F0}  {100.0 * s.MainBytes / Math.Max(1, s.Bytes),5:F0}%  {ClassName(kv.Key)}");
        }
        foreach (var kv in stackSnapshot.OrderByDescending(kv => snapshot.TryGetValue(kv.Key, out Stat st) ? st.Bytes : 0))
        {
            int total = kv.Value.Values.Sum();
            sb.AppendLine($"  allocating code of {ClassName(kv.Key)} ({total} samples, GameAssembly RVAs, innermost first):");
            foreach (var st in kv.Value.OrderByDescending(x => x.Value).Take(6))
                sb.AppendLine($"    {100.0 * st.Value / total,5:F1}%  {st.Key}");
        }
        return sb.ToString();
    }

    /// <summary>Called with the lock held: does this class match AllocStackClasses (cached per class)?</summary>
    private static bool WantsStack(IntPtr klass)
    {
        if (stackClasses.Length == 0) return false;
        if (!wantStacks.TryGetValue(klass, out bool want))
        {
            string name = ClassName(klass);
            want = stackClasses.Any(c => name.Contains(c, StringComparison.Ordinal));
            wantStacks[klass] = want;
        }
        return want;
    }

    /// <summary>Return addresses inside GameAssembly.dll found on the current thread's stack, innermost first, as
    /// RVAs. A stack slot counts when it points into GameAssembly right after a call instruction (E8 rel32, or
    /// FF /2 indirect forms); stale slots can slip in, which the per-stack percentages make obvious.</summary>
    private static string StackKey()
    {
        ulong low, high;
        GetCurrentThreadStackLimits(&low, &high);
        byte marker;
        ulong* slot = (ulong*)(((ulong)&marker) & ~7UL);
        ulong* end = (ulong*)Math.Min(high, (ulong)slot + ScanBytes);
        var sb = new StringBuilder("GA");
        int kept = 0;
        for (; slot < end && kept < StackFrames; slot++)
        {
            long rva = (long)*slot - gameAssemblyBase;
            if (rva < 0x1000 || rva >= gameAssemblySize || !AfterCall((byte*)*slot)) continue;
            sb.Append(' ').Append(rva.ToString("X"));
            kept++;
        }
        if (kept == 0) sb.Append(" (no frames)");
        return sb.Append(ManagedFrames()).ToString();
    }

    /// <summary>
    /// Plugin (CoreCLR) frames above the allocation, if any: boxing done by il2cpp_runtime_invoke for an interop
    /// call made from a mod (value-type return values come back boxed) shows up here with the mod's method name.
    /// </summary>
    private static string ManagedFrames()
    {
        var sb = new StringBuilder();
        int kept = 0;
        foreach (var f in new System.Diagnostics.StackTrace(2, false).GetFrames())
        {
            var m = f.GetMethod();
            if (m == null || m.DeclaringType == typeof(AllocTracker)) continue;
            sb.Append(kept == 0 ? " | CLR " : " < ").Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
            if (++kept == 6) break;
        }
        return sb.ToString();
    }

    private const ulong ScanBytes = 32 * 1024;

    private static bool AfterCall(byte* ret) =>
        ret[-5] == 0xE8 ||                                  // call rel32
        (ret[-2] == 0xFF && (ret[-1] & 0x38) == 0x10) ||    // call reg / [reg]
        (ret[-3] == 0xFF && (ret[-2] & 0x38) == 0x10) ||    // call [reg+disp8]
        (ret[-6] == 0xFF && (ret[-5] & 0x38) == 0x10) ||    // call [rip+disp32] / [reg+disp32]
        (ret[-7] == 0xFF && (ret[-5] & 0x38) == 0x10);      // call [reg+disp32] with SIB

    private static string ClassName(IntPtr klass)
    {
        try
        {
            IntPtr type = il2cpp_class_get_type(klass);
            IntPtr name = type == IntPtr.Zero ? IntPtr.Zero : il2cpp_type_get_name(type);
            if (name != IntPtr.Zero)
            {
                string text = Marshal.PtrToStringAnsi(name);
                il2cpp_free(name);
                return text;
            }
        }
        catch { }
        return $"0x{klass:X}";
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAllocation(IntPtr prof, IntPtr obj, IntPtr klass)
    {
        if (!recording || obj == IntPtr.Zero) return;
        long size = il2cpp_object_get_size(obj);
        bool main = Environment.CurrentManagedThreadId == mainThreadId;
        lock (Gate)
        {
            if (!stats.TryGetValue(klass, out Stat s)) stats[klass] = s = new Stat();
            s.Count++; s.Bytes += size;
            if (main) { s.MainCount++; s.MainBytes += size; }
            if (s.Count % StackSampling == 1 && WantsStack(klass))
            {
                if (!stacks.TryGetValue(klass, out var perClass)) stacks[klass] = perClass = new Dictionary<string, int>();
                string key = StackKey();
                perClass[key] = perClass.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnShutdown(IntPtr prof) { }

    [DllImport("GameAssembly")]
    private static extern void il2cpp_profiler_install(IntPtr prof, delegate* unmanaged[Cdecl]<IntPtr, void> shutdown);
    [DllImport("GameAssembly")]
    private static extern void il2cpp_profiler_install_allocation(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback);
    [DllImport("GameAssembly")] private static extern void il2cpp_profiler_set_events(int events);
    [DllImport("GameAssembly")] private static extern uint il2cpp_object_get_size(IntPtr obj);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_get_type(IntPtr klass);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_type_get_name(IntPtr type);
    [DllImport("GameAssembly")] private static extern void il2cpp_free(IntPtr ptr);
    [DllImport("kernel32")] private static extern void GetCurrentThreadStackLimits(ulong* lowLimit, ulong* highLimit);
}
