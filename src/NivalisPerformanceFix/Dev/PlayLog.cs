using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using NivalisPerformanceFix.Features;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer play log: records how the game runs during normal play, to find the places and situations worth
/// optimizing next. Local file only (BepInEx/NivalisPerformanceFix.playlog.log, appended across sessions), one
/// record per line as key=value pairs:
///  * MIN   every Interval seconds: frame-time stats of that period + context;
///  * SPIKE every frame much slower than the recent median + context;
///  * MARK  when the player presses MarkKey ("I felt a hitch here") + stats of the last seconds + context.
/// Context = scene, camera position, characters (total / on screen), managed heap, GC count, paused, mod state.
/// Costs one median update every 30 frames and one file write per record.
/// </summary>
internal sealed class PlayLog
{
    private ConfigEntry<bool> enabled;
    private ConfigEntry<string> markKeyName;
    private ConfigEntry<float> interval;
    private Key markKey;
    private StreamWriter file;

    private readonly List<float> period = new(16384);
    private float periodTime;
    private int periodGcStart = -1;

    // rolling window for the spike threshold and the MARK "last seconds" stats
    private readonly float[] window = new float[300];
    private int windowPos, windowCount, sinceMedian;
    private float median = 1f;
    private int lastGc, prevGcDelta;

    public bool IsOn => enabled.Value;

    public void Bind(ConfigFile config)
    {
        enabled = config.Bind("Developer", "PlayLog", true,
            "With developer tools on: record frame-time stats, hitches and F11 marks during play (local file only).");
        markKeyName = config.Bind("Developer", "MarkKey", "F11", "Key to press when you feel a hitch: records the moment and context.");
        interval = config.Bind("Developer", "PlayLogInterval", 60f, "Seconds between two periodic records.");
    }

    public void Start()
    {
        Enum.TryParse(markKeyName.Value?.Trim(), true, out markKey);
        if (enabled.Value)
            Write($"SESSION version={Plugin.Version} workers={Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount} " +
                  $"cpu_threads={Environment.ProcessorCount} screen={Screen.width}x{Screen.height}");
    }

    public void Update(float dt)
    {
        if (!enabled.Value) return;
        int gc = Il2CppSystem.GC.CollectionCount(0);
        if (periodGcStart < 0) { periodGcStart = gc; lastGc = gc; }
        int gcDelta = gc - lastGc; lastGc = gc;
        int gcNear = gcDelta + prevGcDelta; prevGcDelta = gcDelta; // a GC finishes at the start of a frame

        if (windowCount >= 60 && dt > Math.Max(2f * median, median + 0.008f) && dt < 5f)
            Write($"SPIKE ms={Ms(dt)} median={Ms(median)} gc={(gcNear > 0 ? 1 : 0)} {Context()}");

        Keyboard kb = Keyboard.current;
        if (kb != null && markKey != Key.None && kb[markKey].wasPressedThisFrame)
        {
            var recent = Recent(5f);
            Write($"MARK last5s_fps={Fps(recent)} last5s_max={Ms(recent.DefaultIfEmpty(0).Max())} " +
                  $"last5s_over20={recent.Count(x => x > 0.020f)} {Context()}");
            Plugin.Log.LogMessage("Hitch marked, thanks");
        }

        window[windowPos] = dt; windowPos = (windowPos + 1) % window.Length;
        if (windowCount < window.Length) windowCount++;
        if (++sinceMedian >= 30)
        {
            sinceMedian = 0;
            var tmp = window.Take(windowCount).OrderBy(x => x).ToArray();
            median = tmp[tmp.Length / 2];
        }

        period.Add(dt);
        if ((periodTime += dt) < interval.Value) return;
        var sorted = period.OrderBy(x => x).ToList();
        double sum = sorted.Sum(x => (double)x);
        int n1 = Math.Max(1, sorted.Count / 100);
        double worst = sorted.Skip(sorted.Count - n1).Sum(x => (double)x);
        Write($"MIN secs={F(periodTime, "F0")} frames={sorted.Count} fps={F(sorted.Count / sum, "F1")} low1={F(n1 / worst, "F1")} " +
              $"p99={Ms(sorted[(int)(sorted.Count * 0.99)])} max={Ms(sorted[^1])} " +
              $"over20={sorted.Count(x => x > 0.020f)} over30={sorted.Count(x => x > 0.030f)} " +
              $"gcs={gc - periodGcStart} {Context()}");
        period.Clear(); periodTime = 0; periodGcStart = gc;
    }

    private List<float> Recent(float seconds)
    {
        var list = new List<float>();
        float t = 0;
        for (int i = 1; i <= windowCount && t < seconds; i++)
        {
            float d = window[(windowPos - i + window.Length) % window.Length];
            list.Add(d); t += d;
        }
        return list;
    }

    private static string Fps(List<float> dts) => dts.Count == 0 ? "0" : (dts.Count / dts.Sum()).ToString("F1", CultureInfo.InvariantCulture);
    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
    private static string Ms(float s) => (s * 1000).ToString("F1", CultureInfo.InvariantCulture);

    private static string Context()
    {
        string scene = "?";
        try { scene = SceneManager.GetActiveScene().name?.Replace(' ', '_'); } catch { }
        string pos = "?";
        try
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                pos = string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0},{2:F0}", p.x, p.y, p.z);
            }
        }
        catch { }
        var (chars, visible) = AnimationLod.Instance?.CountCharacters() ?? (-1, -1);
        long heapMb = -1;
        try { heapMb = il2cpp_gc_get_used_size() / (1024 * 1024); } catch { }
        return $"scene={scene} pos={pos} chars={chars} visible={visible} heap_mb={heapMb} " +
               $"paused={(Time.timeScale == 0 ? 1 : 0)} mod={(Plugin.MasterEnabled.Value ? 1 : 0)}";
    }

    [System.Runtime.InteropServices.DllImport("GameAssembly")] private static extern long il2cpp_gc_get_used_size();

    private void Write(string line)
    {
        try
        {
            file ??= new StreamWriter(Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix.playlog.log"), true)
                { AutoFlush = true };
            file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
        }
        catch { }
    }
}
