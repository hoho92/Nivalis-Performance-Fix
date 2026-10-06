using System;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The game crashes on every quit (access violation in UnityPlayer.dll, also without any mod): Unity's runtime
/// cleanup destroys the job system, then AsyncUploadManager's destructor completes its last upload fence:
///   RuntimeCleanup -> AsyncUploadManager::StaticDestroy -> ~AsyncUploadManager -> CompleteFenceInternal
///   CompleteFenceInternal: queue = GetJobQueue(); queue->WaitForJobGroupID(fence)   (queue is now null)
/// and WaitForJobGroupID reads [null+0xF2]. The `call WaitForJobGroupID` of CompleteFenceInternal now goes through
/// a stub that skips the wait when the queue is gone (only possible during shutdown, nothing is left to wait for)
/// and jumps to the original function otherwise. CompleteFenceInternal then clears the fence as usual.
/// </summary>
internal sealed unsafe class ExitCrashFix : Feature
{
    public override string Name => "Crash on quit fix";
    protected override string Section => "ExitCrash";
    protected override string Description =>
        "Fix the game crashing when you quit (a Unity shutdown-order bug, also present without mods).";

    // CompleteFenceInternal(JobFence&, WorkStealMode) of Unity 2020.3.44 (UnityPlayer.dll):
    //   mov [rsp+8],rbx; push rdi; sub rsp,30h; mov ebx,edx; mov rdi,rcx; call GetJobQueue;
    //   movups xmm0,[rdi]; mov r8d,ebx; lea rdx,[rsp+20h]; mov rcx,rax; movaps [rsp+20h],xmm0; call WaitForJobGroupID
    private const string Signature =
        "48 89 5C 24 08 57 48 83 EC 30 8B DA 48 8B F9 E8 ?? ?? ?? ?? 0F 10 07 44 8B C3 48 8D 54 24 20 " +
        "48 8B C8 0F 29 44 24 20 E8 ?? ?? ?? ??";
    private const int CallOffset = 39;

    private byte* site, stub;
    private byte[] original, patched;
    private bool applied;

    protected override string TryInstall()
    {
        byte* unity = NativeCode.Module("UnityPlayer.dll");
        if (unity == null) return "UnityPlayer.dll not found";
        var hits = NativeCode.Scan(unity + 0x1000, NativeCode.ModuleSize(unity) - 0x1000, NativeCode.Pattern(Signature));
        if (hits.Count != 1) return $"job fence code not recognised ({hits.Count} matches, Unity update?)";
        site = (byte*)hits[0] + CallOffset;
        byte* wait = NativeCode.Rel32Target(site);

        stub = NativeCode.AllocNear(site, executable: true, module: unity);
        if (stub == null) return "no memory near UnityPlayer.dll";
        // test rcx,rcx; je skip; jmp [rip+0] -> WaitForJobGroupID; skip: ret
        // (a leaf without stack frame: no unwind info needed)
        byte[] code = new Asm()
            .Emit(0x48, 0x85, 0xC9)          // test rcx, rcx
            .Jump(0x74, "skip")              // je skip
            .Emit(0xFF, 0x25, 0, 0, 0, 0)    // jmp [rip+0]
            .Imm64((ulong)wait)
            .Label("skip")
            .Emit(0xC3)                      // ret
            .Build();
        NativeCode.WriteStub(stub, code);

        original = new byte[5];
        for (int i = 0; i < 5; i++) original[i] = site[i];
        patched = new byte[5];
        patched[0] = 0xE8;
        BitConverter.GetBytes(NativeCode.Rel32(site + 5, stub)).CopyTo(patched, 1);
        Tick();
        return null;
    }

    public override void Tick()
    {
        if (applied == Active) return;
        applied = Active;
        NativeCode.WriteCode(site, applied ? patched : original);
    }
}
