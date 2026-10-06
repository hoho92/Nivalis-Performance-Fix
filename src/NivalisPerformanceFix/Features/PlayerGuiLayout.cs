using HarmonyLib;
using Nivalis;

namespace NivalisPerformanceFix.Features;

/// <summary>
/// PlayerCharacterController.OnGUI only reads keyboard shortcuts (Event.isKey / control / shift) and draws nothing,
/// but Unity still runs an IMGUI layout pass for it every frame, allocating GUILayoutGroup objects (~250 per second,
/// allocation tracker, Metro Hub). Its useGUILayout is switched off: key events still reach OnGUI, the layout pass
/// (and its garbage) goes away. The game's dev menu (DevOptionMenuRevised) really uses GUILayout and is left alone.
/// </summary>
internal sealed class PlayerGuiLayout : Feature
{
    public override string Name => "Player GUI layout";
    protected override string Section => "PlayerGui";
    protected override string Description =>
        "Skip the unused IMGUI layout pass of the player's keyboard shortcut handler (garbage every frame).";

    private static PlayerGuiLayout self;
    private PlayerCharacterController player;
    private bool applied;

    protected override string TryInstall()
    {
        self = this;
        Plugin.Harmony.Patch(AccessTools.Method(typeof(PlayerCharacterController), "OnEnable"),
            postfix: new HarmonyMethod(typeof(PlayerGuiLayout), nameof(OnEnablePostfix)));
        return null;
    }

    private static void OnEnablePostfix(PlayerCharacterController __instance)
    {
        if (self == null) return;
        self.player = __instance;
        self.applied = !self.Active; // force Tick to (re)apply on this instance
    }

    public override void Tick()
    {
        bool want = Active;
        if (want == applied || player is null) return;
        applied = want;
        if (player == null) { player = null; return; } // destroyed (Unity check only on state changes)
        player.useGUILayout = !want;
    }
}
