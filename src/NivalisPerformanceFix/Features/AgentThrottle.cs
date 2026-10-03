using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using NivalisPerformanceFix.Native;
using Nivalis.GhostSystem.Ai;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// AgentGhostSimulator.UpdateAgentActions runs, for every simulated agent and every frame, EnsureInitialized ->
/// SelectNewAction: a scan of the agent's schedule for an action that should interrupt the current one (or a new
/// attempt for agents without an action). We redirect that one `call EnsureInitialized` to a stub that lets each
/// agent through only every Interval frames (staggered by object address), and always when the agent was just
/// loaded. Ticking the current action is untouched. Measured: +2.5% FPS.
/// </summary>
internal sealed unsafe class AgentThrottle : Feature
{
    public override string Name => "Agent schedule throttle";
    protected override string Section => "Agents";
    protected override string Description =>
        "Re-check the schedule of simulated NPCs every few frames instead of every frame.";

    private ConfigEntry<int> interval;

    // state block: +0 int frame counter, +4 byte active, +8 int mask (interval - 1); stub at +0x40
    private byte* state;

    protected override void BindSettings(ConfigFile config)
    {
        interval = config.Bind(Section, "Interval", 8,
            new ConfigDescription("Frames between two schedule checks of the same NPC.",
                new AcceptableValueList<int>(1, 2, 4, 8, 16, 32)));
    }

    protected override string TryInstall()
    {
        IntPtr sim = Il2CppClassPointerStore<AgentGhostSimulator>.NativeClassPtr;
        IntPtr agent = Il2CppClassPointerStore<AgentGhost>.NativeClassPtr;
        byte* update = NativeCode.MethodPointer(sim, "UpdateAgentActions", 1);
        byte* ensure = NativeCode.MethodPointer(sim, "EnsureInitialized", 1);
        IntPtr flagField = IL2CPP.GetIl2CppField(agent, "WasLoadedButNotInitialized");
        if (update == null || ensure == null || flagField == IntPtr.Zero) return "agent simulator methods not found";
        int flagOff = (int)IL2CPP.il2cpp_field_get_offset(flagField);

        byte* site = null; int found = 0;
        for (int i = 0; i < 0x400; i++)
            if (update[i] == 0xE8 && NativeCode.Rel32Target(update + i) == ensure) { site = update + i; found++; }
        if (found != 1) return $"expected 1 call to EnsureInitialized, found {found}";

        state = NativeCode.AllocNear(site + 5, executable: true);
        if (state == null) return "no memory near GameAssembly";

        // rcx = simulator, rdx = agent
        var a = new Asm();
        a.Emit(0x80, 0xBA).Imm32(flagOff).Emit(0x00)                // cmp byte [rdx+flagOff], 0
         .Jump(0x75, "go")                                          // jne go (just loaded: always initialize)
         .Emit(0x49, 0xB9).Imm64((ulong)state)                      // mov r9, state
         .Emit(0x41, 0x80, 0x79, 0x04, 0x00)                        // cmp byte [r9+4], 0
         .Jump(0x74, "go")                                          // je go (inactive)
         .Emit(0x89, 0xD0)                                          // mov eax, edx
         .Emit(0xC1, 0xE8, 0x05)                                    // shr eax, 5
         .Emit(0x41, 0x03, 0x01)                                    // add eax, [r9]
         .Emit(0x41, 0x85, 0x41, 0x08)                              // test eax, [r9+8]
         .Jump(0x74, "go")                                          // jz go (this agent's turn)
         .Emit(0xC3)                                                // ret (skip)
         .Label("go")
         .Emit(0x48, 0xB8).Imm64((ulong)ensure)                     // mov rax, EnsureInitialized
         .Emit(0xFF, 0xE0);                                         // jmp rax
        byte* stub = state + 0x40;
        NativeCode.WriteStub(stub, a.Build());
        Tick();
        NativeCode.RedirectCall(site, stub);
        Plugin.Log.LogDebug($"{Name}: call at {NativeCode.Rva(site)} redirected");
        return null;
    }

    public override void Tick()
    {
        if (state == null) return;
        (*(int*)state)++;
        *(state + 4) = Active ? (byte)1 : (byte)0;
        *(int*)(state + 8) = Math.Max(1, interval.Value) - 1;
    }
}
