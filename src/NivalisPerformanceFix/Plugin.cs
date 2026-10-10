using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using NivalisPerformanceFix.Dev;
using NivalisPerformanceFix.Features;
using UnityEngine;

namespace NivalisPerformanceFix;

[BepInPlugin(Guid, Name, Version)]
[BepInProcess("Nivalis Nights.exe")]
public class Plugin : BasePlugin
{
    public const string Guid = "hoho92.nivalisperformancefix";
    public const string Name = "Nivalis Performance Fix";
    public const string Version = "1.0.8";

    // prototypes this mod replaces; running both would apply some optimizations twice
    private static readonly string[] Superseded = { "hoho92.nivalis.animlod", "hoho92.nivalis.perftweaks", "hoho92.nivalis.animbatch" };

    // other mods tested together with this one (2026-10-04): no conflict, only reported in the startup summary
    private static readonly (string guid, string name)[] Compatible =
    {
        ("hvizeu.nivalis.unofficialpatch", "Nivalis Unofficial Patch"),
        ("hvizeu.nivalis.trackedquestshud", "Tracked Quests HUD"),
    };

    internal static new ManualLogSource Log;
    internal static Harmony Harmony;
    internal static ConfigEntry<bool> MasterEnabled;

    internal static readonly List<Feature> Features = new()
    {
        new FastLoading(),
        new AnimationLod(),
        new CharacterDetailsLod(),
        new PausedCharacters(),
        new PlayerGuiLayout(),
        new AgentThrottle(),
        new NavPathThrottle(),
        new EconomyStockCheck(),
        new StuckAgents(),
        new HudRedraws(),
        new SpawnSpread(),
        new CameraThrottle(),
        new LensFlareOnce(),
        new ParticleCatchUp(),
        new EnumFlagsInline(),
        new SaveMenusReuse(),
        new SaveRowsParking(),
        new ReviewRows(),
        new ContactRows(),
        new LazyLists(),
        new RecipeDetails(),
        new MenuScrollbars(),
        new FishDatabaseRows(),
        new ShopWindows(),
        new DialogueVoicePrefetch(),
        new ExitCrashFix(),
        new GcFrequency(),
        new QuestHudRefresh(),
        new QuestHudCompat(),
    };
    internal static DevTools Dev;

    public override void Load()
    {
        Log = base.Log;
        NivalisPerformanceFix.Dev.BootLog.Mark("Nivalis Performance Fix: plugin load start");
        NivalisPerformanceFix.Dev.BootLog.StartPixIfAsked();
        NivalisPerformanceFix.Dev.FindProbe.StartIfAsked();
        Harmony = new Harmony(Guid);

        MasterEnabled = Config.Bind("General", "Enabled", true, "Master switch for every optimization below.");
        JobWorkers.Bind(Config);
        foreach (Feature f in Features) f.Bind(Config);
        Dev = new DevTools(Features);
        // separate file, kept only by DevTools: in-game config menus (Nivalis Config Manager) list the plugin's
        // Config, so developer settings stay out of the players' menu
        Dev.Bind(new ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, Guid + ".dev.cfg"), true,
            MetadataHelper.GetMetadata(this)));

        JobWorkers.Apply();
        foreach (Feature f in Features.Where(f => !f.InstallLate)) TimedInstall(f);

        ClassInjector.RegisterTypeInIl2Cpp<PerformanceBehaviour>();
        AddComponent<PerformanceBehaviour>();
        NivalisPerformanceFix.Dev.BootLog.Mark("Nivalis Performance Fix: plugin load end");
    }

    private static void TimedInstall(Feature f)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        f.Install();
        NivalisPerformanceFix.Dev.BootLog.Install(f.Name, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Second stage, once every plugin is loaded (first frame).</summary>
    internal static void LateStart()
    {
        NivalisPerformanceFix.Dev.BootLog.Mark("first frame (late start)");
        foreach (Feature f in Features.Where(f => f.InstallLate)) TimedInstall(f);
        var devWatch = System.Diagnostics.Stopwatch.StartNew();
        Dev.Start();
        NivalisPerformanceFix.Dev.BootLog.Install("developer tools start", devWatch.Elapsed.TotalMilliseconds);

        var superseded = Superseded.Where(g => IL2CPPChainloader.Instance.Plugins.ContainsKey(g)).ToList();
        int usable = Features.Count(f => f.Installed || f.Problem?.StartsWith("not needed") != true);
        int on = Features.Count(f => f.Active);
        Log.LogMessage($"{Name} {Version}: {on}/{usable} optimizations active" + (MasterEnabled.Value ? "" : " (master switch OFF)"));
        Log.LogInfo("  " + JobWorkers.StatusLine);
        foreach (Feature f in Features)
            Log.LogInfo("  " + (f.Problem?.StartsWith("not needed") == true ? $"{f.Name}: {f.Problem}" : f.StatusLine));
        foreach (var (guid, name) in Compatible.Where(c => IL2CPPChainloader.Instance.Plugins.ContainsKey(c.guid)))
            Log.LogInfo($"  {name} detected: compatible");
        if (JobWorkers.RestartNeeded)
            Log.LogWarning($"{Name}: job worker count changed, restart the game to apply it.");
        foreach (Feature f in Features.Where(f => !f.Installed && f.Problem?.StartsWith("not needed") != true))
            Log.LogWarning($"{Name}: {f.Name} disabled: {f.Problem}. The game was probably updated; check for a mod update.");
        if (superseded.Count > 0)
            Log.LogError($"{Name}: old test versions are still installed ({string.Join(", ", superseded)}). " +
                         "Remove NivalisAnimLod.dll / NivalisPerfTweaks.dll / NivalisAnimBatch.dll from BepInEx/plugins.");
    }
}

/// <summary>Per-frame driver (one MonoBehaviour for the whole mod).</summary>
public class PerformanceBehaviour : MonoBehaviour
{
    public PerformanceBehaviour(IntPtr ptr) : base(ptr) { }

    private bool started;

    /// <summary>Set by developer tools to collect each feature's Tick time (ms, summed).</summary>
    internal static Dictionary<string, double> TickTimes;

    private static void Add(Dictionary<string, double> times, string name, long t0)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        times[name] = times.TryGetValue(name, out double v) ? v + ms : ms;
    }

    private void Update()
    {
        if (!started)
        {
            started = true;
            Plugin.LateStart();
        }
        var times = TickTimes; // developer tools: per-feature tick time (null = not measured)
        foreach (Feature f in Plugin.Features)
        {
            if (!f.Installed) continue;
            long t0 = times != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { f.Tick(); }
            catch (Exception e) { f.TickFailed(e); }
            if (times != null) Add(times, f.Name, t0);
        }
        long l0 = times != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try { Features.ListTools.Tick(); }
        catch (Exception e) { Plugin.Log.LogDebug($"ListTools: {e.Message}"); }
        if (times != null) Add(times, "ListTools", l0);
        Plugin.Dev.Update();
        NivalisPerformanceFix.Dev.BootLog.Update();
    }
}
