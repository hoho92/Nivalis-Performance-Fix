using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Updates the animation of far and off-screen characters less often.
///
/// Vanilla Character logic (decompiled):
///   DoUpdate:     AnimatorDeltaTime += dt;
///                 if (--AnimatorUpdateStep &lt;= 0 &amp;&amp; (CameraVisible || action.UpdateStep &lt; 100))
///                     animator.Update(AnimatorDeltaTime);
///   DoLateUpdate: if (AnimatorUpdateStep &lt;= 0) {
///                     AnimatorUpdateStep = CameraVisible ? RoundToInt(1 + 4 * clamp01((dist - 5) / 70)^2)  // 1..5
///                                                        : action.UpdateStep;                              // 1, or 100 when idle
///                     AnimatorDeltaTime = 0; ... }
/// Once per frame, after Character.LateUpdateAll, every character that was just re-armed
/// (AnimatorDeltaTime == 0) gets a larger AnimatorUpdateStep. We only ever raise it, never lower it; the
/// accumulated delta time keeps animations at the right speed.
/// Measured: +7% FPS, 1% lows +28% (busy market, vs. the milder first profile; ~+14% vs. vanilla).
/// </summary>
internal sealed unsafe class AnimationLod : Feature
{
    public static AnimationLod Instance { get; private set; }

    public override string Name => "Animation LOD";
    protected override string Section => "Animation";
    protected override string Description =>
        "Update the animation of far and off-screen characters less often (main CPU cost of the game).";

    private ConfigEntry<float> nearDistance, farDistance;
    private ConfigEntry<int> maxVisibleStep, offscreenStep;
    private ConfigEntry<bool> jitter;

    // Il2Cpp field offsets (object header included), resolved at install.
    private int offDeltaTime, offUpdateStep, offSqrDistance, offBecameVisible;
    private readonly List<IntPtr> characters = new(1024);

    private uint rng = 0x9E3779B9;

    public AnimationLod() => Instance = this;

    protected override void BindSettings(ConfigFile config)
    {
        nearDistance = config.Bind(Section, "NearDistance", 6f,
            new ConfigDescription("On-screen characters closer than this (metres) keep the normal update rate.",
                new AcceptableValueRange<float>(0f, 50f)));
        farDistance = config.Bind(Section, "FarDistance", 25f,
            new ConfigDescription("On-screen characters at or beyond this distance (metres) use MaxVisibleStep.",
                new AcceptableValueRange<float>(1f, 100f)));
        maxVisibleStep = config.Bind(Section, "MaxVisibleStep", 6,
            new ConfigDescription("Frames between two animation updates of a far on-screen character.",
                new AcceptableValueRange<int>(1, 10)));
        offscreenStep = config.Bind(Section, "OffscreenStep", 8,
            new ConfigDescription("Frames between two animation updates of an off-screen character.",
                new AcceptableValueRange<int>(1, 30)));
        jitter = config.Bind(Section, "Jitter", true,
            "Vary each character's interval by +-1 frame so updates spread evenly over frames (smoother frame times).");
    }

    protected override string TryInstall()
    {
        IntPtr character = Il2CppClassPointerStore<Character>.NativeClassPtr;
        IntPtr baseCharacter = Il2CppClassPointerStore<BaseCharacter>.NativeClassPtr;
        if (character == IntPtr.Zero || baseCharacter == IntPtr.Zero) return "Character class not found";

        offDeltaTime = CharacterSet.Offset(character, "<AnimatorDeltaTime>k__BackingField");
        offUpdateStep = CharacterSet.Offset(character, "<AnimatorUpdateStep>k__BackingField");
        offSqrDistance = CharacterSet.Offset(baseCharacter, "<SqrVisibleDistance>k__BackingField");
        offBecameVisible = CharacterSet.Offset(baseCharacter, "becameVisible");
        if (!CharacterSet.Resolve() || offDeltaTime <= 0 || offUpdateStep <= 0 || offSqrDistance <= 0 || offBecameVisible <= 0)
            return "Character fields changed (game update?)";

        Plugin.Harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.LateUpdateAll)),
            postfix: new HarmonyMethod(typeof(AnimationLod), nameof(LateUpdateAllPostfix)));
        return null;
    }

    private static void LateUpdateAllPostfix()
    {
        AnimationLod self = Instance;
        if (self == null || !self.Active) return;
        try
        {
            self.Apply();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{self.Name} disabled until restart after an error: {e.Message}");
            self.DisableForSession("error: " + e.Message);
        }
    }

    /// <summary>Number of live characters and of those on screen (for the developer play log).</summary>
    public (int total, int visible) CountCharacters()
    {
        if (!Installed) return (-1, -1);
        CharacterSet.Collect(characters);
        int visible = 0;
        foreach (IntPtr c in characters)
            if (*((byte*)c + offBecameVisible) != 0) visible++;
        return (characters.Count, visible);
    }

    private void Apply()
    {
        CharacterSet.Collect(characters);
        float near = nearDistance.Value;
        float range = Math.Max(0.01f, farDistance.Value - near);
        int maxVisible = maxVisibleStep.Value, offscreen = offscreenStep.Value;
        bool useJitter = jitter.Value;

        foreach (IntPtr ptr in characters)
        {
            byte* c = (byte*)ptr;

            // only characters DoLateUpdate re-armed this frame: step >= 1 and delta time just reset
            int step = *(int*)(c + offUpdateStep);
            if (step < 1 || step >= 100 || *(float*)(c + offDeltaTime) != 0f) continue;

            int target;
            if (*(c + offBecameVisible) == 0)
            {
                target = offscreen;
            }
            else
            {
                float t = (MathF.Sqrt(*(float*)(c + offSqrDistance)) - near) / range;
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                target = 1 + (int)MathF.Round((maxVisible - 1) * t);
            }

            // without jitter, characters updated on the same frame stay in phase forever and all update together
            // every N frames (measured: worse 1% lows)
            if (target >= 3 && useJitter)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                target += (int)(rng % 3) - 1;
            }

            if (target > step) *(int*)(c + offUpdateStep) = target;
        }
    }
}
