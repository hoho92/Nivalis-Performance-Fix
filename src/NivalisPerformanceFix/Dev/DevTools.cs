using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using NivalisPerformanceFix.Features;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Measurement tools used to tune the mod, off unless [Developer] Enabled = true in hoho92.nivalisperformancefix.dev.cfg:
///  * ToggleKey: switch the whole mod on/off in game;
///  * BenchKey: automatic A/B benchmark (BenchTarget on/off, ABBA order cancels drift, 2 s warm-up per phase);
///  * MeasureKey: frame-time statistics over MeasureSeconds (for comparisons outside the mod);
///  * SurveyKey: one-shot inventory of the scene (see <see cref="SceneSurvey"/>);
///  * AllocKey: which classes the game allocates the most during AllocSeconds (see <see cref="AllocTracker"/>);
///  * TimePinKey / WeatherPinKey: pin the time of day / force a weather for comparable runs (see <see cref="WorldPin"/>);
///  * frame split (crowd / render / rest) in the reports and the play log (see <see cref="FrameSplit"/>);
///  * PlayLog: frame-time stats, hitches and player marks during normal play (see <see cref="PlayLog"/>).
/// Benchmark / measurement results go to the BepInEx log and to BepInEx/NivalisPerformanceFix.dev.log (kept across sessions).
/// </summary>
internal sealed class DevTools
{
    private ConfigEntry<bool> enabled;
    private ConfigEntry<string> toggleKeyName, benchKeyName, measureKeyName, surveyKeyName, allocKeyName, allocStackClasses, benchTarget;
    private ConfigEntry<float> benchPhaseSeconds, measureSeconds, allocSeconds;
    private ConfigEntry<int> benchRounds;

    private readonly List<Feature> features;
    private Key toggleKey, benchKey, measureKey, surveyKey, allocKey;
    private StreamWriter devLog;
    private readonly PlayLog playLog = new();

    public DevTools(List<Feature> features) => this.features = features;

    public void Bind(ConfigFile config)
    {
        const string s = "Developer";
        enabled = config.Bind(s, "Enabled", false, "Enable the measurement tools below (for testing / tuning only).");
        toggleKeyName = config.Bind(s, "ToggleKey", "F8", "Key that switches the whole mod on/off in game.");
        benchKeyName = config.Bind(s, "BenchKey", "F9",
            "Key that starts/cancels an automatic A/B benchmark. Stand still in a busy place, don't pause.");
        benchTarget = config.Bind(s, "BenchTarget", "All",
            "What the benchmark switches: All (whole mod), one section name (Animation, CharacterDetails, PausedCharacters, PlayerGui, LensFlares, Agents, Navigation, Spawns, Cameras, GarbageCollector), or another mod's setting as plugin.guid:Section/Key (off = false / 0).");
        benchPhaseSeconds = config.Bind(s, "BenchPhaseSeconds", 8f, "Measured seconds per benchmark phase (after 2 s warm-up).");
        benchRounds = config.Bind(s, "BenchRounds", 3, "Rounds of on/off phases (alternating ABBA order).");
        measureKeyName = config.Bind(s, "MeasureKey", "F10", "Key that measures frame times for MeasureSeconds.");
        measureSeconds = config.Bind(s, "MeasureSeconds", 20f, "Length of a measurement.");
        surveyKeyName = config.Bind(s, "SurveyKey", "F12",
            "Key that writes a one-shot inventory of the scene to BepInEx/NivalisPerformanceFix (read-only, ~1 s hitch).");
        allocKeyName = config.Bind(s, "AllocKey", "F7", "Key that records which classes the game allocates the most.");
        allocSeconds = config.Bind(s, "AllocSeconds", 10f, "Length of an allocation recording.");
        allocStackClasses = config.Bind(s, "AllocStackClasses", "VenueTasks,NavMeshPath,GUILayoutGroup,IngredientProcessingType,System.String",
            "Comma-separated class names (substrings) whose allocating code the allocation recording also reports.");
        playLog.Bind(config);
        WorldPin.Bind(config);
    }

    public void Start()
    {
        if (!enabled.Value) return;
        Enum.TryParse(toggleKeyName.Value?.Trim(), true, out toggleKey);
        Enum.TryParse(benchKeyName.Value?.Trim(), true, out benchKey);
        Enum.TryParse(measureKeyName.Value?.Trim(), true, out measureKey);
        Enum.TryParse(surveyKeyName.Value?.Trim(), true, out surveyKey);
        Enum.TryParse(allocKeyName.Value?.Trim(), true, out allocKey);
        FrameSplit.Install();
        WorldPin.Start();
        playLog.Start();
        Plugin.Log.LogInfo($"Developer tools on: {toggleKey} toggle, {benchKey} benchmark ({benchTarget.Value}), {measureKey} measure, " +
                           $"{surveyKey} scene survey, {allocKey} allocations, play log {(playLog.IsOn ? "on (F11 = mark a hitch)" : "off")}");
    }

