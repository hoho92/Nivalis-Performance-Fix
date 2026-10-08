using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using Cinemachine;
using HarmonyLib;
using Nivalis;
using NivalisPerformanceFix.Features;
using NivalisPerformanceFix.Native;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace NivalisPerformanceFix.Dev;

/// <summary>
/// Developer tool: route benchmark, the same walk replayed under the same conditions so builds can be compared.
///  * RecordKey (Home): records the player's position and look (~20 per second) until pressed again, into
///    bench/routes/&lt;zone&gt;/&lt;time&gt;.json with the zone (world scene) and the save last loaded;
///  * PlayKey (End): replays the current zone's routes here (no save loaded), RouteRuns times; again = cancel;
///  * bench/request.json present at startup: autonomous campaign — from the title screen, loads each route's save,
///    pins time / weather, replays, writes bench/results/&lt;date&gt;.md + .json, renames the request *.done and
///    quits if asked. Request: {"routes":["all" | "zone" | "zone/name"], "runs":3, "target":"", "hour":18,
///    "weather":"clear", "quit":true, "pix":{"run":1,"at":5,"seconds":20}}; target (BenchTarget syntax) = runs
///    alternate off / on in ABBA order; pix = timing capture during that run (scheduled task NivalisPixCapture).
///    The gameplay clock is set to "hour" and paused for the whole route. "clock":{"from":6,"hours":18,"rate":60}
///    = clock run: no replay, the player waits at the route start while the clock advances (hitches carry the hour).
/// Replay: the player is put in NoClip at the route start, the world settles, then each frame moves the player
/// to the recorded position for that time and sets the look; a route recorded in another zone is refused.
/// Files are under BepInEx/NivalisPerformanceFix/bench.
/// </summary>
internal sealed class RouteBench
{
    private const float HitchMs = 25f;
    private const float SampleInterval = 0.05f, RunSettleSeconds = 3f, TitleWaitSeconds = 3f, LoadTimeout = 120f;

    private sealed class Route
    {
        public string Zone { get; set; }
        public string Save { get; set; }
        public string Recorded { get; set; }
        /// <summary>t, x, y, z, look horizontal, look vertical.</summary>
        public List<float[]> Samples { get; set; } = new();
        [System.Text.Json.Serialization.JsonIgnore] public string Name;
    }

    private sealed class Request
    {
        public List<string> Routes { get; set; } = new() { "all" };
        public int Runs { get; set; } = 3;
        public string Target { get; set; } = "";
        public float Hour { get; set; } = -1;
        public string Weather { get; set; } = "";
        public bool Quit { get; set; }
        /// <summary>Optional PIX timing capture during one run (needs the NivalisPixCapture task, tools/pixauto).</summary>
        public PixRequest Pix { get; set; }
        /// <summary>Screenshots taken after the runs of a route in their zone (bench/shots/&lt;date&gt;-&lt;name&gt;.png).</summary>
        public List<ShotRequest> Shots { get; set; } = new();
        /// <summary>Clock run instead of the route replay: the player stays at the route start while the gameplay
        /// clock goes from From over Hours game hours at Rate game seconds per real second (hitches carry the clock).</summary>
        public ClockRequest Clock { get; set; }
        /// <summary>Logs the HUD canvases (HudProbe) once, at the start of the first run.</summary>
        public bool Probe { get; set; }
        /// <summary>Records LayoutLog (15 s: layout rebuilds, marks, redraws) from the start of the first run.</summary>
        public bool LayoutLog { get; set; }
        /// <summary>Object names whose components HudProbe.Inspect logs at the start of the first run.</summary>
        public List<string> Inspect { get; set; } = new();
    }

    private sealed class ClockRequest
    {
        public float From { get; set; } = 8;
        public float Hours { get; set; } = 4;
        public float Rate { get; set; } = 120;
        /// <summary>Per zone: route time (s) whose recorded spot the player waits at (e.g. the route's busiest
        /// second); zones not listed wait at the route start.</summary>
        public Dictionary<string, float> At { get; set; } = new();
    }

    /// <summary>A screenshot after a zone's runs: the player (NoClip) at Pos looking Look (horizontal, vertical degrees).</summary>
    private sealed class ShotRequest
    {
        public string Name { get; set; } = "shot";
        public string Zone { get; set; } = "";
        public float[] Pos { get; set; }
        public float[] Look { get; set; } = { 0, 0 };
    }

