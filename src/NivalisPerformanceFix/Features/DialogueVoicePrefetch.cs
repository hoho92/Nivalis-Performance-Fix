using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Nivalis.Dialogue;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Each dialogue line loads its voice clip on the main thread when it is shown (DialogueUI.DisplayLine →
/// GetLineAudioClip → Resources.Load): the clip is decoded entirely right then (FMOD Vorbis), a 12-40 ms freeze at
/// every line (PIX, 2026-10-07). While a line is shown, the voices of the lines that can follow it (the next line,
/// the target of each choice, one step further) are now loaded in the background (Resources.LoadAsync, decoded on
/// the loading thread); when the game asks for one, Resources.Load finds it already loaded.
/// </summary>
internal sealed class DialogueVoicePrefetch : Feature
{
    public override string Name => "Dialogue voices loaded ahead";
    protected override string Section => "DialogueVoices";
    protected override string Description =>
        "Load the voice of the next dialogue lines in the background while the current line is shown (no freeze at each line).";

    private const int MaxPerLine = 8;  // clips started per shown line
    private const int MaxKept = 24;    // requests kept (each keeps its clip loaded)

    private static DialogueVoicePrefetch self;
    private static readonly Dictionary<string, ResourceRequest> requests = new();
    private static readonly Queue<string> order = new();
    private static readonly List<IDialogue> next = new();
    private static bool failed;

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(DialogueUI), "DisplayLine"),
            postfix: new HarmonyMethod(typeof(DialogueVoicePrefetch), nameof(DisplayLinePostfix)));
        return null;
    }

    private static void DisplayLinePostfix(DialogueStepDisplayData currentDialogueNode)
    {
        if (self == null || !self.Active || failed || currentDialogueNode is null) return;
        try
        {
            next.Clear();
            AddNext(currentDialogueNode.Dialogue);
            var choices = currentDialogueNode.Choices;
            if (choices is not null)
                for (int i = 0; i < choices.Count; i++)
                    if (choices[i] is { Enabled: true } c && c.ToDialogue is { } to) next.Add(to);
            int count = next.Count; // one step further: a choice often leads to the player's line, then the answer
            for (int i = 0; i < count; i++) AddNext(next[i]);

            int started = 0;
            foreach (IDialogue d in next)
            {
                if (started >= MaxPerLine) break;
                if (Prefetch(d.VOFileName)) started++;
            }
        }
        catch (Exception e)
        {
            failed = true; // the dialogue types changed: the game loads its voices as before
            Plugin.Log.LogWarning($"{self.Name}: stopped ({e.Message})");
        }
    }

    private static void AddNext(IDialogue d)
    {
        if (d?.TryCast<INextDialogueHolder>()?.NextDialogue is { } n) next.Add(n);
    }

    private static bool Prefetch(string path)
    {
        if (string.IsNullOrEmpty(path) || requests.ContainsKey(path)) return false;
        ResourceRequest r = Resources.LoadAsync(path, Il2CppType.Of<AudioClip>());
        if (r is null) return false;
        requests[path] = r;
        order.Enqueue(path);
        while (order.Count > MaxKept) requests.Remove(order.Dequeue());
        return true;
    }
}