    public void Update()
    {
        if (!enabled.Value) return;
        float dt = Direct.UnscaledDeltaTime;
        Keyboard kb = Keyboard.current;
        FrameSplit.Tick();
        playLog.Update(dt);

        if (Pressed(kb, toggleKey) && phase < 0)
        {
            Plugin.MasterEnabled.Value = !Plugin.MasterEnabled.Value;
            Plugin.Log.LogMessage($"Nivalis Performance Fix {(Plugin.MasterEnabled.Value ? "ON" : "OFF")}");
        }
        if (Pressed(kb, benchKey))
        {
            if (phase < 0) StartBench(); else StopBench("cancelled");
        }
        if (Pressed(kb, measureKey) && phase < 0)
        {
            if (measureLeft >= 0) { measureLeft = -1; Plugin.Log.LogMessage("Measurement cancelled"); }
            else
            {
                measureLeft = measureSeconds.Value; measureFrames.Clear(); measureSplit.Reset();
                Plugin.Log.LogMessage($"Measuring {measureSeconds.Value:F0} s...");
            }
        }
        if (Pressed(kb, surveyKey)) SceneSurvey.Run();
        if (Pressed(kb, allocKey)) AllocTracker.Start(allocSeconds.Value, allocStackClasses.Value, Report);
        AllocTracker.Update(dt);
        WorldPin.Update(kb);
        if (measureLeft >= 0) MeasureStep(dt);
        if (phase >= 0) BenchStep(dt);
    }

    /// <summary>Key pressed this frame without Ctrl / Shift / Alt: the game's own developer shortcuts use those
    /// modifiers (Ctrl+Shift+F12 opens its dev menu, Ctrl+F9 is a dev key), so a combination never triggers our tools.</summary>
    internal static bool Pressed(Keyboard kb, Key k) =>
        kb != null && k != Key.None && Direct.WasPressedThisFrame(kb[k]) &&
        !Direct.IsPressed(kb.ctrlKey) && !Direct.IsPressed(kb.shiftKey) && !Direct.IsPressed(kb.altKey);

    private void Report(string text)
    {
        Plugin.Log.LogMessage(text);
        Write(text);
    }

