using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The game tests enum flags with Enum.HasFlag, which IL2CPP compiles as two boxing allocations (the value and the
/// flag) plus a reflection-based call:
///   [value -> slot V] call Box; {mov rcx,TypeInfo; lea rdx,F; mov R,rax; flag -> F} call Box;
///   test R,R; je throw; xor r8d,r8d; mov rdx,rax; mov rcx,R; call Enum.HasFlag
/// The NPC task checks (VenueTasks: CleanupTrash, ServeMeal, ... MeetsRequirements) do this every frame: ~10,000
/// garbage objects per second in Metro Hub, the largest allocation source left. Each such sequence is rewritten
///   mov eax,[V]; and eax,FLAG; cmp eax,FLAG; sete al; jmp after   (tail call: sete al; epilogue; ret)
/// which is HasFlag's own result ((value &amp; flag) == flag) for two values of the same 32-bit enum type.
/// A site is only rewritten when every instruction of the sequence is recognised (both boxes use the same type,
/// the value and the flag are 32-bit stores, the flag is a constant or a callee-saved register); 110 of the game's
/// 136 HasFlag calls (116 calls, 20 tail calls) qualify, the others keep the original code. The original bytes are kept to switch it off.
/// </summary>
internal sealed unsafe class EnumFlagsInline : Feature
{
    public override string Name => "Enum flag checks without allocation";
    protected override string Section => "EnumFlags";
    protected override string Description =>
        "Check NPC task flags without creating garbage (fewer garbage collector runs). Same results.";

    private readonly List<(IntPtr at, byte[] original, byte[] patched)> sites = new();
    private bool applied;

    protected override string TryInstall()
    {
        byte* box = NativeCode.ExportTarget("il2cpp_value_box");
        byte* hasFlag = NativeCode.MethodPointer(IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Enum"), "HasFlag", 1);
        if (box == null || hasFlag == null) return "Box / Enum.HasFlag not found";

        int calls = 0;
        foreach (string opcode in new[] { "E8", "E9" }) // call HasFlag, or jmp HasFlag (tail call)
            foreach (IntPtr hit in NativeCode.ScanGameAssembly(NativeCode.Pattern(opcode + " ?? ?? ?? ??"),
                         p => NativeCode.Rel32Target((byte*)p) == hasFlag))
            {
                calls++;
                if (Match((byte*)hit, box) is { } site) sites.Add(site);
            }
        if (sites.Count == 0) return $"no recognised Enum.HasFlag call among {calls} (game update?)";
        Plugin.Log.LogDebug($"{Name}: {sites.Count} of {calls} HasFlag calls rewritten");
        Tick();
        return null;
    }

    public override void Tick()
    {
        if (applied == Active) return;
        applied = Active;
        foreach (var (at, original, patched) in sites)
            NativeCode.WriteCode((byte*)at, applied ? patched : original);
    }

    // ---- sequence recognition ----

    private const int Rsp = 4, Rbp = 5;
    private static readonly HashSet<int> CalleeSaved = new() { 3, 5, 6, 7, 12, 13, 14, 15 };

    private enum Kind { None, TypeInfo, Lea, MovResult, StoreImm, StoreReg }

    private struct Insn
    {
        public int Length; public Kind Kind;
        public int Base, Disp, Reg, Imm; // Lea: Base/Disp; MovResult: Reg; StoreImm: Base/Disp/Imm; StoreReg: Base/Disp/Reg
        public byte* Target;             // TypeInfo: address of the TypeInfo slot
    }

    /// <summary>[rsp+disp] / [rbp+disp] modrm at p (mod 01 or 10): its length, or 0.</summary>
    private static int Mem(byte* p, int rexR, out int baseReg, out int disp, out int reg)
    {
        int m = p[0], mod = m >> 6, rm = m & 7;
        reg = ((m >> 3) & 7) | rexR; baseReg = rm; disp = 0;
        if ((mod != 1 && mod != 2) || (rm != Rsp && rm != Rbp)) return 0;
        int n = 1;
        if (rm == Rsp) { if (p[1] != 0x24) return 0; n = 2; }
        if (mod == 1) { disp = (sbyte)p[n]; return n + 1; }
        disp = *(int*)(p + n); return n + 4;
    }

    private static Insn Decode(byte* p)
    {
        var i = new Insn();
        int n;
        if (p[0] == 0x48 && p[1] == 0x8B && p[2] == 0x0D)                      // mov rcx,[rip+TypeInfo]
        { i.Length = 7; i.Kind = Kind.TypeInfo; i.Target = p + 7 + *(int*)(p + 3); }
        else if (p[0] == 0x48 && p[1] == 0x8D && (n = Mem(p + 2, 0, out i.Base, out i.Disp, out int r)) > 0 && r == 2)
        { i.Length = 2 + n; i.Kind = Kind.Lea; }                                 // lea rdx,[base+disp]
        else if ((p[0] == 0x48 || p[0] == 0x4C) && p[1] == 0x8B && (p[2] & 0xC7) == 0xC0)
        { i.Length = 3; i.Kind = Kind.MovResult; i.Reg = ((p[2] >> 3) & 7) | (p[0] == 0x4C ? 8 : 0); } // mov R,rax
        else if (p[0] == 0xC7 && (n = Mem(p + 1, 0, out i.Base, out i.Disp, out r)) > 0 && r == 0)
        { i.Length = 1 + n + 4; i.Kind = Kind.StoreImm; i.Imm = *(int*)(p + 1 + n); } // mov dword [base+disp],imm32
        else if (p[0] == 0x89 && (n = Mem(p + 1, 0, out i.Base, out i.Disp, out i.Reg)) > 0)
        { i.Length = 1 + n; i.Kind = Kind.StoreReg; }                            // mov [base+disp],r32
        else if (p[0] == 0x44 && p[1] == 0x89 && (n = Mem(p + 2, 8, out i.Base, out i.Disp, out i.Reg)) > 0)
        { i.Length = 2 + n; i.Kind = Kind.StoreReg; }                            // mov [base+disp],r8d..r15d
        return i;
    }

    private static bool IsCall(byte* p, byte* target) => p[0] == 0xE8 && NativeCode.Rel32Target(p) == target;

    /// <summary>Length of one epilogue instruction (mov r64,[rsp+d8] / add rsp,imm8 / pop r64), or 0.</summary>
    private static int Epilogue(byte* p)
    {
        if ((p[0] == 0x48 || p[0] == 0x4C) && p[1] == 0x8B && (p[2] & 0xC7) == 0x44 && p[3] == 0x24) return 5;
        if (p[0] == 0x48 && p[1] == 0x83 && p[2] == 0xC4) return 4;
        if (p[0] is >= 0x58 and <= 0x5F && p[0] != 0x5C) return 1;
        if (p[0] == 0x41 && p[1] is >= 0x58 and <= 0x5F) return 2;
        return 0;
    }

    private static (IntPtr, byte[], byte[])? Match(byte* hit, byte* box)
    {
        // a tail call (jmp HasFlag) first restores registers and the stack: find where that epilogue starts
        bool tailCall = hit[0] == 0xE9;
        byte* call = null;
        for (int el = 0; el < (tailCall ? 24 : 1) && call == null; el++)
        {
            byte* o = hit - el - 9; // xor r8d,r8d; mov rdx,rax; mov rcx,R
            if (o[0] != 0x45 || o[1] != 0x33 || o[2] != 0xC0 || o[3] != 0x48 || o[4] != 0x8B || o[5] != 0xD0) continue;
            byte* p = o + 9;
            for (int n; p < hit && (n = Epilogue(p)) > 0;) p += n;
            if (p == hit) call = hit - el;
        }
        if (call == null) return null;

        // fixed tail: call Box; test R,R; je (short or near); xor r8d,r8d; mov rdx,rax; mov rcx,R; [epilogue]; call/jmp HasFlag
        byte* t = null; int jeLen = 0;
        foreach (int jl in new[] { 2, 6 })
            if (IsCall(call - 17 - jl, box)) { t = call - 17 - jl; jeLen = jl; break; }
        if (t == null) return null;
        if (jeLen == 2 ? t[8] != 0x74 : t[8] != 0x0F || t[9] != 0x84) return null;
        if ((t[5] != 0x48 && t[5] != 0x4D) || t[6] != 0x85) return null;
        int r1 = (t[7] & 7) | (t[5] == 0x4D ? 8 : 0);
        if (t[7] >> 6 != 3 || ((t[7] >> 3) & 7) != (t[7] & 7)) return null;
        byte* t2 = t + 8 + jeLen;
        if (t2[0] != 0x45 || t2[1] != 0x33 || t2[2] != 0xC0 || t2[3] != 0x48 || t2[4] != 0x8B || t2[5] != 0xD0) return null;
        if (t2[6] != (r1 < 8 ? 0x48 : 0x49) || t2[7] != 0x8B || t2[8] != (0xC8 | (r1 & 7))) return null;

        for (byte* b1 = t - 35; b1 < t - 15; b1++)
        {
            if (!IsCall(b1, box)) continue;
            // between the boxes: exactly {mov rcx,TypeInfo; lea rdx,F; mov R,rax; store flag -> F}, any order
            var mid = new Insn[6];
            byte* p = b1 + 5;
            int count = 0;
            while (p < t)
            {
                Insn i = Decode(p);
                if (i.Kind == Kind.None || mid[(int)i.Kind].Kind != Kind.None) break;
                mid[(int)i.Kind] = i; p += i.Length; count++;
            }
            if (p != t || count != 4) continue;
            Insn ti = mid[(int)Kind.TypeInfo], lea = mid[(int)Kind.Lea], res = mid[(int)Kind.MovResult];
            Insn store = mid[(int)Kind.StoreImm].Kind != Kind.None ? mid[(int)Kind.StoreImm] : mid[(int)Kind.StoreReg];
            if (ti.Kind == Kind.None || lea.Kind == Kind.None || res.Kind == Kind.None || store.Kind == Kind.None) return null;
            if (res.Reg != r1 || store.Base != lea.Base || store.Disp != lea.Disp) return null;
            if (store.Kind == Kind.StoreReg && !CalleeSaved.Contains(store.Reg)) return null;

            // before the first box: {mov rcx,TypeInfo (same); lea rdx,V; store value -> V}, decoded backwards
            var pre = new Insn[6];
            byte* q = b1;
            for (int k = 0; k < 3; k++)
            {
                bool found = false;
                foreach (int len in new[] { 7, 8, 5, 4, 3, 6 })
                {
                    Insn i = Decode(q - len);
                    if (i.Length != len || i.Kind is not (Kind.TypeInfo or Kind.Lea or Kind.StoreReg)
                        || pre[(int)i.Kind].Kind != Kind.None) continue;
                    pre[(int)i.Kind] = i; q -= len; found = true; break;
                }
                if (!found) break;
            }
            Insn pti = pre[(int)Kind.TypeInfo], plea = pre[(int)Kind.Lea], pstore = pre[(int)Kind.StoreReg];
            if (pti.Kind == Kind.None || pti.Target != ti.Target) return null;
            if (plea.Kind == Kind.None || pstore.Kind == Kind.None || pstore.Base != plea.Base || pstore.Disp != plea.Disp)
                return null;

            byte* end = hit + 5;
            var code = new List<byte> { 0x8B };                                    // mov eax,[base+disp]
            code.AddRange(ModRm(0, plea.Base, plea.Disp));
            if (store.Kind == Kind.StoreImm)
            {
                code.Add(0x25); code.AddRange(BitConverter.GetBytes(store.Imm));    // and eax,imm32
                code.Add(0x3D); code.AddRange(BitConverter.GetBytes(store.Imm));    // cmp eax,imm32
            }
            else
            {
                byte modrm = (byte)(0xC0 | ((store.Reg & 7) << 3));
                foreach (byte op in new byte[] { 0x21, 0x39 })                       // and eax,r32; cmp eax,r32
                {
                    if (store.Reg >= 8) code.Add(0x44);
                    code.Add(op); code.Add(modrm);
                }
            }
            code.AddRange(new byte[] { 0x0F, 0x94, 0xC0 });                        // sete al
            int length = (int)(end - b1);
            if (tailCall)
            {
                for (byte* e = call; e < hit; e++) code.Add(*e);                    // the function's epilogue
                code.Add(0xC3);                                                     // ret
            }
            else
            {
                code.Add(0xEB); code.Add((byte)(length - code.Count - 1));         // jmp end
            }
            while (code.Count < length) code.Add(0xCC);

            var original = new byte[length];
            for (int k = 0; k < length; k++) original[k] = b1[k];
            return ((IntPtr)b1, original, code.ToArray());
        }
        return null;
    }

    private static byte[] ModRm(int reg, int baseReg, int disp)
    {
        bool small = disp is >= -128 and <= 127;
        var b = new List<byte> { (byte)((small ? 0x40 : 0x80) | (reg << 3) | baseReg) };
        if (baseReg == Rsp) b.Add(0x24);
        if (small) b.Add((byte)(sbyte)disp); else b.AddRange(BitConverter.GetBytes(disp));
        return b.ToArray();
    }
}
