using System;
using System.Text;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// One-shot dump of what the ghost needs from the game: how the local player's visuals
    /// are built (to clone them as the ghost body) and every clock the recorder can sync to
    /// (run playtime, active area, active NPC challenge). Each section is read on its own so
    /// a member that moved in a game update only costs that section.
    /// </summary>
    internal static class Diagnostics
    {
        // Shaders the ghost material could use; which ones the build ships decides the look.
        private static readonly string[] ShaderCandidates =
        {
            "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Simple Lit", "Sprites/Default", "Unlit/Transparent",
            "Hidden/Internal-Colored", "UI/Default",
        };

        public static void Dump()
        {
            var sb = new StringBuilder("[DUMP] ---- FIH Ghost diagnostics ----\n");
            var player = PlayerRef.LocalPlayer;
            if (player == null)
            {
                GhostPlugin.Logger.LogInfo("[DUMP] No local player: load into a level first.");
                return;
            }

            Section(sb, "player", () => DumpPlayer(sb, player));
            Section(sb, "visuals", () => DumpVisuals(sb, player));
            Section(sb, "run", () => DumpRun(sb, player));
            Section(sb, "areas", () => DumpAreas(sb, player));
            Section(sb, "challenges", () => DumpChallenges(sb, player));
            Section(sb, "shaders", () => DumpShaders(sb));

            GhostPlugin.Logger.LogInfo(sb.ToString());
        }

        private static void Section(StringBuilder sb, string name, Action dump)
        {
            sb.Append($"== {name}\n");
            try { dump(); }
            catch (Exception ex)
            {
                sb.Append($"  FAILED: {ex.GetType().Name}: {ex.Message}\n");
                GhostPlugin.Logger.LogWarning($"[DUMP] Section '{name}' failed: {ex}");
            }
        }

        private static void DumpPlayer(StringBuilder sb, PlayerRef player)
        {
            var rb = player.Rb;
            var t = player.transform;
            sb.Append($"  fullyInitialized={player.IsFullyInitialized} root='{t.name}' layer={t.gameObject.layer}\n");
            sb.Append($"  transform pos={t.position:F3} rot={t.rotation.eulerAngles:F1}\n");
            if (rb != null)
                sb.Append($"  rb pos={rb.position:F3} rot={rb.rotation.eulerAngles:F1} vel={rb.linearVelocity:F2} " +
                          $"interp={rb.interpolation} kinematic={rb.isKinematic}\n");
            sb.Append($"  fixedDeltaTime={Time.fixedDeltaTime} timeScale={Time.timeScale}\n");
            sb.Append($"  connectedPlayers={GameAccess.AsList(PlayerRef.ConnectedPlayersClient)?.Count ?? -1}\n");
        }

        private static void DumpVisuals(StringBuilder sb, PlayerRef player)
        {
            var visual = player.PlayerVisualHandler?.PlayerVisualT;
            if (visual == null)
            {
                sb.Append("  PlayerVisualT is null\n");
                return;
            }
            var root = player.transform;
            sb.Append($"  PlayerVisualT='{visual.name}' isChildOfRoot={visual.IsChildOf(root)}\n");
            sb.Append($"  relative to root: pos={root.InverseTransformPoint(visual.position):F3} " +
                      $"rot={(Quaternion.Inverse(root.rotation) * visual.rotation).eulerAngles:F1} " +
                      $"lossyScale={visual.lossyScale:F3}\n");
            DumpHierarchy(sb, visual, 1);
        }

        private static void DumpHierarchy(StringBuilder sb, Transform t, int depth)
        {
            string indent = new string(' ', depth * 2);
            var go = t.gameObject;
            sb.Append($"{indent}- {t.name} active={go.activeSelf} layer={go.layer} " +
                      $"localPos={t.localPosition:F3} localScale={t.localScale:F3}\n");

            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string type = c.GetIl2CppType().FullName;
                if (type == "UnityEngine.Transform") continue;
                sb.Append($"{indent}    [{type}]");
                var renderer = c.TryCast<Renderer>();
                if (renderer != null)
                {
                    sb.Append($" enabled={renderer.enabled} mats:");
                    foreach (var m in renderer.sharedMaterials)
                        sb.Append(m == null ? " <null>" : $" '{m.name}'({m.shader?.name})");
                }
                var skin = c.TryCast<EHS.Skins.SkinPrefabHandler>();
                if (skin != null)
                    sb.Append($" category={skin.Category} skin='{skin.CurrentSkinKey}' loading={skin.IsLoading}");
                sb.Append('\n');
            }

            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(sb, t.GetChild(i), depth + 1);
        }

        private static void DumpRun(StringBuilder sb, PlayerRef player)
        {
            sb.Append($"  playtime(live)={player.PlayerProgressHandler?.Playtime}\n");
            sb.Append($"  gameEnded={GameManager.IsGameEnded} frozen={GameManager.IsGameFrozen} " +
                      $"pauseMenu={GameManager.IsPauseMenuShown}\n");
            var checkpoints = player.GameRefs?.CheckpointsManager;
            if (checkpoints != null)
                sb.Append($"  checkpoints enabled={checkpoints.CheckpointsEnabled()} used={checkpoints.UsedCheckpoints}\n");
            var progress = player.GameRefs?.WorldSaveManager?.GetCurrentProgress();
            if (progress == null)
            {
                sb.Append("  no current ProgressData\n");
                return;
            }
            sb.Append($"  mapId='{progress.MapId}' difficulty={progress.DifficultyType} " +
                      $"playtime(saved)={progress.Playtime} isNewGame={progress.IsNewGame} " +
                      $"isGameEnded={progress.IsGameEnded} worldGuid='{progress.WorldGuid}'\n");
        }

        private static void DumpAreas(StringBuilder sb, PlayerRef player)
        {
            var levels = player.GameRefs?.LevelsInfoManager;
            if (levels == null)
            {
                sb.Append("  no LevelsInfoManager\n");
                return;
            }
            var active = levels.ActiveLevelInfo;
            sb.Append($"  active={(active == null ? "<none>" : $"{active.Id} '{active.name}'")}\n");
            var all = GameAccess.AsList(levels.LevelsInfo);
            for (int i = 0; i < all.Count; i++)
            {
                var info = all[i];
                sb.Append($"  [{i}] id={info.Id} '{info.name}' unlocked={levels.IsAreaUnlocked(info)}\n");
            }
        }

        private static void DumpChallenges(StringBuilder sb, PlayerRef player)
        {
            var manager = player.GameRefs?.NpcChallengeManager;
            if (manager == null)
            {
                sb.Append("  no NpcChallengeManager\n");
                return;
            }
            var active = manager.ActiveChallenge;
            sb.Append($"  active={(active == null ? "<none>" : $"'{active.PersistentId}' elapsed={active.ElapsedTime}")}\n");
            var all = GameAccess.AsList(manager.Challenges);
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                sb.Append($"  [{i}] '{c.PersistentId}' go='{c.name}' completed={c.HasCompleted} " +
                          $"best={(c.HasBestTime ? c.BestTime.ToString("F3") : "-")} pos={c.transform.position:F1}\n");
            }
        }

        private static void DumpShaders(StringBuilder sb)
        {
            foreach (var name in ShaderCandidates)
                sb.Append($"  {name}: {(Shader.Find(name) != null ? "yes" : "no")}\n");
        }
    }
}