    private sealed class PixRequest
    {
        public int Run { get; set; } = 1;
        public float At { get; set; } = 5;
        public int Seconds { get; set; } = 20;
    }

    private sealed class RunResult
    {
        public string Route { get; set; }
        public string Variant { get; set; }
        public int Run { get; set; }
        public double AvgFps { get; set; }
        public double Low1 { get; set; }
        public double P99Ms { get; set; }
        public double MaxMs { get; set; }
        public int Over33 { get; set; }
        public int Frames { get; set; }
        public double PathErrorMax { get; set; }
        public string Split { get; set; }
        public List<int> FpsPerSecond { get; set; }
        public List<Hitch> Hitches { get; set; }
        public string PixCapture { get; set; }
        public string Clock { get; set; }
    }

    /// <summary>A slow frame: when on the route, how long, where, and what the frame split says.</summary>
    private sealed class Hitch
    {
        public float T { get; set; }
        public float Ms { get; set; }
        public float[] Pos { get; set; }
        public float CrowdMs { get; set; }
        public float RenderMs { get; set; }
        public bool Gc { get; set; }
        public string Clock { get; set; }
    }

    private static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    private readonly DevTools dev;
    private ConfigEntry<string> recordKeyName, playKeyName, routeTarget;
    private ConfigEntry<int> routeRuns;
    private ConfigEntry<float> settleSeconds;
    private Key recordKey, playKey;
    private string folder;
    private static string lastSave;
    private string saveZone; // zone the last loaded save arrived in (a route needs a save made in its own zone)
    private bool wasLoading;
    private static bool saveLoading; // set by a save load: the next loading end is that save's arrival

    public RouteBench(DevTools dev) => this.dev = dev;

    public void Bind(ConfigFile config)
    {
        const string s = "RouteBench";
        recordKeyName = config.Bind(s, "RecordKey", "Home", "Key that starts / stops recording a route (position + look).");
        playKeyName = config.Bind(s, "PlayKey", "End", "Key that replays the current zone's routes (again = cancel).");
        routeRuns = config.Bind(s, "Runs", 3, "Replays per route with PlayKey (rounds of off/on when Target is set).");
        routeTarget = config.Bind(s, "Target", "",
            "Empty = replay with the current settings; else what the runs switch off / on (BenchTarget syntax).");
        settleSeconds = config.Bind(s, "SettleSeconds", 15f,
            "Seconds waited at the route start before the first run (the world fills after loading / teleporting).");
    }

    public void Start()
    {
        Enum.TryParse(recordKeyName.Value?.Trim(), true, out recordKey);
        Enum.TryParse(playKeyName.Value?.Trim(), true, out playKey);
        folder = Path.Combine(Paths.BepInExRootPath, "NivalisPerformanceFix", "bench");
        ListTools.WatchLoadingScreen();
        try
        {
            new Harmony(Plugin.Guid + ".routebench").Patch(
                AccessTools.Method(typeof(SerializationManager), nameof(SerializationManager.Load), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(RouteBench), nameof(LoadPrefix)));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"RouteBench: save name not followed ({e.Message})"); }
        Plugin.Log.LogInfo($"Route benchmark: {recordKey} record, {playKey} replay, files in BepInEx/NivalisPerformanceFix/bench");