    private void Write(string text)
    {
        try
        {
            devLog ??= DevFile.Open("NivalisPerformanceFix.dev.log");
            devLog.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}");
        }
        catch { }
    }

    // ---- measurement

    private float measureLeft = -1;
    private readonly List<float> measureFrames = new(8192);
    private readonly FrameSplit.Window measureSplit = new();

    private void MeasureStep(float dt)
    {
        measureFrames.Add(dt);
        measureSplit.Add(dt);
        if ((measureLeft -= dt) > 0) return;
        measureLeft = -1;
        var (fps, low) = Stats(measureFrames);
        var sorted = measureFrames.OrderBy(x => x).ToList();
        float p99 = sorted[(int)(sorted.Count * 0.99)] * 1000, max = sorted[^1] * 1000;
        Report($"Measurement: {fps:F1} FPS avg, 1% low {low:F1}, p99 {p99:F1} ms, max {max:F1} ms, " +
               $">20 ms: {measureFrames.Count(x => x > 0.020f)}, >30 ms: {measureFrames.Count(x => x > 0.030f)} " +
               $"({measureFrames.Count} frames, mod {(Plugin.MasterEnabled.Value ? "on" : "off")})" +
               (measureSplit.ToString() is { Length: > 0 } split ? "\n  " + split : ""));
    }

    // ---- A/B benchmark

    private const float Warmup = 2f;
    private ConfigEntryBase benchEntry;
    private object benchSaved, benchOn, benchOff;
    private List<bool> plan;
    private int phase = -1;
    private float phaseTime;
    private readonly List<float> phaseFrames = new(4096);
    private readonly Dictionary<bool, List<(double fps, double low)>> results = new();
    private readonly Dictionary<bool, FrameSplit.Window> splits = new();

    private void StartBench()
    {
        string t = benchTarget.Value.Trim();
        benchEntry = t.Contains(':') ? OtherModEntry(t)
            : t.Equals("All", StringComparison.OrdinalIgnoreCase) ? Plugin.MasterEnabled
            : features.FirstOrDefault(f => f.Enabled.Definition.Section.Equals(t, StringComparison.OrdinalIgnoreCase))?.Enabled;
        if (benchEntry == null) { Plugin.Log.LogError($"Unknown BenchTarget '{t}'"); return; }

        benchSaved = benchEntry.BoxedValue;
        // on = the entry's current value (true for a switch), off = false / 0
        if (benchEntry.SettingType == typeof(bool)) { benchOn = true; benchOff = false; }
        else
        {
            benchOn = benchSaved;
            benchOff = Convert.ChangeType(0, benchEntry.SettingType);
            if (Equals(benchOn, benchOff)) { Plugin.Log.LogError($"BenchTarget '{t}' is 0: set the value to test first"); return; }
        }
        // the phases switch the entry in memory only: quitting during a benchmark leaves the player's config as it was
        benchEntry.ConfigFile.SaveOnConfigSet = false;
        plan = new List<bool>();
        for (int r = 0; r < Math.Max(1, benchRounds.Value); r++)
            plan.AddRange(r % 2 == 0 ? new[] { false, true } : new[] { true, false });
        results.Clear();
        splits.Clear();
        Plugin.Log.LogMessage($"Benchmark '{t}' started: {plan.Count} phases, ~{plan.Count * (Warmup + benchPhaseSeconds.Value):F0} s. Stay still, don't pause.");
        BeginPhase(0);
    }

    private void BeginPhase(int i)
    {
        phase = i; phaseTime = 0; phaseFrames.Clear();
        benchEntry.BoxedValue = plan[i] ? benchOn : benchOff;
    }

    /// <summary>
    /// "plugin.guid:Section/Key": a bool or number setting of another installed mod (off = false / 0), e.g.
    /// hvizeu.nivalis.unofficialpatch:Graphics/AnimationLodDistanceReduction. Only meaningful for settings that mod
    /// reads while playing.
    /// </summary>
    private static ConfigEntryBase OtherModEntry(string target)
    {
        int colon = target.IndexOf(':'), slash = target.IndexOf('/', colon + 1);
        if (slash < 0) return null;
        if (!BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.TryGetValue(target[..colon], out var info) ||
            info.Instance is not BepInEx.Unity.IL2CPP.BasePlugin plugin) return null;
        var key = new ConfigDefinition(target[(colon + 1)..slash], target[(slash + 1)..]);
        if (!plugin.Config.ContainsKey(key)) return null;
        ConfigEntryBase entry = plugin.Config[key];
        return entry.SettingType == typeof(bool) || entry.SettingType == typeof(float) || entry.SettingType == typeof(int)
            ? entry : null;
    }

    private void BenchStep(float dt)
    {
        phaseTime += dt;
        if (phaseTime > Warmup)
        {
            phaseFrames.Add(dt);
            if (!splits.TryGetValue(plan[phase], out var split)) splits[plan[phase]] = split = new FrameSplit.Window();
            split.Add(dt);
        }
        if (phaseTime < Warmup + benchPhaseSeconds.Value) return;
        if (!results.TryGetValue(plan[phase], out var list)) results[plan[phase]] = list = new();
        list.Add(Stats(phaseFrames));
        if (phase + 1 < plan.Count) BeginPhase(phase + 1); else StopBench(null);
    }

    private void StopBench(string why)
    {
        phase = -1;
        benchEntry.BoxedValue = benchSaved;
        benchEntry.ConfigFile.SaveOnConfigSet = true;
        if (why != null) { Plugin.Log.LogMessage("Benchmark " + why); return; }
        var sb = new StringBuilder($"Benchmark '{benchTarget.Value}' (job workers {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount}):\n");
        sb.AppendLine("  state | avg FPS | 1% low | per phase");
        foreach (bool on in new[] { false, true })
            if (results.TryGetValue(on, out var r))
                sb.AppendLine($"  {(on ? "on " : "off")}   | {r.Average(v => v.fps),7:F1} | {r.Average(v => v.low),6:F1} | " +
                              string.Join(" ", r.Select(v => v.fps.ToString("F1"))) +
                              (splits.TryGetValue(on, out var split) && split.ToString() is { Length: > 0 } text ? " | " + text : ""));
        Report(sb.ToString());
    }

    private static (double fps, double low) Stats(List<float> dts)
    {
        if (dts.Count == 0) return (0, 0);
        double sum = 0; foreach (float d in dts) sum += d;
        var sorted = dts.OrderByDescending(d => d).ToList();
        int n = Math.Max(1, dts.Count / 100);
        double worst = 0; for (int i = 0; i < n; i++) worst += sorted[i];
        return (dts.Count / sum, n / worst);
    }
}
