using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Nivalis;
using NivalisPerformanceFix.Native;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Fast read of the game's live characters (static HashSet&lt;Character&gt; Character.instances), shared by the
/// features that work per character. Reads the IL2CPP HashSet memory directly: no allocation, no interop call
/// per character.
/// </summary>
internal static unsafe class CharacterSet
{
    // HashSet<T>: _slots (Slot[]) and _lastIndex; Slot = { int hashCode; int next; T value; }, free slots have
    // hashCode < 0. Offsets and the slot size are read from the game's classes at the first read (see Layout).
    private static int setSlots, setLastIndex, slotSize, slotHash, slotValue;
    private static bool slotsKnown;
    private static bool layoutBad;

    private static IntPtr instancesField;

    /// <summary>Locates Character.instances; false after a game update that removed it.</summary>
    public static bool Resolve()
    {
        if (instancesField != IntPtr.Zero) return true;
        IntPtr character = Il2CppClassPointerStore<Character>.NativeClassPtr;
        if (character != IntPtr.Zero) instancesField = IL2CPP.GetIl2CppField(character, "instances");
        return instancesField != IntPtr.Zero;
    }

    /// <summary>Field offset in an IL2CPP class (object header included), -1 if missing.</summary>
    public static int Offset(IntPtr klass, string field)
    {
        IntPtr f = IL2CPP.GetIl2CppField(klass, field);
        return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f);
    }

    /// <summary>Fills <paramref name="into"/> with the native pointers of the live characters.</summary>
    public static void Collect(List<IntPtr> into)
    {
        into.Clear();
        if (instancesField == IntPtr.Zero) return;
        IntPtr set;
        IL2CPP.il2cpp_field_static_get_value(instancesField, &set);
        if (set == IntPtr.Zero || !LayoutOf(set)) return;
        byte* slots = *(byte**)((byte*)set + setSlots);
        int last = *(int*)((byte*)set + setLastIndex);
        if (slots == null || last <= 0) return;
        if ((ulong)last > *(ulong*)(slots + Layout.ArrayLength)) return; // inconsistent: do nothing
        if (!slotsKnown && !SlotLayout(slots)) return;
        byte* slot = slots + Layout.ArrayData;
        for (int i = 0; i < last; i++, slot += slotSize)
        {
            if (*(int*)(slot + slotHash) < 0) continue;
            IntPtr c = *(IntPtr*)(slot + slotValue);
            if (c != IntPtr.Zero) into.Add(c);
        }
    }

    /// <summary>Field offsets of the set's class (resolved once); false if they cannot be trusted.</summary>
    private static bool LayoutOf(IntPtr set)
    {
        if (layoutBad) return false;
        if (setSlots > 0) return true;
        IntPtr klass = Layout.ClassOf((void*)set);
        setSlots = Layout.Field(klass, "_slots");
        setLastIndex = Layout.Field(klass, "_lastIndex");
        if (setSlots > 0 && setLastIndex > 0) return true;
        Fail("HashSet fields not found");
        return false;
    }

    /// <summary>Slot size and field offsets, from the first slot array seen; false if they cannot be trusted.</summary>
    private static bool SlotLayout(byte* slots)
    {
        var (size, slotClass) = Layout.ArrayElement(slots);
        slotHash = Layout.ValueField(slotClass, "hashCode");
        slotValue = Layout.ValueField(slotClass, "value");
        slotSize = size;
        if (slotHash >= 0 && slotValue >= 0 && slotHash + 4 <= size && slotValue + IntPtr.Size <= size)
        {
            slotsKnown = true;
            return true;
        }
        Fail($"HashSet slot layout not recognised (size {size}, hashCode {slotHash}, value {slotValue})");
        return false;
    }

    private static void Fail(string why)
    {
        layoutBad = true;
        setSlots = 0;
        Plugin.Log.LogWarning($"Character list unreadable ({why}): per-character optimizations do nothing. Game update?");
    }
}
