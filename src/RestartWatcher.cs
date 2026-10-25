using System;
using EHS;
using HarmonyLib;

namespace FIHGhost
{
    /// <summary>
    /// Notices a quick restart so the automatic recording starts over with the new run.
    /// The only Harmony patches in the mod: void, parameterless methods with a body of
    /// their own (checked in fih-address-map.json: no folded twins), patched one by one so
    /// a method a game update removed only costs that patch.
    /// </summary>
    internal static class RestartWatcher
    {
        // The game's restart entry points: the solo restart request (quick restart key and
        // button) and its full-reset step, both before the scene reloads. Not hooks that may
        // run once the new run is up (AnalyticsManager.OnGameRestarted): starting over then
        // would cut the first seconds of the new run and lose its full-run ghost.
        private static readonly (Type type, string method)[] Targets =
        {
            (typeof(GameEndManager), "RequestRestartGame"),
            (typeof(GameEndManager), "RequestFullSoloRestart"),
        };

        /// <summary>Set by the patches, cleared by RunSession when it starts over.</summary>
        public static bool Requested;

        public static void Install(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(RestartWatcher), nameof(OnRestart));
            foreach (var (type, name) in Targets)
            {
                try
                {
                    var method = AccessTools.Method(type, name);
                    if (method == null)
                    {
                        GhostPlugin.Logger.LogWarning($"[RESTART] {type.Name}.{name} not found; not watched.");
                        continue;
                    }
                    harmony.Patch(method, prefix: prefix);
                }
                catch (Exception ex)
                {
                    GhostPlugin.Logger.LogWarning($"[RESTART] Could not patch {type.Name}.{name}: {ex.Message}");
                }
            }
        }

        private static void OnRestart()
        {
            if (!Requested) GhostPlugin.Logger.LogInfo("[RESTART] The game is restarting the run.");
            Requested = true;
        }
    }
}
