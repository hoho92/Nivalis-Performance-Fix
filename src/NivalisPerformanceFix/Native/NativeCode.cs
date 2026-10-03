using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace NivalisPerformanceFix.Native;

/// <summary>
/// Helpers for the few patches that rewrite GameAssembly machine code: locating code by byte signature,
/// allocating memory close enough for rel32 calls / rip-relative operands, and writing to read-only code.
/// Every patch verifies what it finds and refuses to apply on a mismatch, so a game update disables the
/// patch instead of corrupting the game.
/// </summary>
internal static unsafe class NativeCode
{
    [DllImport("kernel32")] private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);
    [DllImport("kernel32")] private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint prot, out uint old);
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr addr, UIntPtr size);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool RtlAddFunctionTable(IntPtr table, uint count, ulong baseAddress);

    private const uint MemCommitReserve = 0x3000, PageReadWrite = 0x04, PageExecuteReadWrite = 0x40;

    public static byte* GameAssembly { get; } = (byte*)GetModuleHandleW("GameAssembly.dll");

    public static uint GameAssemblySize
    {
        get
        {
            byte* nt = GameAssembly + *(int*)(GameAssembly + 0x3C);
            return *(uint*)(nt + 0x50); // OptionalHeader.SizeOfImage
        }
    }

    public static string Rva(void* p) => $"GameAssembly+0x{(long)((byte*)p - GameAssembly):X}";

    /// <summary>Parses "48 8B ?? 05" into bytes, ?? = wildcard.</summary>
    public static byte?[] Pattern(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t == "??" ? (byte?)null : Convert.ToByte(t, 16)).ToArray();

    /// <summary>All addresses in [start, start+length) matching the pattern (first byte must not be a wildcard).</summary>
    public static List<IntPtr> Scan(byte* start, long length, byte?[] pattern, Func<IntPtr, bool> accept = null)
    {
        var hits = new List<IntPtr>();
        byte first = pattern[0] ?? throw new ArgumentException("pattern must start with a fixed byte");
        for (byte* p = start, end = start + length - pattern.Length; p < end; p++)
        {
            if (*p != first) continue;
            int i = 1;
            while (i < pattern.Length && (!pattern[i].HasValue || p[i] == pattern[i].Value)) i++;
            if (i == pattern.Length && (accept == null || accept((IntPtr)p))) hits.Add((IntPtr)p);
        }
        return hits;
    }

    /// <summary>Scans the whole GameAssembly image.</summary>
    public static List<IntPtr> ScanGameAssembly(byte?[] pattern, Func<IntPtr, bool> accept = null) =>
        Scan(GameAssembly + 0x1000, GameAssemblySize - 0x1000, pattern, accept);

    /// <summary>Target of a rel32 call/jmp whose opcode is at <paramref name="insn"/> (E8/E9 xx xx xx xx).</summary>
    public static byte* Rel32Target(byte* insn) => insn + 5 + *(int*)(insn + 1);

    /// <summary>Native code pointer of an Il2Cpp method (MethodInfo.methodPointer is its first field).</summary>
    public static byte* MethodPointer(IntPtr klass, string name, int argCount)
    {
        if (klass == IntPtr.Zero) return null;
        IntPtr mi = IL2CPP.il2cpp_class_get_method_from_name(klass, name, argCount);
        return mi == IntPtr.Zero ? null : *(byte**)mi;
    }

    /// <summary>
    /// One page within +-2 GB of <paramref name="near"/> (below GameAssembly), so rel32 calls and rip-relative
    /// operands from that code can reach it. Returns null if none is free.
    /// </summary>
    public static byte* AllocNear(byte* near, bool executable)
    {
        for (long d = 0x1000000; d < 0x70000000; d += 0x1000000)
        {
            byte* mem = (byte*)VirtualAlloc((IntPtr)(GameAssembly - d), (UIntPtr)4096, MemCommitReserve,
                executable ? PageExecuteReadWrite : PageReadWrite);
            if (mem == null) continue;
            if (Math.Abs((long)mem - (long)near) < int.MaxValue - 0x10000) return mem;
        }
        return null;
    }

    /// <summary>Writes bytes into (read-only) code and flushes the instruction cache.</summary>
    public static void WriteCode(byte* at, byte[] bytes)
    {
        VirtualProtect((IntPtr)at, (UIntPtr)bytes.Length, PageExecuteReadWrite, out uint old);
        Marshal.Copy(bytes, 0, (IntPtr)at, bytes.Length);
        VirtualProtect((IntPtr)at, (UIntPtr)bytes.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)at, (UIntPtr)bytes.Length);
    }

    /// <summary>Copies a freshly generated stub into executable memory.</summary>
    public static void WriteStub(byte* at, IReadOnlyList<byte> code)
    {
        Marshal.Copy(code.ToArray(), 0, (IntPtr)at, code.Count);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)at, (UIntPtr)code.Count);
    }

    /// <summary>rel32 displacement from the end of an instruction to a target, checked to fit.</summary>
    public static int Rel32(byte* nextInsn, void* target) => checked((int)((long)target - (long)nextInsn));

    /// <summary>Redirects an existing `call rel32` (E8) at <paramref name="site"/> to <paramref name="stub"/>.</summary>
    public static void RedirectCall(byte* site, byte* stub)
    {
        if (*site != 0xE8) throw new InvalidOperationException("not a rel32 call");
        WriteCode(site + 1, BitConverter.GetBytes(Rel32(site + 5, stub)));
    }

    /// <summary>
    /// Registers unwind info for a stub region that does `push rbx; sub rsp, N` (so exceptions thrown by the
    /// game code it calls can unwind through it). <paramref name="table"/> needs 12 + 8 free bytes in the block.
    /// </summary>
    public static bool RegisterPushRbxFrame(byte* block, byte* table, byte* unwindInfo, int funcStart, int funcEnd,
        int prologSize, int afterPush, int stackAlloc)
    {
        uint* rf = (uint*)table;
        rf[0] = (uint)funcStart; rf[1] = (uint)funcEnd; rf[2] = (uint)(unwindInfo - block);
        unwindInfo[0] = 1; unwindInfo[1] = (byte)prologSize; unwindInfo[2] = 2; unwindInfo[3] = 0;
        unwindInfo[4] = (byte)prologSize; unwindInfo[5] = (byte)(0x02 | ((stackAlloc / 8 - 1) << 4)); // UWOP_ALLOC_SMALL
        unwindInfo[6] = (byte)afterPush; unwindInfo[7] = 0x00 | (3 << 4);                            // UWOP_PUSH_NONVOL rbx
        return RtlAddFunctionTable((IntPtr)table, 1, (ulong)block);
    }
}

/// <summary>Tiny x64 emitter with labels for short jumps (enough for our stubs).</summary>
internal sealed class Asm
{
    private readonly List<byte> code = new();
    private readonly Dictionary<string, int> labels = new();
    private readonly List<(int at, string label)> fixups = new();

    public int Position => code.Count;
    public IReadOnlyList<byte> Bytes => code;

    public Asm Emit(params byte[] b) { code.AddRange(b); return this; }
    public Asm Imm32(int v) { code.AddRange(BitConverter.GetBytes(v)); return this; }
    public Asm Imm64(ulong v) { code.AddRange(BitConverter.GetBytes(v)); return this; }
    public Asm Label(string name) { labels[name] = code.Count; return this; }

    /// <summary>Short conditional/unconditional jump: opcode byte (74 je, 75 jne, 73 jae, 77 ja, EB jmp) + rel8.</summary>
    public Asm Jump(byte opcode, string label) { code.Add(opcode); fixups.Add((code.Count, label)); code.Add(0); return this; }

    public byte[] Build()
    {
        foreach (var (at, label) in fixups)
            code[at] = checked((byte)(sbyte)(labels[label] - (at + 1)));
        return code.ToArray();
    }
}
