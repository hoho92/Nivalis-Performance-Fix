using BepInEx.Configuration;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Unity's incremental garbage collector (Boehm) ends every cycle with one unbounded ~15-20 ms stop (mark +
/// finish), felt as a hitch every ~4 s in busy scenes. A cycle starts after allocating
///   min_bytes_allocd = (2 * composite_in_use + atomic_in_use / 4 + roots + stack) / GC_free_space_divisor
/// (halved in incremental mode). GC_free_space_divisor is a plain global (vanilla 3); we find it through the
/// `div qword ptr [rip+x]` in min_bytes_allocd. Divisor 1 = cycles ~4x less often (hitch every ~18 s), same hitch
/// size, at the cost of a larger managed heap.
/// </summary>
internal sealed unsafe class GcFrequency : Feature
{
    public override string Name => "Garbage collector frequency";
    protected override string Section => "GarbageCollector";
    protected override string Description =>
        "Run the garbage collector less often: fewer GC hitches, slightly more memory used.";

    private ConfigEntry<int> divisor;

    //   shr rcx,2; add rcx,[rip+?]; lea rax,[rcx+rdx*2]; xor edx,edx; div qword [rip+DIVISOR]; mov rcx,rax; mov r8,rax; shr rcx,1
    private const string Signature =
        "48 C1 E9 02 48 03 0D ?? ?? ?? ?? 48 8D 04 51 33 D2 48 F7 35 ?? ?? ?? ?? 48 8B C8 4C 8B C0 48 D1 E9";
    private const int DispOffset = 20, NextInsn = 24;

    private ulong* global;
    private ulong vanilla;

    protected override void BindSettings(ConfigFile config)
    {
        divisor = config.Bind(Section, "FreeSpaceDivisor", 1,
            new ConfigDescription("GC_free_space_divisor (game default 3). Lower = less frequent GC, more memory.",
                new AcceptableValueRange<int>(1, 3)));
    }

    protected override string TryInstall()
    {
        var hits = NativeCode.ScanGameAssembly(NativeCode.Pattern(Signature));
        if (hits.Count != 1) return $"code signature found {hits.Count} times (game update?)";
        byte* hit = (byte*)hits[0];
        ulong* d = (ulong*)(hit + NextInsn + *(int*)(hit + DispOffset));
        if (*d < 1 || *d > 100) return $"implausible divisor value {*d}";
        global = d; vanilla = *d;
        Tick();
        return null;
    }

    public override void Tick()
    {
        if (global == null) return;
        ulong want = Active ? (ulong)divisor.Value : vanilla;
        if (want >= 1 && *global != want) *global = want;
    }
}
