using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Nivalis.DayNightCycle;
using Nivalis.Weather;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: pins the time of day (F2) and/or cycles a forced weather (F3) so performance
/// measurements can be compared across runs (day vs night, rain vs snow) without waiting for the game
/// to get there. While a pin is active it is re-applied from prefix/postfix hooks every frame (the
/// game's own updates cannot drift it); when released, the game resumes control from the pinned
/// state. Nothing is touched while inactive.
///
/// Scope: the VISUAL cycle (LightCycleManager) and the weather globals (WeatherManager). The gameplay
/// clock (curfew, NPC schedules) is NOT pinned — behavioural state may still follow the real clock.
/// </summary>
internal static class WorldPin
{
    private static ConfigEntry<string> timeKeyName, weatherKeyName;
    private static ConfigEntry<float> timePinHour;
    private static Key timeKey, weatherKey;

    private static bool timePinned;
    private static float pinnedTime; // LightCycleManager time, 0..1 over the day
    private static LightCycleManager lightCycle; // cached: FindObjectOfType per frame costs tens of ms on dense scenes

    private static int weatherStep = -1; // -1 = off (game control), 0 = clear, 1 = rain, 2 = snow, 3 = blizzard
    private static readonly string[] weatherNames = { "clear", "rain", "snow", "blizzard" };

    private static Harmony hooks;

    internal static void Bind(ConfigFile config)
    {
        const string s = "Developer";
        timeKeyName = config.Bind(s, "TimePinKey", "F2",
            "Key that pins the time of day (visual cycle) at TimePinHour. Press again to release.");
        timePinHour = config.Bind(s, "TimePinHour", 22f,
            new ConfigDescription("Hour the time of day is pinned at while TimePinKey is active.",
                new AcceptableValueRange<float>(0f, 24f)));
        weatherKeyName = config.Bind(s, "WeatherPinKey", "F3",
            "Key that cycles the weather pin: off (game control) -> clear -> rain -> snow -> blizzard -> off.");
    }

    internal static void Start()
    {
        Enum.TryParse(timeKeyName.Value?.Trim(), true, out timeKey);
        Enum.TryParse(weatherKeyName.Value?.Trim(), true, out weatherKey);
        hooks = new Harmony(Plugin.Guid + ".worldpin");
        try
        {
            MethodInfo update = AccessTools.DeclaredMethod(typeof(LightCycleManager), "Update")
                ?? throw new MissingMethodException("LightCycleManager.Update");
            hooks.Patch(update, prefix: new HarmonyMethod(typeof(WorldPin), nameof(TimePinPrefix)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"WorldPin: time pin unavailable: {e.Message}");
        }
        try
        {
            foreach (string name in new[] { "UpdateWeather", "UpdateAllWeather" })
            {
                MethodInfo m = AccessTools.DeclaredMethod(typeof(WeatherManager), name)
                    ?? throw new MissingMethodException("WeatherManager." + name);
                hooks.Patch(m, postfix: new HarmonyMethod(typeof(WorldPin), nameof(WeatherPinPostfix)));
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"WorldPin: weather pin unavailable: {e.Message}");
        }
        Plugin.Log.LogInfo($"World pins installed: {timeKey} = time ({timePinHour.Value:F0}h), {weatherKey} = weather cycle");
    }

    internal static void Update(Keyboard kb)
    {
        if (DevTools.Pressed(kb, timeKey))
        {
            timePinned = !timePinned;
            pinnedTime = Mathf.Clamp01(timePinHour.Value / 24f);
            Plugin.Log.LogMessage($"Time pin {(timePinned ? $"ON at {timePinHour.Value:F0}h" : "OFF (game resumes from pinned time)")}");
        }
        if (DevTools.Pressed(kb, weatherKey))
        {
            weatherStep = weatherStep >= weatherNames.Length - 1 ? -1 : weatherStep + 1;
            ApplyWeather();
            Plugin.Log.LogMessage(weatherStep < 0
                ? "Weather pin OFF (game control)"
                : $"Weather pin: {weatherNames[weatherStep]}");
        }
    }

    /// <summary>Pins from code (route benchmark): hour &lt; 0 = time left to the game, weather "" = left to the game,
    /// else clear / rain / snow / blizzard. Returns false for an unknown weather name.</summary>
    internal static bool Pin(float hour, string weather)
    {
        timePinned = hour >= 0;
        pinnedTime = Mathf.Clamp01(hour / 24f);
        weatherStep = string.IsNullOrWhiteSpace(weather) ? -1 : Array.IndexOf(weatherNames, weather.Trim().ToLowerInvariant());
        ApplyWeather();
        return string.IsNullOrWhiteSpace(weather) || weatherStep >= 0;
    }

    /// <summary>Releases both pins: the game resumes control.</summary>
    internal static void Release()
    {
        timePinned = false;
        weatherStep = -1;
    }

    /// <summary>Runs before LightCycleManager.Update: the frame computes its output with the pinned time.</summary>
    private static void TimePinPrefix()
    {
        if (!timePinned) return;
        try
        {
            if (lightCycle == null) lightCycle = UnityEngine.Object.FindObjectOfType<LightCycleManager>();
            if (lightCycle == null) return;
            if (Mathf.Abs(lightCycle.CurrentTime - pinnedTime) > 1e-4f) lightCycle.CurrentTime = pinnedTime;
        }
        catch { }
    }

    /// <summary>Runs after the weather recomputes from its preset: re-asserts the pinned globals.</summary>
    private static void WeatherPinPostfix()
    {
        if (weatherStep < 0) return;
        ApplyWeather();
    }

    private static void ApplyWeather()
    {
        try
        {
            WeatherManager w = WeatherManager.Instance;
            if (w == null) return;
            if (weatherStep < 0) return; // released: the preset logic takes over again
            switch (weatherStep)
            {
                case 0: // clear
                    w.GlobalRainAmount = 0; w.GlobalSnow = 0; w.GlobalBlizzard = 0;
                    w.GlobalWetness = 0; w.GlobalPuddles = 0; w.GlobalThunderAmount = 0;
                    break;
                case 1: // rain
                    w.GlobalRainAmount = 1; w.GlobalSnow = 0; w.GlobalBlizzard = 0;
                    w.GlobalWetness = 1; w.GlobalPuddles = 1; w.GlobalThunderAmount = 0;
                    break;
                case 2: // snow
                    w.GlobalRainAmount = 0; w.GlobalSnow = 1; w.GlobalBlizzard = 0;
                    w.GlobalWetness = 0; w.GlobalPuddles = 0; w.GlobalThunderAmount = 0;
                    break;
                case 3: // blizzard
                    w.GlobalRainAmount = 0; w.GlobalSnow = 1; w.GlobalBlizzard = 1;
                    w.GlobalWetness = 0; w.GlobalPuddles = 0; w.GlobalThunderAmount = 0;
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"WorldPin: weather apply failed: {e.Message}");
        }
    }
}
