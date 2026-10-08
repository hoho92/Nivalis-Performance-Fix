using System;
using Il2CppInterop.Runtime;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// A few times per in-game day EconomyManager.StepSimulation runs, in one frame, StepStockSimulation for every item
/// of every vendor; each calls VendorItem.GetDetailedStock. That method first runs a consistency check whose result
/// is thrown away: it filters the vendor's WHOLE container with LINQ (Where over every stack), compares it with the
/// container's per-type list (Count twice, then Except + Any both ways), and only then sums the per-type list, which
/// is the value it returns. The check walks all stacks for every item (items x stacks) and has no side effect: in
/// Metro Hub it made one 98 ms frame (67 of the 71 samples in GetDetailedStock, PIX 2026-10-07, route benchmark).
/// We jump over the check, straight to the per-type sum: same result, the hitch goes away. Shops reading the stock
/// get the same speed-up. The jump is written while the feature is active and removed when it is switched off.
/// </summary>
internal sealed unsafe class EconomyStockCheck : Feature
{
    public override string Name => "Vendor stock without the unused check";
    protected override string Section => "Economy";
    protected override string Description =>
        "Vendor stock updates skip a check whose result the game never uses (removes a hitch of up to ~100 ms a few times per in-game day).";

    // locals zeroed, then: mov rax,[rdi+10h]; test rax,rax; jz ...   (the check starts at the mov)
    private const string SiteSignature =
        "45 33 E4 4C 89 A4 24 A0 00 00 00 4C 89 A4 24 A8 00 00 00 0F 57 C0 33 C0 0F 11 44 24 48 48 89 44 24 58 " +
        "4C 89 A4 24 90 00 00 00 48 8B 47 10 48 85 C0 0F 84";
    private const int SiteOffset = 42; // the mov rax,[rdi+10h] inside the signature
    // the per-type sum starts with the same load, right after the check's last call (Any): call rel32; mov rax,[rdi+10h]
    private static readonly byte[] Load = { 0x48, 0x8B, 0x47, 0x10, 0x48, 0x85, 0xC0, 0x0F, 0x84 };
    private const int MaxFunctionSize = 0x800;

    private byte* site;
    private byte[] original, jump;
    private bool patched;

    protected override string TryInstall()
    {
        byte* method = NativeCode.MethodPointer(Il2CppClassPointerStore<Nivalis.Economy.VendorItem>.NativeClassPtr, "GetDetailedStock", 0);
        if (method == null) return "VendorItem.GetDetailedStock not found";

        var hits = NativeCode.Scan(method, MaxFunctionSize, NativeCode.Pattern(SiteSignature));
        if (hits.Count != 1) return $"code signature found {hits.Count} times (game update?)";
        site = (byte*)hits[0] + SiteOffset;

        // the target: the next identical load preceded by a call (end of the check)
        byte* target = null;
        int checkCalls = 0;
        for (byte* p = site + Load.Length; p < method + MaxFunctionSize - Load.Length; p++)
        {
            if (*p == 0xE8) checkCalls++;
            if (!Matches(p, Load) || p[-5] != 0xE8) continue;
            target = p;
            break;
        }
        if (target == null) return "end of the check not found (game update?)";
        // the check: the container filter, two counts, then Except + Any twice = at least 8 calls in between
        if (checkCalls < 8) return $"unexpected code between site and target ({checkCalls} calls, game update?)";

        original = new byte[7];
        for (int i = 0; i < original.Length; i++) original[i] = site[i];
        int rel = NativeCode.Rel32(site + 5, target);
        jump = new byte[] { 0xE9, (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16), (byte)(rel >> 24), 0x90, 0x90 };
        Plugin.Log.LogDebug($"{Name}: jump at {NativeCode.Rva(site)} -> {NativeCode.Rva(target)} ({checkCalls} calls skipped)");
        return null;
    }

    private static bool Matches(byte* p, byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            if (p[i] != bytes[i]) return false;
        return true;
    }

    /// <summary>Writes / removes the jump when the feature is switched on / off (config, F8, benchmarks).</summary>
    public override void Tick()
    {
        if (Active == patched) return;
        NativeCode.WriteCode(site, Active ? jump : original);
        patched = Active;
    }

    protected override void SwitchedOff()
    {
        if (!patched) return;
        NativeCode.WriteCode(site, original);
        patched = false;
    }
}
