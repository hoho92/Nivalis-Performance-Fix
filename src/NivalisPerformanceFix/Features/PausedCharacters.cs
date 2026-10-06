using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Nivalis;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Game bug: while the game is paused (Time.deltaTime == 0), Character.LateUpdateAll returns at once, so
/// DoLateUpdate never re-arms AnimatorUpdateStep, but Character.UpdateAll keeps decrementing it. Every far
/// character on screen then gets a manual Animator.Update(0) EVERY frame for as long as the pause lasts:
/// Metro Hub pause menu, 2026-10-06: 175 ms per second of main thread, 4.4x the cost of normal play.
/// While paused we hold the step above zero, so nothing is re-evaluated; after the pause the normal cycle resumes
/// (at most one frame later). Nothing moves while paused, so nothing changes on screen.
/// </summary>
internal sealed unsafe class PausedCharacters : Feature
{
    public override string Name => "Paused characters";
    protected override string Section => "PausedCharacters";
    protected override string Description =>
        "Don't re-animate every far character each frame while the game is paused (game bug).";

    private int offUpdateStep;
    private readonly List<IntPtr> characters = new(1024);

    protected override string TryInstall()
    {
        IntPtr character = Il2CppClassPointerStore<Character>.NativeClassPtr;
        if (character == IntPtr.Zero) return "Character class not found";
        offUpdateStep = CharacterSet.Offset(character, "<AnimatorUpdateStep>k__BackingField");
        if (!CharacterSet.Resolve() || offUpdateStep <= 0) return "Character fields changed (game update?)";
        return null;
    }

    public override void Tick()
    {
        if (!Active || Direct.DeltaTime > 0f) return;
        // DoUpdate decrements once per frame and updates the animator at <= 0: keeping it at 2 whatever the
        // order of our Update and the game's UpdateAll means it never gets there.
        CharacterSet.Collect(characters);
        foreach (IntPtr c in characters)
        {
            int* step = (int*)((byte*)c + offUpdateStep);
            if (*step < 2) *step = 2;
        }
    }
}
