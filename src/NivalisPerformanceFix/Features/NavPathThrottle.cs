using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using NivalisPerformanceFix.Native;
using UnityEngine.AI;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// MoveAction's per-frame loop (MoveAction.&lt;Perform&gt;d__21.MoveNext) runs for every walking character and does
///   corners = agent.path.GetCornersNonAlloc(pooledArray)
/// only to read the corner COUNT. NavMeshAgent.path allocates a new NavMeshPath and copies the whole path every
/// call. We replace those two calls with a stub that re-reads each character's path every Interval frames and
/// otherwise returns the cached count, kept in the state machine's padding bytes (+0x44..+0x47, between the
/// corners int at +0x40 and the awaiter at +0x48): low byte = count+1 (0 = none), upper 24 bits = frame.
/// The stub reads the path with NavMeshAgent.CopyPathTo into one shared NavMeshPath instead of the getter, which
/// allocated a new NavMeshPath (a native path + a finalizer) per read: ~2,500 garbage objects per second in Metro Hub.
/// Measured: +2.3% FPS, 1% lows +22%.
/// </summary>
internal sealed unsafe class NavPathThrottle : Feature
{
    public override string Name => "Navigation path throttle";
    protected override string Section => "Navigation";
    protected override string Description =>
        "Walking NPCs re-read their navigation path every few frames instead of every frame.";

    private ConfigEntry<int> interval;

    //   mov rcx,[rcx+18h]; test rcx,rcx; je X; xor edx,edx; call get_path; test rax,rax; je X;
    //   xor r8d,r8d; mov rdx,[rsp+0A0h]; mov rcx,rax; call GetCornersNonAlloc; mov esi,eax
    private const string Signature =
        "48 8B 49 18 48 85 C9 0F 84 ?? ?? ?? ?? 33 D2 E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? " +
        "45 33 C0 48 8B 94 24 A0 00 00 00 48 8B C8 E8 ?? ?? ?? ?? 8B F0";
    private const int PatchStart = 13, PatchEnd = 48, GetPathCall = 15, CornersCall = 43;

    // state block: +0 int frame, +4 byte active, +8 int interval; +0x10 RUNTIME_FUNCTION, +0x20 UNWIND_INFO; code at +0x40
    private byte* state;
    private NavMeshPath sharedPath; // kept alive by this reference; the stub uses its native object

    protected override void BindSettings(ConfigFile config)
    {
        interval = config.Bind(Section, "Interval", 4,
            new ConfigDescription("Frames between two path reads of the same walking NPC (1 = every frame).",
                new AcceptableValueRange<int>(1, 30)));
    }

