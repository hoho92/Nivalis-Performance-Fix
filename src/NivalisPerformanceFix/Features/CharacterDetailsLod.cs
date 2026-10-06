using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CrazyMinnow.SALSA;
using Il2CppInterop.Runtime;
using Nivalis;
using RootMotion.FinalIK;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Switches off two per-character details where they cannot be seen: off-screen, or farther than a distance.
///  * Head look-at (FinalIK LookAtIK, one per character): solved every frame for every character, on screen or
///    not. The game itself stops updating the look-at weights beyond 20 m (BaseIK cutoff) but the solver keeps
///    running. Metro Hub, 2026-10-06: 15.5 ms per second of main thread.
///  * Lip-sync (SALSA QueueProcessor, one per character): blend-shape queue processed every frame. ~20 ms/s.
/// The game never enables / disables these components itself (CharacterIK.HeadIkPart only sets targets and
/// weights), so we only switch the ones we turned off back on. A 2 m hysteresis avoids flicker at the limit.
/// The per-frame loop reads raw memory and plain references only: a Unity null check (Object.op_Inequality)
/// goes through il2cpp_runtime_invoke, which boxes its bool result (2 per character per frame = ~135k garbage
/// objects per second in Metro Hub, measured with the allocation tracker). It is done only on state changes.
/// </summary>
internal sealed unsafe class CharacterDetailsLod : Feature
{
    public override string Name => "Far character details";
    protected override string Section => "CharacterDetails";
    protected override string Description =>
        "Stop the head look-at and the lip-sync of off-screen and far characters, where they cannot be seen.";

    private const float Hysteresis = 2f;
    private const int NewEntriesPerFrame = 32; // component lookups of newly seen characters, spread over frames
    private const int PruneEvery = 600;

    private ConfigEntry<bool> lookAt, lipSync;
    private ConfigEntry<float> lookAtDistance, lipSyncDistance;

    private int offSqrDistance, offBecameVisible, offIk, offLookIk;
    private readonly List<IntPtr> characters = new(1024);
    private readonly Dictionary<IntPtr, Entry> entries = new();
    private readonly HashSet<IntPtr> alive = new();
    private int frame;
    private bool applied;

    private sealed class Entry
    {
        public LookAtIK Look;
        public QueueProcessor Lip;
        public bool LookOff, LipOff;
    }

    protected override void BindSettings(ConfigFile config)
    {
        lookAt = config.Bind(Section, "LookAt", true, "Stop the head look-at of off-screen and far characters.");
        lookAtDistance = config.Bind(Section, "LookAtDistance", 20f,
            new ConfigDescription("On-screen characters farther than this (metres) stop turning their head toward targets.",
                new AcceptableValueRange<float>(5f, 100f)));
        lipSync = config.Bind(Section, "LipSync", true, "Stop the lip-sync of off-screen and far characters.");
        lipSyncDistance = config.Bind(Section, "LipSyncDistance", 25f,
            new ConfigDescription("On-screen characters farther than this (metres) stop moving their lips.",
                new AcceptableValueRange<float>(5f, 100f)));
    }

    protected override string TryInstall()
    {
        IntPtr character = Il2CppClassPointerStore<Character>.NativeClassPtr;
        IntPtr baseCharacter = Il2CppClassPointerStore<BaseCharacter>.NativeClassPtr;
        IntPtr characterIk = Il2CppClassPointerStore<CharacterIK>.NativeClassPtr;
        if (character == IntPtr.Zero || baseCharacter == IntPtr.Zero || characterIk == IntPtr.Zero)
            return "Character classes not found";
        offSqrDistance = CharacterSet.Offset(baseCharacter, "<SqrVisibleDistance>k__BackingField");
        offBecameVisible = CharacterSet.Offset(baseCharacter, "becameVisible");
        offIk = CharacterSet.Offset(character, "ik");
        offLookIk = CharacterSet.Offset(characterIk, "lookIK");
        if (!CharacterSet.Resolve() || offSqrDistance <= 0 || offBecameVisible <= 0 || offIk <= 0 || offLookIk <= 0)
            return "Character fields changed (game update?)";
        return null;
    }

    public override void Tick()
    {
        if (!Active)
        {
            if (applied) RestoreAll();
            return;
        }
        applied = true;
        try
        {
            Apply();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Name} disabled until restart after an error: {e.Message}");
            RestoreAll();
            DisableForSession("error: " + e.Message);
        }
    }

    private void Apply()
    {
        CharacterSet.Collect(characters);
        bool doLook = lookAt.Value, doLip = lipSync.Value;
        float lookFar = lookAtDistance.Value, lipFar = lipSyncDistance.Value;
        int created = 0;

        foreach (IntPtr ptr in characters)
        {
            if (!entries.TryGetValue(ptr, out Entry e))
            {
                if (created++ >= NewEntriesPerFrame) continue;
                entries[ptr] = e = Create(ptr);
            }
            byte* c = (byte*)ptr;
            bool visible = *(c + offBecameVisible) != 0;
            float sqr = *(float*)(c + offSqrDistance);

            if (e.Look is not null)
            {
                bool off = doLook && Hidden(visible, sqr, lookFar, e.LookOff);
                if (off != e.LookOff)
                {
                    if (e.Look == null) e.Look = null; // destroyed: forget it
                    else { e.Look.enabled = !off; e.LookOff = off; }
                }
            }
            if (e.Lip is not null)
            {
                bool off = doLip && Hidden(visible, sqr, lipFar, e.LipOff);
                if (off != e.LipOff)
                {
                    if (e.Lip == null) e.Lip = null;
                    else { e.Lip.enabled = !off; e.LipOff = off; }
                }
            }
        }

        if (++frame % PruneEvery == 0) Prune();
    }

    /// <summary>Off-screen, or beyond the distance (switching back on only once 2 m closer).</summary>
    private static bool Hidden(bool visible, float sqrDistance, float distance, bool currentlyOff)
    {
        if (!visible) return true;
        float limit = currentlyOff ? Math.Max(0f, distance - Hysteresis) : distance;
        return sqrDistance > limit * limit;
    }

    private Entry Create(IntPtr ptr)
    {
        var e = new Entry();
        IntPtr ik = *(IntPtr*)((byte*)ptr + offIk);
        IntPtr look = ik == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)((byte*)ik + offLookIk);
        if (look != IntPtr.Zero) e.Look = new LookAtIK(look);
        QueueProcessor lip = new Character(ptr).GetComponentInChildren<QueueProcessor>(true);
        if (lip != null) e.Lip = lip; // Unity null check once, plain reference afterwards
        return e;
    }

    /// <summary>Forgets characters that are gone, switching back on what we turned off (pooled characters come back).</summary>
    private void Prune()
    {
        alive.Clear();
        foreach (IntPtr p in characters) alive.Add(p);
        var gone = new List<IntPtr>();
        foreach (var kv in entries)
            if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (IntPtr p in gone)
        {
            Restore(entries[p]);
            entries.Remove(p);
        }
    }

    private void RestoreAll()
    {
        foreach (Entry e in entries.Values) Restore(e);
        entries.Clear();
        applied = false;
    }

    private static void Restore(Entry e)
    {
        try
        {
            if (e.LookOff && e.Look != null) e.Look.enabled = true;
            if (e.LipOff && e.Lip != null) e.Lip.enabled = true;
        }
        catch { } // destroyed with its character: nothing to restore
        e.LookOff = e.LipOff = false;
    }
}
