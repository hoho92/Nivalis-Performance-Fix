using System;
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
    private IntPtr instancesField;

    // HashSet<T> (Unity 2020 mscorlib), reference T: _slots @0x18, _lastIndex @0x24.
    // Slot = { int hashCode; int next; T value; } (16 bytes), free slots have hashCode < 0. Il2CppArray data @0x20.
    private const int HashSetSlots = 0x18, HashSetLastIndex = 0x24;
    private const int ArrayLength = 0x18, ArrayData = 0x20, SlotSize = 16, SlotValue = 8;

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

        instancesField = IL2CPP.GetIl2CppField(character, "instances");
        offDeltaTime = Offset(character, "<AnimatorDeltaTime>k__BackingField");
        offUpdateStep = Offset(character, "<AnimatorUpdateStep>k__BackingField");
        offSqrDistance = Offset(baseCharacter, "<SqrVisibleDistance>k__BackingField");
        offBecameVisible = Offset(baseCharacter, "becameVisible");
        if (instancesField == IntPtr.Zero || offDeltaTime <= 0 || offUpdateStep <= 0 || offSqrDistance <= 0 || offBecameVisible <= 0)
            return "Character fields changed (game update?)";

        Plugin.Harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.LateUpdateAll)),
            postfix: new HarmonyMethod(typeof(AnimationLod), nameof(LateUpdateAllPostfix)));
        return null;
    }

    private static int Offset(IntPtr klass, string field)
    {
        IntPtr f = IL2CPP.GetIl2CppField(klass, field);
        return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f);
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
        IntPtr set;
        IL2CPP.il2cpp_field_static_get_value(instancesField, &set);
        if (set == IntPtr.Zero) return (0, 0);
        byte* slots = *(byte**)((byte*)set + HashSetSlots);
        int last = *(int*)((byte*)set + HashSetLastIndex);
        if (slots == null || last <= 0 || (ulong)last > *(ulong*)(slots + ArrayLength)) return (0, 0);
        int total = 0, visible = 0;
        byte* slot = slots + ArrayData;
        for (int i = 0; i < last; i++, slot += SlotSize)
        {
            if (*(int*)slot < 0) continue;
            byte* c = *(byte**)(slot + SlotValue);
            if (c == null) continue;
            total++;
            if (*(c + offBecameVisible) != 0) visible++;
        }
        return (total, visible);
    }

    private void Apply()
    {
        IntPtr set;
        IL2CPP.il2cpp_field_static_get_value(instancesField, &set);
        if (set == IntPtr.Zero) return;

        byte* slots = *(byte**)((byte*)set + HashSetSlots);
        int last = *(int*)((byte*)set + HashSetLastIndex);
        if (slots == null || last <= 0) return;
        if ((ulong)last > *(ulong*)(slots + ArrayLength)) return; // layout mismatch: do nothing

        float near = nearDistance.Value;
        float range = Math.Max(0.01f, farDistance.Value - near);
        int maxVisible = maxVisibleStep.Value, offscreen = offscreenStep.Value;
        bool useJitter = jitter.Value;

        byte* slot = slots + ArrayData;
        for (int i = 0; i < last; i++, slot += SlotSize)
        {
            if (*(int*)slot < 0) continue;
            byte* c = *(byte**)(slot + SlotValue);
            if (c == null) continue;

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
