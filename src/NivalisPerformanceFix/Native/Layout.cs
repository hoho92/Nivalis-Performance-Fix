using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace NivalisPerformanceFix.Native;

/// <summary>
/// Memory layout of IL2CPP objects read at runtime (field offsets, array element sizes), for the code that reads
/// game objects directly: a game update that changes a layout then changes the offsets with it, or makes the
/// lookup fail (-1) instead of reading the wrong memory.
/// </summary>
internal static unsafe class Layout
{
    /// <summary>Il2CppArray: max_length, then the elements.</summary>
    public const int ArrayLength = 0x18, ArrayData = 0x20;

    private const int ObjectHeader = 0x10; // value type field offsets count the boxed object's header

    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_object_get_class(IntPtr obj);
    [DllImport("GameAssembly")] private static extern int il2cpp_array_element_size(IntPtr arrayClass);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_field_get_type(IntPtr field);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_from_type(IntPtr type);

    public static IntPtr ClassOf(void* obj) => obj == null ? IntPtr.Zero : il2cpp_object_get_class((IntPtr)obj);

    /// <summary>Offset of a field in an object of class <paramref name="klass"/> (header included), -1 if missing.</summary>
    public static int Field(IntPtr klass, string name)
    {
        IntPtr f = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(klass, name);
        return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f);
    }

    /// <summary>Offset of a field inside an unboxed value of the value type <paramref name="klass"/>, -1 if missing.</summary>
    public static int ValueField(IntPtr klass, string name)
    {
        int offset = Field(klass, name);
        return offset < ObjectHeader ? -1 : offset - ObjectHeader;
    }

    /// <summary>Class of the values a field holds, IntPtr.Zero if missing.</summary>
    public static IntPtr FieldClass(IntPtr klass, string name)
    {
        IntPtr f = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(klass, name);
        IntPtr type = f == IntPtr.Zero ? IntPtr.Zero : il2cpp_field_get_type(f);
        return type == IntPtr.Zero ? IntPtr.Zero : il2cpp_class_from_type(type);
    }

    /// <summary>Element size and element class of an array object; (0, Zero) if <paramref name="array"/> is null.</summary>
    public static (int size, IntPtr element) ArrayElement(void* array)
    {
        IntPtr klass = ClassOf(array);
        return klass == IntPtr.Zero ? (0, IntPtr.Zero) : (il2cpp_array_element_size(klass), il2cpp_class_get_element_class(klass));
    }
}
