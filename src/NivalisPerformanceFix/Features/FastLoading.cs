using BepInEx.Configuration;
using UnityEngine;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// Launch PIX capture (2026-10-08): before the first frame the main thread mostly waits
/// (PreloadManager.WaitForAllAsyncOperationsToComplete, Texture.VerifyFileTextureUploadCompletion) while the job
/// workers decompress crunched textures at ~7 % each: the async upload pipeline, throttled by
/// QualitySettings.asyncUploadTimeSlice (ms per step) and asyncUploadBufferSize (MB ring buffer), feeds them slowly.
/// While the game loads (launch until the title, then every loading screen) both are raised; in play the game's
/// values come back, so textures arriving during play keep their small per-frame budget. The buffer is RAM held only
/// while loading; with UploadBufferMB = 0 (default) it follows the PC's memory: RAM / 128, between the game's value and
/// 128 MB (8 GB PC: 64 MB). Measured (4 launches ABBA): first frame and title screen both 0.7 s sooner.
/// </summary>
internal sealed class FastLoading : Feature
{
    public override string Name => "Faster loading";
    protected override string Section => "FastLoading";
    protected override string Description =>
        "Let Unity upload textures faster while the game is loading (launch and loading screens).";

    private ConfigEntry<int> timeSliceMs, bufferMb;
    private int gameSlice = -1, gameBuffer = -1;
    private bool fast, launchDone, titleSeen;

    protected override void BindSettings(ConfigFile config)
    {
        timeSliceMs = config.Bind(Section, "UploadTimeSliceMs", 25,
            new ConfigDescription("Texture upload time per step while loading (Unity default 2).", new AcceptableValueRange<int>(1, 100)));
        bufferMb = config.Bind(Section, "UploadBufferMB", 0,
            new ConfigDescription("Texture upload buffer while loading, in MB. 0 = from the PC's memory (RAM / 128, at most 128).",
                new AcceptableValueRange<int>(0, 512)));
    }

    protected override string TryInstall()
    {
        gameSlice = QualitySettings.asyncUploadTimeSlice;
        gameBuffer = QualitySettings.asyncUploadBufferSize;
        Plugin.Log.LogInfo($"Faster loading: game values time slice {gameSlice} ms, buffer {gameBuffer} MB; " +
                           $"while loading {timeSliceMs.Value} ms, {LoadingBuffer()} MB (RAM {SystemInfo.systemMemorySize} MB)");
        Apply(Enabled.Value && Plugin.MasterEnabled.Value); // plugin load: the first scene is still loading (not Installed yet)
        return null;
    }

    public override void Tick()
    {
        // launch over: the title was seen and no loading screen is shown. The title is looked for (GameObject.Find,
        // ~6 ms) only until seen: it used to be looked for until title AND no loading were true at once, which
        // could stay false and then ran every 30 frames in play (UI test 2026-10-08)
        if (!launchDone)
        {
            if (!titleSeen && ListTools.LoadingIndex < 2 && Time.frameCount % 30 == 0 && GameObject.Find("P_MainMenu(Clone)") != null) titleSeen = true;
            if ((titleSeen || ListTools.LoadingIndex >= 2) && !ListTools.Loading) launchDone = true; // or a save loaded since
        }
        Apply(Active && (!launchDone || ListTools.Loading));
    }

    protected override void SwitchedOff() => Apply(false);

    private int LoadingBuffer()
    {
        if (bufferMb.Value > 0) return bufferMb.Value;
        return Mathf.Clamp(SystemInfo.systemMemorySize / 128, gameBuffer, Mathf.Max(gameBuffer, 128));
    }

    private void Apply(bool want)
    {
        if (want == fast) return;
        fast = want;
        QualitySettings.asyncUploadTimeSlice = want ? timeSliceMs.Value : gameSlice;
        QualitySettings.asyncUploadBufferSize = want ? LoadingBuffer() : gameBuffer;
        NivalisPerformanceFix.Dev.BootLog.Mark($"texture upload {(want ? "fast" : "game values")}");
    }
}
