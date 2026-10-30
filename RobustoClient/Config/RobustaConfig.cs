using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

/// <summary>
/// Holds all configuration settings for the RobustoClient
/// Uses new "RobustaKeybind" to support multi bounded keys
/// </summary>
public static partial class RobustaConfig
{
    #region Keybinds

    public static RobustaKeybind ToggleMenuKey = new(Keyboard.Key.F4);

    public static RobustaKeybind TargetLockKey = new(Keyboard.Key.Space);

    public static RobustaKeybind AutoConstructKey = new(Keyboard.Key.V);

    public static RobustaKeybind AutoDeconstructKey = new(Keyboard.Key.N);

    public static RobustaKeybind HackingKey = new(Keyboard.Key.H, Keyboard.Key.Shift);

    #endregion

    #region Toggles

    public static bool SpinBotEnabled;

    public static bool MeleeAimbotEnabled = true;

    public static bool MeleeTriggerbotEnabled = false;

    public static bool RangedAimbotEnabled = true;

    public static bool ThrowAimbotEnabled = true;

    public static bool NoRecoilEnabled = false;

    public static bool UsePrediction = false; // Toggles prediction

    public static bool SyndicateDetector = true;

    public static bool ContrabandDetector = true;

    public static bool FOVDisable = true;

    public static bool OverlaysDisable = true;

    public static bool FullbrightEnabled = false;

    public static bool EspEnabled = false;

    public static bool AntiSlipEnabled = true;

    #endregion

    #region Combat

    public static float SpinBotDegreesPerSecond = 1440f;

    // Radius (FOV) where aimbot searches for target (in meters/tiles from cursor)
    public static float AimRadius = 5f;

    // Maximum distance at which Target Lock continues to hold the target (so that lock resets if target goes off-screen)
    public static float MaxLockDistance = 15f;

    public static float RangedAimbotRadius = 2f;

    public static float AimFovPixels = 100f; // Aimbot capture radius in pixels (Screen FOV)

    // Average speed of a thrown item (needed for prediction)
    public static float DefaultThrowSpeed = 11f;

    // Default bullet speed for prediction (40.0 is standard for most firearms in current build)
    public static float DefaultProjectileSpeed = 40f;

    #endregion

    #region Ping compensation

    public enum PingMode { Local, Stable, Laggy, Auto }

    public static PingMode CurrentPingMode = PingMode.Auto;

    #endregion

    #region Friends

    public static HashSet<string> FriendsSet = [];

    #endregion

    #region Session only (never persisted)

    [JsonIgnore] public static bool ItemSearchEnabled = false;

    [JsonIgnore] public static string ItemSearchQuery = "";

    [JsonIgnore] public static int ManualPingMs = 0;

    #endregion
}