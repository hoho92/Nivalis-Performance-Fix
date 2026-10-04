using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using Unity.Jobs.LowLevel.Unsafe;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Unity creates one job worker thread per logical CPU minus one (15 on an 8-core/16-thread CPU). This game's
/// main thread schedules thousands of tiny jobs per second and wakes the workers for each batch; with that many
/// workers, waking and waiting costs more than the work. Measured on a Ryzen 7 9800X3D (8 cores / 16 threads):
/// 15 workers 77.5 FPS, 6 workers 91.1 FPS (+18%, 1% lows +22%), 4 workers slightly worse lows than 6.
/// The count can only be set before the engine starts (boot.config "job-worker-count"), so we write it there and
/// it applies from the next launch. Steam updates / file verification restore the original boot.config; we then
/// write it again. The original file is backed up once as boot.config.npf-backup.
/// </summary>
internal static class JobWorkers
{
    private const string Key = "job-worker-count";

    private static ConfigEntry<string> mode;
    private static ConfigEntry<int> count;

    public static string StatusLine { get; private set; } = "Job worker threads: not checked";
    public static bool RestartNeeded { get; private set; }

    public static void Bind(ConfigFile config)
    {
        mode = config.Bind("JobWorkers", "Mode", "Auto",
            new ConfigDescription(
                "Auto = choose from the CPU (physical cores - 2, between 2 and 8). Manual = use Count. " +
                "Off = never touch boot.config. Changes apply from the next game launch.",
                new AcceptableValueList<string>("Auto", "Manual", "Off")));
        count = config.Bind("JobWorkers", "Count", 6,
            new ConfigDescription("Worker thread count used when Mode = Manual.", new AcceptableValueRange<int>(1, 32)));
        // changed from an in-game config menu: write boot.config now, the count itself needs a restart
        mode.SettingChanged += (_, _) => ApplyFromMenu();
        count.SettingChanged += (_, _) => ApplyFromMenu();
    }

    private static void ApplyFromMenu()
    {
        Apply();
        if (RestartNeeded) Plugin.Log.LogWarning($"{Plugin.Name}: {StatusLine}");
        else Plugin.Log.LogInfo($"{Plugin.Name}: {StatusLine}");
    }

    public static int Recommended()
    {
        int cores = PhysicalCores();
        return Math.Clamp(cores - 2, 2, 8);
    }

    public static void Apply()
    {
        int running = SafeRunningWorkers();
        string m = mode.Value.Trim();
        if (m.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            StatusLine = $"Job worker threads: not managed (Mode = Off), {running} running";
            return;
        }
        int want = m.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? count.Value : Recommended();

        string path = Path.Combine(Paths.GameRootPath, Paths.ProcessName + "_Data", "boot.config");
        try
        {
            if (!File.Exists(path))
            {
                StatusLine = "Job worker threads: boot.config not found, not managed";
                return;
            }
            string text = File.ReadAllText(path);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int idx = Array.FindIndex(lines, l => l.TrimStart().StartsWith(Key + "=", StringComparison.Ordinal));
            string current = idx >= 0 ? lines[idx].Substring(lines[idx].IndexOf('=') + 1).Trim() : null;

            if (current != want.ToString())
            {
                string backup = path + ".npf-backup";
                if (!File.Exists(backup)) File.Copy(path, backup);
                if (idx >= 0) lines[idx] = $"{Key}={want}";
                else
                {
                    // insert before the trailing empty line, if any
                    int at = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
                    var list = new System.Collections.Generic.List<string>(lines);
                    list.Insert(at, $"{Key}={want}");
                    lines = list.ToArray();
                }
                File.WriteAllText(path, string.Join(nl, lines));
            }
        }
        catch (Exception e)
        {
            StatusLine = $"Job worker threads: could not update boot.config ({e.Message})";
            return;
        }

        RestartNeeded = running > 0 && running != want;
        StatusLine = RestartNeeded
            ? $"Job worker threads: set to {want} in boot.config, RESTART THE GAME to apply (running: {running})"
            : $"Job worker threads: {want} (active)";
    }

    private static int SafeRunningWorkers()
    {
        try { return JobsUtility.JobWorkerMaximumCount; }
        catch { return -1; }
    }

    // ---- physical core count (GetLogicalProcessorInformationEx, RelationProcessorCore)

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    private static int PhysicalCores()
    {
        try
        {
            int len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len <= 0) return Environment.ProcessorCount / 2;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return Environment.ProcessorCount / 2;
                int cores = 0;
                for (int off = 0; off < len;)
                {
                    int size = Marshal.ReadInt32(buf, off + 4); // { int Relationship; int Size; ... }
                    if (size <= 0) break;
                    cores++;
                    off += size;
                }
                return cores > 0 ? cores : Environment.ProcessorCount / 2;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch
        {
            return Environment.ProcessorCount / 2;
        }
    }
}
