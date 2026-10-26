using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FIHGhost
{
    [BepInPlugin(Guid, "FIH Ghost", "0.1.1")]
    public class GhostPlugin : BasePlugin
    {
        public const string Guid = "com.dani.fihghost";

        internal static ManualLogSource Logger { get; private set; }
        internal static ConfigEntry<KeyCode> MenuKey { get; private set; }
        internal static ConfigEntry<KeyCode> RecordKey { get; private set; }
        internal static ConfigEntry<KeyCode> PlayKey { get; private set; }
        internal static ConfigEntry<KeyCode> CursorKey { get; private set; }
        internal static ConfigEntry<KeyCode> RestartKey { get; private set; }
        internal static ConfigEntry<string> RecordingsDir { get; private set; }
        internal static ConfigEntry<bool> RaceFullRun { get; private set; }
        internal static ConfigEntry<bool> RaceAreas { get; private set; }
        internal static ConfigEntry<bool> RaceChallenges { get; private set; }
        internal static ConfigEntry<bool> ShowHud { get; private set; }

        public override void Load()
        {
            Logger = Log;

            // The config file lives next to the dll, like the other FIH mods. The keys are
            // also rebindable from the menu, which writes them back here. Defaults avoid
            // every key the installed mods use (ChaosIsHard takes F1-F11 and the numpad).
            string folder = Path.GetDirectoryName(typeof(GhostPlugin).Assembly.Location) ?? Paths.ConfigPath;
            var config = new ConfigFile(Path.Combine(folder, "FIHGhost.cfg"), true);
            MenuKey = config.Bind("Keys", "Menu", KeyCode.J, "Opens / closes the FIH Ghost menu.");
            RecordKey = config.Bind("Keys", "Record", KeyCode.K,
                "Starts / stops recording your run; stopping saves it as a ghost file.");
            PlayKey = config.Bind("Keys", "PlayLatest", KeyCode.L,
                "Plays / stops the most recent ghost file, starting now from its first frame.");
            CursorKey = config.Bind("Keys", "Cursor", KeyCode.C,
                "With the menu open: switches between the mouse on the menu and the mouse turning the camera.");
            RestartKey = config.Bind("Keys", "PracticeRestart", KeyCode.R,
                "In practice mode: back to the selected route's start for a new attempt.");
            RecordingsDir = config.Bind("Ghost", "Folder", Path.Combine(folder, "Ghosts"),
                "Where ghost files (*" + GhostRecording.Extension + ") are saved and loaded from.");
            RaceFullRun = config.Bind("Race", "FullRun", true,
                "Shows your full-run PB ghost, synced to the playtime.");
            RaceAreas = config.Bind("Race", "Areas", true,
                "Shows your PB ghost of each area from the moment you reach it.");
            RaceChallenges = config.Bind("Race", "Challenges", true,
                "Shows your PB ghost of an NPC challenge while you play it.");
            ShowHud = config.Bind("UI", "ShowHud", true,
                "Shows the panel with the recording indicator and the splits being timed.");

            RestartWatcher.Install(new Harmony(Guid));
            ClassInjector.RegisterTypeInIl2Cpp<GhostBehaviour>();

            var host = new GameObject("FIHGhost");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<GhostBehaviour>();

            Logger.LogInfo($"Loaded. {MenuKey.Value} opens the menu, {RecordKey.Value} records, " +
                           $"{PlayKey.Value} plays the last ghost.");
        }
    }
}
