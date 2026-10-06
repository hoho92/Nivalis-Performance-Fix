using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace NivalisPerformanceFix.Native;

/// <summary>
/// Il2CppInterop calls game / Unity methods through il2cpp_runtime_invoke, which returns value types BOXED: every
/// Time.deltaTime, bool property or `unityObject != null` made from plugin code leaves one garbage object on the
/// game's heap (measured: ~2,400 bools + ~700 floats / ints per second from our own per-frame code). The values read
/// every frame are fetched here by calling the method's compiled code directly (IL2CPP convention: arguments, then
/// the MethodInfo*), which allocates nothing. Only for methods that cannot throw. If a method is not found, the
/// normal interop call is used.
/// </summary>
internal static unsafe class Direct
{
    private readonly struct Method
    {
        public readonly IntPtr Code, Info;
        public Method(IntPtr klass, string name, int args)
        {
            Info = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, name, args);
            Code = Info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)Info; // MethodInfo.methodPointer
        }
        public bool Ok => Code != IntPtr.Zero;
    }

    private static readonly IntPtr TimeClass = Il2CppClassPointerStore<Time>.NativeClassPtr;
    private static readonly Method deltaTime = new(TimeClass, "get_deltaTime", 0);
    private static readonly Method unscaledDeltaTime = new(TimeClass, "get_unscaledDeltaTime", 0);
    private static readonly Method unscaledTime = new(TimeClass, "get_unscaledTime", 0);
    private static readonly Method timeScale = new(TimeClass, "get_timeScale", 0);
    private static readonly Method activeScene =
        new(Il2CppClassPointerStore<SceneManager>.NativeClassPtr, "GetActiveScene", 0);
    private static readonly Method collectionCount =
        new(IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "GC"), "CollectionCount", 1);
    private static readonly IntPtr ButtonClass = Il2CppClassPointerStore<ButtonControl>.NativeClassPtr;
    private static readonly Method wasPressed = new(ButtonClass, "get_wasPressedThisFrame", 0);
    private static readonly Method isPressed = new(ButtonClass, "get_isPressed", 0);

    private static readonly Method behaviourEnabled =
        new(Il2CppClassPointerStore<Behaviour>.NativeClassPtr, "get_enabled", 0);

    private static float Float(in Method m) => ((delegate* unmanaged<IntPtr, float>)m.Code)(m.Info);

    public static float DeltaTime => deltaTime.Ok ? Float(deltaTime) : Time.deltaTime;
    public static float UnscaledDeltaTime => unscaledDeltaTime.Ok ? Float(unscaledDeltaTime) : Time.unscaledDeltaTime;
    public static float UnscaledTime => unscaledTime.Ok ? Float(unscaledTime) : Time.unscaledTime;
    public static float TimeScale => timeScale.Ok ? Float(timeScale) : Time.timeScale;

    /// <summary>SceneManager.GetActiveScene().handle (Scene is a struct holding only the int handle).</summary>
    public static int ActiveSceneHandle =>
        activeScene.Ok ? ((delegate* unmanaged<IntPtr, int>)activeScene.Code)(activeScene.Info)
                       : SceneManager.GetActiveScene().handle;

    public static int GcCollectionCount(int generation) =>
        collectionCount.Ok ? ((delegate* unmanaged<int, IntPtr, int>)collectionCount.Code)(generation, collectionCount.Info)
                           : Il2CppSystem.GC.CollectionCount(generation);

    public static bool WasPressedThisFrame(ButtonControl b) =>
        wasPressed.Ok ? ((delegate* unmanaged<IntPtr, IntPtr, byte>)wasPressed.Code)(b.Pointer, wasPressed.Info) != 0
                      : b.wasPressedThisFrame;

    public static bool IsPressed(ButtonControl b) =>
        isPressed.Ok ? ((delegate* unmanaged<IntPtr, IntPtr, byte>)isPressed.Code)(b.Pointer, isPressed.Info) != 0
                     : b.isPressed;

    /// <summary>Behaviour.enabled; <paramref name="b"/> must be alive (see <see cref="Alive"/>).</summary>
    public static bool Enabled(Behaviour b) =>
        behaviourEnabled.Ok ? ((delegate* unmanaged<IntPtr, IntPtr, byte>)behaviourEnabled.Code)(b.Pointer, behaviourEnabled.Info) != 0
                            : b.enabled;

    /// <summary>Unity's `obj != null` (the native object still exists) without the boxed bool of op_Inequality.</summary>
    public static bool Alive(UnityEngine.Object o) => o is not null && o.m_CachedPtr != IntPtr.Zero;
}