    protected override string TryInstall()
    {
        byte* getPath = AiMethod("NavMeshAgent", "get_path", 0);
        byte* copyPath = AiMethod("NavMeshAgent", "CopyPathTo", 1);
        byte* getCorners = AiMethod("NavMeshPath", "GetCornersNonAlloc", 1);
        if (getPath == null || copyPath == null || getCorners == null) return "NavMesh methods not found";

        var hits = NativeCode.ScanGameAssembly(NativeCode.Pattern(Signature), p =>
            NativeCode.Rel32Target((byte*)p + GetPathCall) == getPath &&
            NativeCode.Rel32Target((byte*)p + CornersCall) == getCorners);
        if (hits.Count != 1) return $"code signature found {hits.Count} times (game update?)";
        byte* site = (byte*)hits[0];

        // sanity: the count is then compared with the state machine's corners field: cmp esi,[rdi+40h]
        bool cmpFound = false;
        for (int i = PatchEnd; i < PatchEnd + 0x100 && !cmpFound; i++)
            cmpFound = site[i] == 0x3B && site[i + 1] == 0x77 && site[i + 2] == 0x40;
        if (!cmpFound) return "unexpected code after the path read (game update?)";

        state = NativeCode.AllocNear(site + PatchStart + 16, executable: true);
        if (state == null) return "no memory near GameAssembly";
        sharedPath = new NavMeshPath();
        ulong path = (ulong)sharedPath.Pointer;

        // stub(rcx = agent, rdx = corners array, r8 = state machine) -> eax = corner count
        var a = new Asm();
        a.Emit(0x49, 0xB9).Imm64((ulong)state)              // mov r9, state
         .Emit(0x41, 0x80, 0x79, 0x04, 0x00)                // cmp byte [r9+4], 0
         .Jump(0x74, "query")                               // je query (inactive)
         .Emit(0x41, 0x8B, 0x40, 0x44)                      // mov eax, [r8+44h]
         .Emit(0x84, 0xC0)                                  // test al, al
         .Jump(0x74, "query")                               // je query (nothing cached)
         .Emit(0x41, 0x89, 0xC2)                            // mov r10d, eax
         .Emit(0x41, 0xC1, 0xEA, 0x08)                      // shr r10d, 8          (cached frame)
         .Emit(0x45, 0x8B, 0x19)                            // mov r11d, [r9]       (current frame)
         .Emit(0x45, 0x29, 0xD3)                            // sub r11d, r10d
         .Emit(0x41, 0x81, 0xE3).Imm32(0xFFFFFF)            // and r11d, 0FFFFFFh   (age)
         .Emit(0x45, 0x3B, 0x59, 0x08)                      // cmp r11d, [r9+8]
         .Jump(0x73, "query")                               // jae query (too old)
         .Emit(0x0F, 0xB6, 0xC0)                            // movzx eax, al
         .Emit(0xFF, 0xC8)                                  // dec eax
         .Emit(0xC3);                                       // ret (cached count)
        int query = a.Position;
        a.Label("query")
         .Emit(0x53);                                       // push rbx
        int afterPush = a.Position - query;
        a.Emit(0x48, 0x83, 0xEC, 0x30);                     // sub rsp, 30h
        int prolog = a.Position - query;
        a.Emit(0x4C, 0x89, 0xC3)                            // mov rbx, r8
         .Emit(0x48, 0x89, 0x54, 0x24, 0x28)                // mov [rsp+28h], rdx
         .Emit(0x48, 0xBA).Imm64(path)                      // mov rdx, sharedPath
         .Emit(0x45, 0x31, 0xC0)                            // xor r8d, r8d
         .Emit(0x48, 0xB8).Imm64((ulong)copyPath)           // mov rax, CopyPathTo
         .Emit(0xFF, 0xD0)                                  // call rax (agent.CopyPathTo(sharedPath))
         .Emit(0x48, 0xB9).Imm64(path)                      // mov rcx, sharedPath
         .Emit(0x48, 0x8B, 0x54, 0x24, 0x28)                // mov rdx, [rsp+28h]
         .Emit(0x45, 0x31, 0xC0)                            // xor r8d, r8d
         .Emit(0x48, 0xB8).Imm64((ulong)getCorners)         // mov rax, GetCornersNonAlloc
         .Emit(0xFF, 0xD0)                                  // call rax
         .Emit(0x3D).Imm32(0xFE)                            // cmp eax, 0FEh
         .Jump(0x77, "nocache")                             // ja nocache
         .Emit(0x49, 0xB9).Imm64((ulong)state)              // mov r9, state
         .Emit(0x45, 0x8B, 0x11)                            // mov r10d, [r9]
         .Emit(0x41, 0xC1, 0xE2, 0x08)                      // shl r10d, 8
         .Emit(0x8D, 0x48, 0x01)                            // lea ecx, [rax+1]
         .Emit(0x41, 0x09, 0xCA)                            // or r10d, ecx
         .Emit(0x44, 0x89, 0x53, 0x44)                      // mov [rbx+44h], r10d
         .Jump(0xEB, "done")                                // jmp done
         .Label("nocache")
         .Emit(0xC7, 0x43, 0x44).Imm32(0)                   // mov dword [rbx+44h], 0
         .Label("done")
         .Emit(0x48, 0x83, 0xC4, 0x30)                      // add rsp, 30h
         .Emit(0x5B)                                        // pop rbx
         .Emit(0xC3);                                       // ret
        byte[] code = a.Build();

        byte* stub = state + 0x40;
        NativeCode.WriteStub(stub, code);
        // CopyPathTo / GetCornersNonAlloc may throw: give the framed part unwind info
        if (!NativeCode.RegisterPushRbxFrame(state, state + 0x10, state + 0x20, 0x40 + query, 0x40 + code.Length,
                prolog, afterPush, 0x30))
            Plugin.Log.LogWarning($"{Name}: could not register unwind info");
        Tick();

        // mov rdx,[rsp+0A0h]; mov r8,rdi; call stub; nop...
        var patch = new List<byte> { 0x48, 0x8B, 0x94, 0x24, 0xA0, 0x00, 0x00, 0x00, 0x49, 0x89, 0xF8, 0xE8 };
        patch.AddRange(BitConverter.GetBytes(NativeCode.Rel32(site + PatchStart + 16, stub)));
        while (patch.Count < PatchEnd - PatchStart) patch.Add(0x90);
        NativeCode.WriteCode(site + PatchStart, patch.ToArray());
        Plugin.Log.LogDebug($"{Name}: patched at {NativeCode.Rva(site + PatchStart)}");
        return null;
    }

    private static byte* AiMethod(string klass, string name, int args) =>
        NativeCode.MethodPointer(IL2CPP.GetIl2CppClass("UnityEngine.AIModule.dll", "UnityEngine.AI", klass), name, args);

    public override void Tick()
    {
        if (state == null) return;
        *(int*)state = (*(int*)state + 1) & 0xFFFFFF;
        *(state + 4) = Active ? (byte)1 : (byte)0;
        *(int*)(state + 8) = Math.Max(1, interval.Value);
    }
}
