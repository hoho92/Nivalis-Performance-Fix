using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using NivalisPerformanceFix.Features;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: timeline of the game launch, in seconds since the process started. Marks: this plugin's Load
/// (start / end, with each feature's install time), the first frame, every change of the active scene, the title
/// screen (P_MainMenu), then the first loading screen (start / end) when a save is loaded; plus every frame over
/// SlowMs with the active scene. Written to the log as one block when the first loading ends, or at the title screen
/// + 10 s when no save is loaded (then the game quits if bench/boot.quit exists: autonomous launch measurements).
/// The loading screen shown before the title (Logo_Screen) is part of the launch; the report waits for the first
/// loading that starts after the title. bench/boot.pix (one line: seconds) asks the NivalisPixCapture task for a
/// timing capture from the plugin load, to see what the engine does before the first frame.
/// Recording costs nothing measurable (a clock read per frame, a name lookup every 10 frames until the title).
/// </summary>
internal static class BootLog
{
    private const float SlowMs = 100f, TitleExtraSeconds = 10f, MaxSeconds = 240f;

    private static readonly double startOffset = SinceProcessStart();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly List<string> lines = new();
    private static readonly List<(string name, double ms)> installs = new();
    private static bool done, titleSeen, wasLoading, loadingSeen; // loadingSeen: a loading after the title
    private static double titleAt = -1, lastFrame = -1;
    private static string scene = "";
    private static int frames;

    private static double SinceProcessStart()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; }
        catch { return 0; }
    }

    internal static double Now => startOffset + clock.Elapsed.TotalSeconds;

    internal static void Mark(string what)
    {
        if (!done) lines.Add($"  {Now,7:F2} s  {what}");
    }

    internal static void Install(string feature, double ms)
    {
        if (!done) installs.Add((feature, ms));
    }

    /// <summary>
    /// Developer tools off (players): nothing more is recorded nor reported. The marks before the first frame cost
    /// nothing, but the report printed a launch timeline in every player's console.
    /// </summary>
    internal static void Stop()
    {
        if (FindProbe.On) return; // asked for by a file: report as usual
        done = true;
        lines.Clear();
        installs.Clear();
    }

    /// <summary>Once per frame from the plugin behaviour, until the report.</summary>
    internal static void Update()
    {
        if (done) return;
        double now = Now;
        frames++;
        if (lastFrame >= 0 && (now - lastFrame) * 1000 > SlowMs)
            lines.Add($"  {lastFrame,7:F2} s  slow frame {(now - lastFrame) * 1000:F0} ms (scene {scene}, loaded scenes {SceneManager.sceneCount})");
        lastFrame = now;

        string active = SceneManager.GetActiveScene().name;
        if (active != scene) { scene = active; Mark($"active scene: {active}"); }
        if (!titleSeen && frames % 10 == 0 && GameObject.Find("P_MainMenu(Clone)") != null)
        {
            titleSeen = true; titleAt = now;
            Mark("title screen shown");
        }
        bool loading = ListTools.Loading;
        if (loading && !wasLoading) { loadingSeen = titleSeen; Mark("loading screen start"); }
        if (!loading && wasLoading)
        {
            Mark("loading screen end");
            if (loadingSeen) Report("first save loading done");
            else if (titleSeen) titleAt = now; // the launch loading ended: title wait counts from here
        }
        wasLoading = loading;
        if (titleSeen && !loadingSeen && !loading && now - titleAt > TitleExtraSeconds) Report("title screen + 10 s");
        else if (now > MaxSeconds) Report("time limit");
    }

    /// <summary>From the plugin load: a PIX timing capture of the launch when bench/boot.pix exists.</summary>
    internal static void StartPixIfAsked()
    {
        try
        {
            string bench = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix", "bench");
            string ask = Path.Combine(bench, "boot.pix");
            if (!File.Exists(ask)) return;
            int seconds = int.TryParse(File.ReadAllText(ask).Trim(), out int s) ? s : 40;
            string name = $"boot-{DateTime.Now:HHmmss}";
            string requests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NivalisPix", "requests");
            File.WriteAllText(Path.Combine(requests, "request.txt"), $"name={name}\nseconds={seconds}\n");
            Process.Start(new ProcessStartInfo("schtasks", "/run /tn NivalisPixCapture") { CreateNoWindow = true, UseShellExecute = false });
            Mark($"PIX capture {name} requested ({seconds} s)");
        }
        catch (Exception e) { Mark($"PIX capture not started ({e.Message})"); }
    }

    private static void Report(string why)
    {
        done = true;
        var sb = new StringBuilder($"Boot timeline ({why}), seconds since the game process started:\n");
        foreach (string l in lines) sb.AppendLine(l);
        double total = 0;
        foreach (var (_, ms) in installs) total += ms;
        sb.AppendLine($"  feature installs: {total:F0} ms in all");
        installs.Sort((a, b) => b.ms.CompareTo(a.ms));
        foreach (var (name, ms) in installs)
            if (ms >= 1) sb.AppendLine($"    {ms,7:F1} ms  {name}");
        FindProbe.Report(sb);
        Plugin.Log.LogMessage(sb.ToString());

        string quit = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix", "bench", "boot.quit");
        if (why == "title screen + 10 s" && File.Exists(quit)) Application.Quit();
    }
}