        string requestPath = Path.Combine(folder, "request.json");
        if (!File.Exists(requestPath)) return;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), json) ?? new Request();
            requestFile = requestPath;
            Application.runInBackground = true; // launched by a script: keep running without focus
            Plugin.Log.LogMessage($"Route benchmark request found: {string.Join(", ", request.Routes)}, {request.Runs} run(s)" +
                                  (request.Target.Length > 0 ? $", target {request.Target}" : ""));
            Begin(SelectRoutes(request.Routes), fromTitle: true);
        }
        catch (Exception e) { Plugin.Log.LogError($"RouteBench: bad request.json: {e.Message}"); }
    }

    private static void LoadPrefix(string saveName)
    {
        lastSave = saveName;
        saveLoading = true;
    }

    // ---- steps

    private enum Step { Idle, Recording, WaitTitle, WaitLoadStart, WaitLoadEnd, Settle, Run, Shot, WaitPix }

    private Step step = Step.Idle;
    private float stepTime;
    private Request request;
    private string requestFile;
    private List<Route> queue;
    private int routeIndex;
    private Route route;
    private List<bool?> plan; // per run: null = settings as they are, false = target off, true = target on
    private int runIndex;
    private ConfigEntryBase target;
    private object targetSaved, targetOn, targetOff;
    private readonly List<RunResult> results = new();
    private DateTime started;

    public void Update(Keyboard kb, float dt)
    {
        if (DevTools.Pressed(kb, recordKey))
        {
            if (step == Step.Recording) StopRecording();
            else if (step == Step.Idle) StartRecording();
        }
        if (DevTools.Pressed(kb, playKey))
        {
            if (step != Step.Idle && step != Step.Recording) Finish("cancelled");
            else if (step == Step.Idle)
            {
                string zone = Zone();
                request = new Request { Runs = routeRuns.Value, Target = routeTarget.Value?.Trim() ?? "" };
                requestFile = null;
                if (zone == null) Plugin.Log.LogMessage("Route benchmark: not in a world zone");
                else Begin(SelectRoutes(new List<string> { zone }), fromTitle: false);
            }
        }
        if (wasLoading && !ListTools.Loading && saveLoading) { saveZone = Zone(); saveLoading = false; }
        wasLoading = ListTools.Loading;
        if (step == Step.Idle) return;
        stepTime += dt;
        try { Tick(dt); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"RouteBench: {e}");
            Finish("failed: " + e.Message);
        }
    }

    private void Go(Step s)
    {
        step = s;
        stepTime = 0;
    }

    private void Tick(float dt)
    {
        switch (step)
        {
            case Step.Recording:
                RecordStep();
                break;
            case Step.WaitTitle:
                if (GameObject.Find("P_MainMenu(Clone)") is null) stepTime = 0; // title not shown yet
                else if (stepTime > TitleWaitSeconds) NextRoute();
                break;
            case Step.WaitLoadStart:
                if (ListTools.Loading) Go(Step.WaitLoadEnd);
                else if (stepTime > LoadTimeout) Finish($"save '{route.Save}' did not load");
                break;
            case Step.WaitLoadEnd:
                if (!ListTools.Loading && !ListTools.Held && Controller() is not null) StartRoute();
                else if (stepTime > LoadTimeout) Finish("loading did not end");
                break;
            case Step.Settle:
                if (ListTools.Loading || ListTools.Held) { stepTime = 0; break; } // held loading screen: the world is not shown yet
                Hold(StartSample());
                if (stepTime > (runIndex == 0 ? settleSeconds.Value : RunSettleSeconds)) StartRun();
                break;
            case Step.Run:
                RunStep(dt);
                break;
            case Step.Shot:
                ShotStep();
                break;
            case Step.WaitPix:
                if (PixEnded() || stepTime > pixWaitSeconds) { pixPending = null; NextRoute(); }
                break;
        }
    }

    // ---- screenshots

    private const float ShotSettleSeconds = 3f, ShotWriteSeconds = 1f;
    private readonly Queue<ShotRequest> shots = new();
    private bool shotTaken;

    /// <summary>After a route's runs: queues the request's shots for that zone (each shot once per campaign).</summary>
    private bool StartShots()
    {
        foreach (ShotRequest s in request.Shots.Where(s => s.Pos is { Length: 3 } &&
                     s.Zone.Equals(route.Zone, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            shots.Enqueue(s);
            request.Shots.Remove(s);
        }
        if (shots.Count == 0) return false;
        shotTaken = false;
        Go(Step.Shot);
        return true;
    }

    private void ShotStep()
    {
        ShotRequest s = shots.Peek();
        float[] look = s.Look is { Length: 2 } ? s.Look : new float[] { 0, 0 };
        Hold(new[] { 0, s.Pos[0], s.Pos[1], s.Pos[2], look[0], look[1] });
        if (!shotTaken && stepTime > ShotSettleSeconds)
        {
            string dir = Path.Combine(folder, "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{s.Name}.png");
            ScreenCapture.CaptureScreenshot(path); // written at the end of the frame
            Plugin.Log.LogMessage($"Route benchmark: screenshot {s.Name} -> shots/{Path.GetFileName(path)}");
            shotTaken = true;
            stepTime = 0;
        }
        if (!shotTaken || stepTime < ShotWriteSeconds) return;
        shots.Dequeue();
        shotTaken = false;
        stepTime = 0;
        if (shots.Count == 0) NextRoute();
    }

    // ---- recording

    private Route recording;
    private float recordTime, nextSample;

    private void StartRecording()
    {
        string zone = Zone();
        if (zone == null || Controller() is null) { Plugin.Log.LogMessage("Route recording: not in a world zone"); return; }
        recording = new Route { Zone = zone, Save = lastSave ?? "", Recorded = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        if (saveZone != null && !saveZone.Equals(zone, StringComparison.OrdinalIgnoreCase))
            Plugin.Log.LogWarning($"Route recording: save '{lastSave}' arrives in {saveZone}, not {zone}: the autonomous run " +
                                  "will skip this route (save here, load that save, then record)");
        recordTime = 0;
        nextSample = 0;
        Go(Step.Recording);
        Plugin.Log.LogMessage($"Route recording in {zone} (save '{recording.Save}'): {recordKey} again to stop");
    }

    private void RecordStep()
    {
        if (Time.timeScale == 0) return; // paused: the route goes on where the game resumes
        PlayerCharacterController c = Controller();
        if (c is null) { StopRecording(); return; }
        recordTime += Direct.UnscaledDeltaTime;
        if (recordTime < nextSample) return;
        nextSample = recordTime + SampleInterval;
        Vector3 p = c.transform.position;
        CinemachinePOV pov = c.FirstPersonPOV;
        recording.Samples.Add(new[] { recordTime, p.x, p.y, p.z, pov.m_HorizontalAxis.Value, pov.m_VerticalAxis.Value });
    }

    private void StopRecording()
    {
        Go(Step.Idle);
        if (recording.Samples.Count < 40) { Plugin.Log.LogMessage("Route recording too short (< 2 s): not kept"); return; }
        string dir = Path.Combine(folder, "routes", recording.Zone);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(recording, json));
        Plugin.Log.LogMessage($"Route recorded: {recording.Samples[^1][0]:F0} s, {recording.Samples.Count} samples -> " +
                              $"routes/{recording.Zone}/{Path.GetFileName(path)}" +
                              (recording.Save.Length == 0 ? " (save unknown: set \"save\" in the file)" : ""));
    }

    // ---- campaign

    /// <summary>"all", a zone, or "zone/name"; sorted by zone, then save (fewer loadings), then name.</summary>
    private List<Route> SelectRoutes(List<string> wanted)
    {
        var list = new List<Route>();
        string root = Path.Combine(folder, "routes");
        if (!Directory.Exists(root)) return list;
        foreach (string file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            string zoneDir = Path.GetFileName(Path.GetDirectoryName(file));
            string name = zoneDir + "/" + Path.GetFileNameWithoutExtension(file);
            if (!wanted.Any(w => w.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                 w.Equals(zoneDir, StringComparison.OrdinalIgnoreCase) ||
                                 w.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                Route r = JsonSerializer.Deserialize<Route>(File.ReadAllText(file), json);
                if (r?.Samples is { Count: >= 2 }) { r.Name = name; list.Add(r); }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"RouteBench: {name} unreadable: {e.Message}"); }
        }
        return list.OrderBy(r => r.Zone).ThenBy(r => r.Save).ThenBy(r => r.Name).ToList();
    }

    private void Begin(List<Route> routes, bool fromTitle)
    {
        if (routes.Count == 0) { Finish("no route found"); return; }
        if (request.Clock is { } c && (c.From < 8 || c.From + c.Hours > 25)) // curfew 2h-8h: cameras, forced end of day
        { Finish("clock run outside 8h-1h (curfew from 1h warning / 2h, ends 8h)"); return; }
        queue = routes;
        routeIndex = -1;
        pixPending = null;
        results.Clear();
        started = DateTime.Now;
        target = null;
        if (request.Target.Length > 0)
        {
            target = dev.FindTarget(request.Target);
            if (target == null) { Finish($"unknown target '{request.Target}'"); return; }
            targetSaved = target.BoxedValue;
            if (target.SettingType == typeof(bool)) { targetOn = true; targetOff = false; }
            else { targetOn = targetSaved; targetOff = Convert.ChangeType(0, target.SettingType); }
            target.ConfigFile.SaveOnConfigSet = false; // switched in memory only
        }
        plan = new List<bool?>();
        for (int r = 0; r < Math.Max(1, request.Runs); r++)
            if (target == null) plan.Add(null);
            else plan.AddRange(r % 2 == 0 ? new bool?[] { false, true } : new bool?[] { true, false });
        Plugin.Log.LogMessage($"Route benchmark: {routes.Count} route(s) x {plan.Count} run(s)");
        if (fromTitle) Go(Step.WaitTitle); else NextRoute();
    }

    private void NextRoute()
    {
        if (pixPending != null) { Go(Step.WaitPix); return; } // a load or quit would cut the capture short
        if (++routeIndex >= queue.Count) { Finish(null); return; }
        route = queue[routeIndex];
        runIndex = 0;
        string zone = Zone();
        bool here = zone != null && zone.Equals(route.Zone, StringComparison.OrdinalIgnoreCase);
        if (requestFile == null || (here && route.Save == lastSave)) // replay here (PlayKey, or same save already loaded)
        {
            if (!here) { Skip($"recorded in {route.Zone}, not here ({zone})"); return; }
            StartRoute();
            return;
        }
        if (route.Save.Length == 0) { Skip("no save in the route file"); return; }
        SerializationManager sm = Singleton<SerializationManager>.Instance;
        if (sm is null || !sm.DoesSaveExist(route.Save)) { Skip($"save '{route.Save}' not found"); return; }
        Plugin.Log.LogMessage($"Route benchmark: loading '{route.Save}' for {route.Name}");
        sm.Load(route.Save);
        loadWatch.Restart();
        Go(Step.WaitLoadStart);
    }

    private void Skip(string why)
    {
        Plugin.Log.LogWarning($"Route {route.Name} skipped: {why}");
        dev.Report($"Route {route.Name} skipped: {why}");
        NextRoute();
    }

    private readonly System.Diagnostics.Stopwatch loadWatch = new();

    private void StartRoute()
    {
        if (loadWatch.IsRunning)
        {
            loadWatch.Stop();
            Plugin.Log.LogMessage($"Route benchmark: save '{route.Save}' loaded in {loadWatch.Elapsed.TotalSeconds:F2} s (load call to world shown)");
        }
        string zone = Zone();
        if (zone == null || !zone.Equals(route.Zone, StringComparison.OrdinalIgnoreCase))
        {
            Skip($"recorded in {route.Zone}, loaded zone is {zone}");
            return;
        }
        float lightHour = request.Clock != null ? -1 : request.Hour; // clock run: the light follows the clock
        if (lightHour >= 0 || request.Weather.Length > 0)
            if (!WorldPin.Pin(lightHour, request.Weather)) Plugin.Log.LogWarning($"RouteBench: unknown weather '{request.Weather}'");
        FreezeClock();
        Plugin.Log.LogMessage($"Route {route.Name}: settling {settleSeconds.Value:F0} s at the start");
        Go(Step.Settle);
    }

    // ---- gameplay clock

    private OverrideableBool.OverrideLock clockLock;

    /// <summary>
    /// Sets the gameplay clock (NPC schedules, curfew, economy ticks) to the request's hour with the game's dev
    /// option Dev_SetTime, then pauses it through the game's own pause lock, so every run of a route sees the same
    /// hour whatever time the save was made at (WorldPin's hour only pins the light cycle). Re-taken per route (a
    /// load builds a new TimeOfDayManager).
    /// </summary>
    private void FreezeClock()
    {
        ReleaseClock();
        SetClock();
        try { clockLock = TimeOfDayManager.Pause(new Il2CppSystem.Object()); }
        catch (Exception e) { Plugin.Log.LogWarning($"RouteBench: gameplay clock not paused ({e.Message})"); }
        Plugin.Log.LogMessage($"Route {route.Name}: gameplay clock paused at {Clock()}");
    }

    /// <summary>Gameplay clock to the request's hour (clock run: its start hour), through the game's dev option.</summary>
    private void SetClock()
    {
        float hour = request.Clock?.From ?? request.Hour;
        if (hour < 0) return;
        try
        {
            AccessTools.Method(typeof(TimeOfDayManager), "Dev_SetTime")
                .Invoke(Singleton<TimeOfDayManager>.Instance, new object[] { Mathf.Clamp01(hour / 24f), false });
        }
        catch (Exception e) { Plugin.Log.LogWarning($"RouteBench: gameplay clock not set ({e.Message})"); }
    }

    private void ReleaseClock()
    {
        try { clockLock?.Release(); } catch { } // the lock of an unloaded manager
        clockLock = null;
    }

    private static string Clock() => $"{TimeOfDayManager.ClockHour:D2}:{TimeOfDayManager.ClockMinute:D2}";

    // ---- replay

    private readonly List<float> frames = new(16384);
    private readonly List<int> perSecond = new();
    private readonly List<Hitch> hitches = new();
    private readonly FrameSplit.Window split = new();
    private float runTime, secondTime;
    private int secondFrames, sample;
    private double pathError;
    private string pixName;
    private int gcBefore, gcLast;
    private string pixPending; // capture log of a capture still running (or being written)
    private float pixWaitSeconds;

    /// <summary>Keeps the player at a sample (NoClip: no gravity nor collisions on the way).</summary>
    private static void Hold(float[] s) => Place(s, out _);

    private static void Place(float[] s, out float error)
    {
        error = 0;
        PlayerCharacterController c = Controller();
        if (c is null) return;
        if (c.State != PlayerCharacterController.ControllerState.NoClip) c.State = PlayerCharacterController.ControllerState.NoClip;
        var target = new Vector3(s[1], s[2], s[3]);
        Vector3 delta = target - c.transform.position;
        error = delta.magnitude;
        if (error > 5f) c.TeleportPlayer(target, Quaternion.Euler(0, s[4], 0), false);
        else if (error > 1e-4f) c.Move(delta);
        CinemachinePOV pov = c.FirstPersonPOV;
        if (pov is null) return;
        AxisState h = pov.m_HorizontalAxis, v = pov.m_VerticalAxis;
        h.Value = s[4];
        v.Value = s[5];
        pov.m_HorizontalAxis = h;
        pov.m_VerticalAxis = v;
    }

    private void StartRun()
    {
        if (target != null) target.BoxedValue = plan[runIndex] == true ? targetOn : targetOff;
        frames.Clear(); perSecond.Clear(); split.Reset(); hitches.Clear();
        runTime = secondTime = 0;
        secondFrames = 0;
        Mark("start");
        sample = 0;
        pathError = 0;
        pixName = null;
        gcBefore = gcLast = Direct.GcCollectionCount(0);
        if (request.Probe && runIndex == 0) HudProbe.Dump();
        if (request.LayoutLog && runIndex == 0) Dev.LayoutLog.Toggle();
        if (request.Inspect.Count > 0 && runIndex == 0) HudProbe.Inspect(request.Inspect);
        Go(Step.Run);
    }

    /// <summary>Where a run starts: the route's first sample, or for a clock run the sample at the zone's "at" time.</summary>
    private float[] StartSample()
    {
        List<float[]> s = route.Samples;
        if (request.Clock?.At is not { } at || !at.TryGetValue(route.Zone, out float t)) return s[0];
        return s.FirstOrDefault(x => x[0] >= t) ?? s[^1];
    }

    private void RunStep(float dt)
    {
        runTime += dt;
        frames.Add(dt);
        int gc = Direct.GcCollectionCount(0);
        int gcNear = gc - gcBefore; // this frame or the previous one (a GC finishes at the start of a frame)
        gcBefore = gcLast; gcLast = gc;
        split.Add(dt);
        secondFrames++;
        if ((secondTime += dt) >= 1f) { perSecond.Add(secondFrames); secondFrames = 0; secondTime -= 1f; }
        if (dt * 1000f > HitchMs && Controller() is { } pc) // the player is still where that frame was made
        {
            Vector3 p = pc.transform.position;
            hitches.Add(new Hitch
            {
                T = (float)Math.Round(runTime, 2), Ms = (float)Math.Round(dt * 1000f, 1),
                Pos = new[] { (float)Math.Round(p.x, 1), (float)Math.Round(p.y, 1), (float)Math.Round(p.z, 1) },
                CrowdMs = (float)Math.Round(FrameSplit.CrowdMs, 1), RenderMs = (float)Math.Round(FrameSplit.RenderMs, 1),
                Gc = gcNear > 0,
                Clock = request.Clock != null ? Clock() : null,
            });
        }

        if (pixName == null && request.Pix is { } pix && pix.Run == runIndex + 1 && runTime >= pix.At) StartPix(pix);

        List<float[]> s = route.Samples;
        if (request.Clock is { } clock)
        {
            Singleton<TimeOfDayManager>.Instance?.AddTime(dt * clock.Rate); // the clock stays paused: only we move it
            Hold(StartSample());
            if (runTime >= clock.Hours * 3600f / Math.Max(1f, clock.Rate)) EndRun();
            return;
        }
        while (sample + 1 < s.Count && s[sample + 1][0] <= runTime) sample++;
        if (sample + 1 >= s.Count) { EndRun(); return; }
        float[] a = s[sample], b = s[sample + 1];
        float k = Mathf.Clamp01((runTime - a[0]) / Math.Max(1e-4f, b[0] - a[0]));
        var at = new float[6];
        at[0] = runTime;
        for (int i = 1; i <= 3; i++) at[i] = Mathf.Lerp(a[i], b[i], k);
        at[4] = Mathf.LerpAngle(a[4], b[4], k);
        at[5] = Mathf.Lerp(a[5], b[5], k);
        Place(at, out float error);
        if (frames.Count > 2) pathError = Math.Max(pathError, error); // first frames: still at the settle point
    }

    private void EndRun()
    {
        Mark("end");
        var sorted = frames.OrderBy(x => x).ToList();
        var (fps, low) = DevTools.Stats(frames);
        var r = new RunResult
        {
            Route = route.Name,
            Variant = plan[runIndex] switch { null => "as is", true => "on", false => "off" },
            Run = runIndex + 1,
            AvgFps = Math.Round(fps, 1), Low1 = Math.Round(low, 1),
            P99Ms = Math.Round(sorted[(int)(sorted.Count * 0.99)] * 1000, 1),
            MaxMs = Math.Round(sorted[^1] * 1000, 1),
            Over33 = frames.Count(x => x > 0.033f), Frames = frames.Count,
            PathErrorMax = Math.Round(pathError, 2),
            Split = split.ToString(),
            FpsPerSecond = new List<int>(perSecond),
            Hitches = new List<Hitch>(hitches),
            PixCapture = pixName,
            Clock = Clock(),
        };
        results.Add(r);
        Plugin.Log.LogMessage($"Route {r.Route} run {r.Run}/{plan.Count} ({r.Variant}): {r.AvgFps} FPS, 1% low {r.Low1}, " +
                              $"p99 {r.P99Ms} ms, max {r.MaxMs} ms, >33 ms {r.Over33}, path error max {r.PathErrorMax} m");
        if (++runIndex < plan.Count) // back to the start, short settle
        {
            if (request.Clock != null) SetClock(); // clock run: the next run replays the same hours
            Go(Step.Settle);
        }
        else if (!StartShots()) NextRoute();
    }

    /// <summary>
    /// Asks the scheduled task NivalisPixCapture (installed once by the user, elevated) for a timing capture of the
    /// game: a request file with name + seconds, then "schtasks /run". The capture begins a few seconds later
    /// (pixtool attach); the .wpix lands in the task's captures folder.
    /// </summary>
    private void StartPix(PixRequest pix)
    {
        pixName = $"{route.Zone}-run{runIndex + 1}-{DateTime.Now:HHmmss}";
        try
        {
            string requests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NivalisPix", "requests");
            File.WriteAllText(Path.Combine(requests, "request.txt"), $"name={pixName}\nseconds={pix.Seconds}\n");
            var start = new System.Diagnostics.ProcessStartInfo("schtasks", "/run /tn NivalisPixCapture")
                { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(start);
            pixPending = Path.Combine(Path.GetDirectoryName(requests), "captures", pixName + ".log");
            pixWaitSeconds = pix.Seconds + 90f; // attach + capture + save, in case the task never writes "end"
            Mark($"PIX capture {pixName} requested ({pix.Seconds} s)");
            Plugin.Log.LogMessage($"Route benchmark: PIX capture {pixName} requested at t {runTime:F1} s");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"RouteBench: PIX capture not started ({e.Message})");
            pixName = "failed: " + e.Message;
        }
    }

    /// <summary>The task's capture log ends with an "end <time> exit <code>" line once pixtool has saved the file.</summary>
    private bool PixEnded()
    {
        try { return File.Exists(pixPending) && File.ReadAllLines(pixPending).Any(l => l.StartsWith("end ")); }
        catch (IOException) { return false; } // still being written
    }

    /// <summary>Marks the run in the game's Player.log, so its warnings can be counted per run / variant.</summary>
    private void Mark(string what) =>
        UnityEngine.Debug.Log($"[RouteBench] {route.Name} run {runIndex + 1} " +
                              $"({plan[runIndex] switch { null => "as is", true => "on", false => "off" }}) {what}");

    // ---- end

    private void Finish(string why)
    {
        Step was = step;
        step = Step.Idle;
        if (target != null) { target.BoxedValue = targetSaved; target.ConfigFile.SaveOnConfigSet = true; target = null; }
        if (was is Step.Settle or Step.Run or Step.Shot && Controller() is { } c) c.State = PlayerCharacterController.ControllerState.Normal;
        WorldPin.Release();
        ReleaseClock();
        if (why != null) dev.Report("Route benchmark " + why);
        if (results.Count > 0) WriteResults(why);
        results.Clear();
        if (requestFile == null) return;
        try { File.Move(requestFile, requestFile + ".done", true); } catch { }
        requestFile = null;
        if (request.Quit) Application.Quit();
    }

    private void WriteResults(string why)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Route benchmark {started:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"Runs {request.Runs}, target {(request.Target.Length > 0 ? request.Target : "none")}, hour {(request.Hour >= 0 ? request.Hour.ToString("F0") : "game")}, " +
                      $"weather {(request.Weather.Length > 0 ? request.Weather : "game")}, job workers {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount}" +
                      (why != null ? $" — stopped: {why}" : ""));
        foreach (var group in results.GroupBy(r => r.Route))
        {
            sb.AppendLine();
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| variant | runs | avg FPS | 1% low | p99 ms | max ms | >33 ms |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var v in group.GroupBy(r => r.Variant))
                sb.AppendLine($"| {v.Key} | {v.Count()} | {v.Average(r => r.AvgFps):F1} | {v.Average(r => r.Low1):F1} | " +
                              $"{v.Average(r => r.P99Ms):F1} | {v.Max(r => r.MaxMs):F1} | {v.Sum(r => r.Over33)} |");
            sb.AppendLine();
            foreach (var r in group)
                sb.AppendLine($"- run {r.Run} ({r.Variant}): {r.AvgFps} FPS, 1% low {r.Low1}, p99 {r.P99Ms}, max {r.MaxMs}, " +
                              $"path error {r.PathErrorMax} m{(r.Split.Length > 0 ? ", " + r.Split : "")}" +
                              (r.PixCapture != null ? $", PIX capture {r.PixCapture}" : "") + $", game clock {r.Clock}");
            foreach (var r in group.Where(r => r.Hitches.Count > 0))
                sb.AppendLine($"- run {r.Run} frames > {HitchMs:F0} ms: " + string.Join(", ", r.Hitches.Take(30).Select(h =>
                    $"t {h.T:F1} s {h.Ms:F0} ms at ({h.Pos[0]:F0} {h.Pos[1]:F0} {h.Pos[2]:F0}) crowd {h.CrowdMs:F1} render {h.RenderMs:F1}{(h.Gc ? " GC" : "")}{(h.Clock != null ? " clock " + h.Clock : "")}")) +
                    (r.Hitches.Count > 30 ? $" … ({r.Hitches.Count})" : ""));
        }
        string dir = Path.Combine(folder, "results");
        Directory.CreateDirectory(dir);
        string name = started.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(dir, name + ".md"), sb.ToString());
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(results, new JsonSerializerOptions(json) { WriteIndented = true }));
        dev.Report(sb.ToString());
        Plugin.Log.LogMessage($"Route benchmark results: bench/results/{name}.md");
    }

    // ---- game access

    private static PlayerCharacterController Controller()
    {
        try
        {
            PlayerCharacterController c = Singleton<PlayerManager>.Instance?.LocalPlayer?.Character?.Controller;
            return c is not null && Direct.Alive(c) ? c : null;
        }
        catch { return null; }
    }

    /// <summary>The loaded world scene ("1_Lowtown" .. "18_Spire"), null on the title screen / between zones.</summary>
    private static string Zone()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            string n = SceneManager.GetSceneAt(i).name;
            if (n.Length > 2 && char.IsDigit(n[0]) && n.Contains('_') && !n.EndsWith("Credits")) return n;
        }
        return null;
    }
}
