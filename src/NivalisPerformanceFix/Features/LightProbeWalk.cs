using System;
using System.Diagnostics;
using System.Linq;
using BepInEx.Configuration;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Unity (UnityPlayer.dll, LightProbes::LightProbeData::GetLightProbeInterpolationWeights) finds the light probe
/// tetrahedron of every visible probe-lit renderer each frame by walking from the tetrahedron found last frame,
/// up to "number of tetrahedra" steps. Outside the probe volume (outer cells) the walk can cycle without ever
/// converging, so a few renderers cost tens of thousands of expensive outer-cell steps per frame: in 13_Stacks
/// (10646 probes, ~5000 probe-lit renderers in view) that was ~50% of every job thread, 40 FPS instead of 150.
/// We cap the walk at MaxSteps per frame: a normal renderer needs 1-3 steps, and the walk resumes from where it
/// stopped next frame (Unity stores the last tetrahedron), so lighting stays the same.
/// </summary>
internal sealed unsafe class LightProbeWalk : Feature
{
    public override string Name => "Light probe walk limit";
    protected override string Section => "LightProbes";
    protected override string Description =>
        "Limit Unity's per-frame light probe search, which can loop for thousands of steps in some areas (13_Stacks).";

    private ConfigEntry<int> maxSteps;
    private byte* site;          // "cmp ebp, r9d" (41 3B E9) closing the walk loop
    private bool patched;

    // ... mov eax,[rbx+18h]; jmp; mov eax,[rbx+1Ch]; inc ebp; mov [r14],eax; cmp ebp,r9d; jl loop
    private static readonly byte?[] Signature = NativeCode.Pattern("8B 43 18 EB 03 8B 43 1C FF C5 41 89 06 41 3B E9 0F 8C");
    private const int SiteOffset = 13;
    private static readonly byte[] Original = { 0x41, 0x3B, 0xE9 };

    protected override void BindSettings(ConfigFile config)
    {
        maxSteps = config.Bind(Section, "MaxSteps", 100,
            new ConfigDescription("Maximum light probe search steps per renderer and frame (Unity: number of tetrahedra).",
                new AcceptableValueRange<int>(8, 127)));
    }

    protected override string TryInstall()
    {
        ProcessModule up = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => m.ModuleName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
        if (up == null) return "UnityPlayer.dll not found";
        var hits = NativeCode.Scan((byte*)up.BaseAddress + 0x1000, up.ModuleMemorySize - 0x1000, Signature);
        if (hits.Count != 1) return $"code not found ({hits.Count} matches)";
        site = (byte*)hits[0] + SiteOffset;
        return null;
    }

    public override void Tick()
    {
        bool on = Active;
        if (on == patched) return;
        // "cmp ebp, imm8" has the same length as "cmp ebp, r9d"
        NativeCode.WriteCode(site, on ? new byte[] { 0x83, 0xFD, (byte)maxSteps.Value } : Original);
        patched = on;
    }
}
