using BepInEx.Configuration;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Two offscreen cameras do a full cull + render every frame:
///  * MainCamera/SkyboxCamera: sky only, into a 640x360 texture;
///  * _Footprint_Snow_Camera: snow footprints, into a 3730x4096 texture.
/// We render them every N frames instead (the sky every 2 frames looks identical; at 30 its overlay layer visibly
/// stuttered). Measured: +8% FPS, 1% lows +47%.
/// </summary>
internal sealed class CameraThrottle : Feature
{
    public override string Name => "Offscreen camera throttle";
    protected override string Section => "Cameras";
    protected override string Description =>
        "Render the sky and snow-footprint helper cameras every few frames instead of every frame.";

    private ConfigEntry<int> skyInterval, snowInterval;

    // cached because a disabled camera disappears from Camera.allCameras; searched again when lost (scene change)
    private Camera skyCam, snowCam;
    private int searchCooldown, frame;

    protected override void BindSettings(ConfigFile config)
    {
        skyInterval = config.Bind(Section, "SkyInterval", 2,
            new ConfigDescription("Render the sky camera every N frames (1 = every frame).",
                new AcceptableValueRange<int>(1, 10)));
        snowInterval = config.Bind(Section, "SnowFootprintInterval", 4,
            new ConfigDescription("Render the snow-footprint camera every N frames (1 = every frame).",
                new AcceptableValueRange<int>(1, 30)));
    }

    protected override string TryInstall() => null; // plain Unity API, nothing to locate

    public override void Tick()
    {
        if ((skyCam == null || snowCam == null) && --searchCooldown <= 0)
        {
            searchCooldown = 60;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null) continue;
                if (c.name == "SkyboxCamera") skyCam = c;
                else if (c.name == "_Footprint_Snow_Camera") snowCam = c;
            }
        }
        frame++;
        bool on = Active;
        // offsets 0 / 1 so both rarely render on the same frame
        Set(skyCam, !on || Due(skyInterval.Value, 0));
        Set(snowCam, !on || Due(snowInterval.Value, 1));
    }

    private bool Due(int interval, int offset) => interval <= 1 || (frame + offset) % interval == 0;

    private static void Set(Camera c, bool enabled)
    {
        if (c != null && c.enabled != enabled) c.enabled = enabled;
    }
}
