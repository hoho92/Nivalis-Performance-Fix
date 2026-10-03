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
    public const string Version = "1.0.1";

    // prototypes this mod replaces; running both would apply some optimizations twice
    private static readonly string[] Superseded = { "hoho92.nivalis.animlod", "hoho92.nivalis.perftweaks", "hoho92.nivalis.animbatch" };

    internal static new ManualLogSource Log;
    internal static Harmony Harmony;
    internal static ConfigEntry<bool> MasterEnabled;

    internal static readonly List<Feature> Features = new()
    {
        new AnimationLod(),
        new AgentThrottle(),
        new NavPathThrottle(),
        new CameraThrottle(),
        new LightProbeWalk(),
        new GcFrequency(),
        new QuestHudCompat(),
    };
    internal static DevTools Dev;

    public override void Load()
    {
        Log = base.Log;
        Harmony = new Harmony(Guid);

        MasterEnabled = Config.Bind("General", "Enabled", true, "Master switch for every optimization below.");
        JobWorkers.Bind(Config);
        foreach (Feature f in Features) f.Bind(Config);
        Dev = new DevTools(Features);
        Dev.Bind(Config);

        JobWorkers.Apply();
        foreach (Feature f in Features.Where(f => !f.InstallLate)) f.Install();

        ClassInjector.RegisterTypeInIl2Cpp<PerformanceBehaviour>();
        AddComponent<PerformanceBehaviour>();
    }

    /// <summary>Second stage, once every plugin is loaded (first frame).</summary>
    internal static void LateStart()
    {
        foreach (Feature f in Features.Where(f => f.InstallLate)) f.Install();
        Dev.Start();

        var superseded = Superseded.Where(g => IL2CPPChainloader.Instance.Plugins.ContainsKey(g)).ToList();
        int usable = Features.Count(f => f.Installed || f.Problem?.StartsWith("not needed") != true);
        int on = Features.Count(f => f.Active);
        Log.LogMessage($"{Name} {Version}: {on}/{usable} optimizations active" + (MasterEnabled.Value ? "" : " (master switch OFF)"));
        Log.LogInfo("  " + JobWorkers.StatusLine);
        foreach (Feature f in Features)
            Log.LogInfo("  " + (f.Problem?.StartsWith("not needed") == true ? $"{f.Name}: {f.Problem}" : f.StatusLine));
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

    private void Update()
    {
        if (!started)
        {
            started = true;
            Plugin.LateStart();
        }
        foreach (Feature f in Plugin.Features)
        {
            if (!f.Installed) continue;
            try { f.Tick(); }
            catch (Exception e) { Plugin.Log.LogError($"{f.Name}: {e.Message}"); }
        }
        Plugin.Dev.Update();
    }
}
