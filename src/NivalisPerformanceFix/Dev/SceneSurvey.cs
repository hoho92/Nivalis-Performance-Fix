using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using Nivalis;
using Nivalis.DayNightCycle;
using NivalisPerformanceFix.Features;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool (SurveyKey, F12): one-shot, read-only inventory of the current scene, written to
/// BepInEx/NivalisPerformanceFix/survey-&lt;scene&gt;-&lt;time&gt;.json for offline comparison (tools/survey.py).
///  * meta: quality settings, lightmaps, characters, managed heap;
///  * cameras (+ the game's per-layer cull distances), lights, reflection probes (+ the game's manager knobs);
///  * components: every component type in the scene with its instance count, how many are enabled, and whether
///    the type has Update / LateUpdate / FixedUpdate (read from IL2CPP metadata, parents included). This is the
///    map of per-frame scripts used to find optimization targets.
/// Takes about a second on dense scenes (one hitch per key press, never automatic). Modifies nothing.
/// </summary>
internal static class SceneSurvey
{
    internal static void Run()
    {
        try
        {
            string path = Write();
            Plugin.Log.LogMessage($"Scene survey written: {path}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Scene survey failed: {e}");
        }
    }

    private static string Write()
    {
        string scene = SceneManager.GetActiveScene().name?.Replace(' ', '_') ?? "unknown";
        DateTime now = DateTime.Now;
        string dir = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"survey-{scene}-{now:yyyyMMdd-HHmmss}.json");

        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, Options);
        w.WriteStartObject();
        Section(w, "meta", s => Meta(s, scene, now));
        Section(w, "cameras", Cameras);
        Section(w, "lights", Lights);
        Section(w, "reflectionProbes", ReflectionProbes);
        Section(w, "components", Components);
        w.WriteEndObject();
        return path;
    }

    private static readonly JsonWriterOptions Options = new() { Indented = true };

    /// <summary>Writes one named section, built in its own buffer so that a failure is recorded as
    /// {"error": ...} and the file stays valid JSON.</summary>
    private static void Section(Utf8JsonWriter w, string name, Action<Utf8JsonWriter> body)
    {
        w.WritePropertyName(name);
        try
        {
            using var buffer = new MemoryStream();
            using (var s = new Utf8JsonWriter(buffer, Options))
            {
                s.WriteStartObject();
                body(s);
                s.WriteEndObject();
            }
            w.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
        }
        catch (Exception e)
        {
            w.WriteStartObject();
            w.WriteString("error", e.Message);
            w.WriteEndObject();
        }
    }

    private static void Num(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, Math.Round(v, 3)); else w.WriteNull(name);
    }

    private static void Meta(Utf8JsonWriter w, string scene, DateTime now)
    {
        w.WriteString("scene", scene);
        w.WriteString("time", now.ToString("yyyy-MM-dd HH:mm:ss"));
        w.WriteString("modVersion", Plugin.Version);
        w.WriteBoolean("modEnabled", Plugin.MasterEnabled.Value);
        w.WriteString("screen", $"{Screen.width}x{Screen.height}");
        w.WriteNumber("qualityLevel", QualitySettings.GetQualityLevel());
        w.WriteNumber("vSyncCount", QualitySettings.vSyncCount);
        w.WriteNumber("targetFrameRate", Application.targetFrameRate);
        Num(w, "shadowDistance", QualitySettings.shadowDistance);
        w.WriteNumber("shadowCascades", QualitySettings.shadowCascades);
        w.WriteNumber("pixelLightCount", QualitySettings.pixelLightCount);
        Num(w, "lodBias", QualitySettings.lodBias);
        w.WriteNumber("antiAliasing", QualitySettings.antiAliasing);
        w.WriteBoolean("realtimeReflectionProbes", QualitySettings.realtimeReflectionProbes);
        w.WriteNumber("lightmaps", LightmapSettings.lightmaps?.Length ?? 0);
        Num(w, "timeScale", Time.timeScale);
        var (chars, visible) = AnimationLod.Instance?.CountCharacters() ?? (-1, -1);
        w.WriteNumber("characters", chars);
        w.WriteNumber("charactersVisible", visible);
        w.WriteNumber("heapMb", il2cpp_gc_get_used_size() / (1024 * 1024));
        Camera main = Camera.main;
        if (main != null)
        {
            Vector3 p = main.transform.position;
            w.WriteString("cameraPos", $"{p.x:F0},{p.y:F0},{p.z:F0}");
        }
    }

    private static void Cameras(Utf8JsonWriter w)
    {
        w.WriteStartArray("items");
        foreach (Camera c in UnityEngine.Object.FindObjectsOfType<Camera>())
        {
            w.WriteStartObject();
            w.WriteString("name", c.name);
            w.WriteBoolean("enabled", c.enabled);
            Num(w, "depth", c.depth);
            Num(w, "far", c.farClipPlane);
            w.WriteNumber("cullingMask", c.cullingMask);
            RenderTexture rt = c.targetTexture;
            if (rt != null) w.WriteString("targetTexture", $"{rt.name} {rt.width}x{rt.height}");
            float[] lcd = c.layerCullDistances;
            var set = lcd?.Where(d => d > 0).ToArray() ?? Array.Empty<float>();
            w.WriteNumber("layerCullDistances", set.Length);
            if (set.Length > 0) { Num(w, "layerCullMin", set.Min()); Num(w, "layerCullMax", set.Max()); }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // the game's per-layer cull distances (ScriptableObject asset: only FindObjectsOfTypeAll sees it)
        w.WriteStartArray("cullLayers");
        foreach (CullLayersStorage s in Resources.FindObjectsOfTypeAll<CullLayersStorage>())
            foreach (var l in s.layers)
            {
                if (l == null) continue;
                w.WriteStartObject();
                w.WriteNumber("index", l.index);
                w.WriteString("name", LayerMask.LayerToName(l.index));
                w.WriteNumber("mask", l.name.value); // the game's field "name" is a LayerMask
                Num(w, "distance", l.distance);
                w.WriteEndObject();
            }
        w.WriteEndArray();
    }

    private static void Lights(Utf8JsonWriter w)
    {
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        int enabled = 0, shadows = 0;
        var byType = new Dictionary<string, int>();
        foreach (Light l in lights)
        {
            if (l.enabled) enabled++;
            if (l.shadows != LightShadows.None) shadows++;
            string t = l.type.ToString();
            byType[t] = byType.TryGetValue(t, out int n) ? n + 1 : 1;
        }
        w.WriteNumber("total", lights.Length);
        w.WriteNumber("enabled", enabled);
        w.WriteNumber("withShadows", shadows);
        w.WriteStartObject("byType");
        foreach (var kv in byType) w.WriteNumber(kv.Key, kv.Value);
        w.WriteEndObject();
    }

    private static void ReflectionProbes(Utf8JsonWriter w)
    {
        var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
        w.WriteNumber("total", probes.Length);
        w.WriteNumber("realtime", probes.Count(p => p.mode == UnityEngine.Rendering.ReflectionProbeMode.Realtime));
        w.WriteNumber("refreshEveryFrame",
            probes.Count(p => p.refreshMode == UnityEngine.Rendering.ReflectionProbeRefreshMode.EveryFrame));
        ReflectionProbeManager m = ReflectionProbeManager.Instance;
        if (m != null)
        {
            w.WriteNumber("managerRefreshFrequency", m.refreshFrequency);
            w.WriteBoolean("managerTimeSlicing", m.runtimeTimeSlicing);
        }
    }

    private sealed class TypeInfo
    {
        public string Name;
        public int Count, Enabled;
        public bool HasEnabled;
        public string PerFrame;
    }

    private static void Components(Utf8JsonWriter w)
    {
        IntPtr behaviourClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Behaviour>.NativeClassPtr;
        IntPtr rendererClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Renderer>.NativeClassPtr;
        var types = new Dictionary<IntPtr, TypeInfo>();
        int total = 0;
        foreach (Component c in UnityEngine.Object.FindObjectsOfType<Component>())
        {
            if (c == null) continue;
            IntPtr klass = il2cpp_object_get_class(c.Pointer);
            if (klass == IntPtr.Zero) continue;
            total++;
            if (!types.TryGetValue(klass, out TypeInfo info))
            {
                types[klass] = info = new TypeInfo
                {
                    Name = ClassName(klass),
                    HasEnabled = il2cpp_class_is_subclass_of(klass, behaviourClass, false) ||
                                 il2cpp_class_is_subclass_of(klass, rendererClass, false),
                    PerFrame = PerFrame(klass),
                };
            }
            info.Count++;
            if (!info.HasEnabled) continue;
            Behaviour b = c.TryCast<Behaviour>();
            if (b != null ? b.enabled : c.Cast<Renderer>().enabled) info.Enabled++;
        }

        var sorted = types.Values.OrderByDescending(t => t.Count).ToList();
        w.WriteNumber("instances", total);
        w.WriteNumber("types", sorted.Count);
        w.WriteNumber("perFrameInstancesEnabled", sorted.Where(t => t.PerFrame.Length > 0).Sum(t => t.HasEnabled ? t.Enabled : t.Count));
        w.WriteStartArray("items");
        foreach (TypeInfo t in sorted)
        {
            w.WriteStartObject();
            w.WriteString("type", t.Name);
            w.WriteNumber("count", t.Count);
            if (t.HasEnabled) w.WriteNumber("enabled", t.Enabled);
            if (t.PerFrame.Length > 0) w.WriteString("perFrame", t.PerFrame);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string ClassName(IntPtr klass)
    {
        string ns = Marshal.PtrToStringAnsi(il2cpp_class_get_namespace(klass));
        string name = Marshal.PtrToStringAnsi(il2cpp_class_get_name(klass));
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>"U", "L", "F" for Update / LateUpdate / FixedUpdate (the lookup also searches parent classes).</summary>
    private static string PerFrame(IntPtr klass)
    {
        string s = "";
        if (il2cpp_class_get_method_from_name(klass, "Update", 0) != IntPtr.Zero) s += "U";
        if (il2cpp_class_get_method_from_name(klass, "LateUpdate", 0) != IntPtr.Zero) s += "L";
        if (il2cpp_class_get_method_from_name(klass, "FixedUpdate", 0) != IntPtr.Zero) s += "F";
        return s;
    }

    [DllImport("GameAssembly")] private static extern long il2cpp_gc_get_used_size();
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_object_get_class(IntPtr obj);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_get_name(IntPtr klass);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_get_namespace(IntPtr klass);
    [DllImport("GameAssembly")] private static extern IntPtr il2cpp_class_get_method_from_name(IntPtr klass, string name, int argsCount);
    [DllImport("GameAssembly")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc, [MarshalAs(UnmanagedType.I1)] bool checkInterfaces);
}
