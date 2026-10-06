using BepInEx.Configuration;
using NivalisPerformanceFix.Native;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// The sky helper camera (MainCamera/SkyboxCamera, sky only, into a 640x360 texture) does a full cull + render
/// every frame. We render it every N frames instead (every 2 frames looks identical; at 30 its overlay layer
/// visibly stuttered). Measured with the former snow-footprint throttle: +8% FPS, 1% lows +47%.
///
/// The snow-footprint camera is left alone: since the 2026-10-06 game update, FootstepsRenderTexture.Update sets
/// its enabled state every frame itself (off when no footprint particles, else at most once per refreshInterval),
/// and toggling it too would fight the game (re-enabling it when it is not snowing).
///
/// The sky camera is only switched back on if we switched it off: when the game turns it off itself, it stays off.
/// Feature off: the camera is given back on (if we had it off) and left alone.
/// </summary>
internal sealed class CameraThrottle : Feature
{
    public override string Name => "Offscreen camera throttle";
    protected override string Section => "Cameras";
    protected override string Description =>
        "Render the sky helper camera every few frames instead of every frame.";

    private ConfigEntry<int> skyInterval;

    // cached because a disabled camera disappears from Camera.allCameras; searched again when lost (scene change)
    private Camera skyCam;
    private int searchCooldown, frame;
    private bool skippedByUs; // we switched it off for the current frame

    protected override void BindSettings(ConfigFile config)
    {
        skyInterval = config.Bind(Section, "SkyInterval", 2,
            new ConfigDescription("Render the sky camera every N frames (1 = every frame).",
                new AcceptableValueRange<int>(1, 10)));
    }

    protected override string TryInstall() => null; // plain Unity API, nothing to locate

    public override void Tick()
    {
        if (!Direct.Alive(skyCam) && --searchCooldown <= 0)
        {
            skyCam = null;
            searchCooldown = 60;
            foreach (Camera c in Camera.allCameras)
                if (c != null && c.name == "SkyboxCamera") { skyCam = c; skippedByUs = false; break; }
        }
        frame++;
        if (!Direct.Alive(skyCam)) return;
        bool on = Direct.Enabled(skyCam);
        if (!on && !skippedByUs) return; // the game switched it off: leave it off
        bool want = !Active || skyInterval.Value <= 1 || frame % skyInterval.Value == 0;
        if (on != want) skyCam.enabled = want;
        skippedByUs = !want;
    }
}
