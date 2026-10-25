using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Puts the player's PB ghosts next to them, one per category and each in its own
    /// colour, so several can run at once: the full-run PB synced to the playtime for the
    /// whole session, the area PB from the moment a new area is reached, and the challenge
    /// PB while that challenge runs. RunSession tells it when those moments happen.
    /// </summary>
    internal sealed class PbRacer
    {
        private static readonly Color RunColor = new Color(0.45f, 0.75f, 1f, 0.35f);
        private static readonly Color AreaColor = new Color(0.45f, 1f, 0.6f, 0.35f);
        private static readonly Color ChallengeColor = new Color(1f, 0.7f, 0.35f, 0.4f);

        private readonly PersonalBests _pbs;
        private readonly GhostPlayback _run = new GhostPlayback(RunColor);
        private readonly GhostPlayback _area = new GhostPlayback(AreaColor);
        private readonly GhostPlayback _challenge = new GhostPlayback(ChallengeColor);
        private string _mapId, _difficulty;

        public PbRacer(PersonalBests pbs) => _pbs = pbs;

        public GhostPlayback RunGhost => _run;
        public GhostPlayback AreaGhost => _area;
        public GhostPlayback ChallengeGhost => _challenge;

        public void OnSessionStart(string mapId, string difficulty, PlayerRef player)
        {
            _mapId = mapId;
            _difficulty = difficulty;
            StartRunGhost(player);
        }

        public void OnSessionEnd()
        {
            _run.Stop();
            _area.Stop();
            _challenge.Stop();
            _mapId = _difficulty = null;
        }

        public void OnAreaReached(string areaId, PlayerRef player)
        {
            _area.Stop();
            if (!GhostPlugin.RaceAreas.Value) return;
            var pb = _pbs.Load(_mapId, _difficulty, GhostCategory.Area, areaId);
            if (pb != null && _area.StartRelative(pb, player))
                GhostPlugin.Logger.LogInfo($"[RACE] Area {areaId} PB {pb.FinalTime:0.000} s");
        }

        public void OnChallengeStarted(string challengeId, PlayerRef player)
        {
            _challenge.Stop();
            if (!GhostPlugin.RaceChallenges.Value) return;
            var pb = _pbs.Load(_mapId, _difficulty, GhostCategory.Challenge, challengeId);
            if (pb != null && _challenge.StartRelative(pb, player))
                GhostPlugin.Logger.LogInfo($"[RACE] Challenge {challengeId} PB {pb.FinalTime:0.000} s");
        }

        public void OnChallengeEnded() => _challenge.Stop();

        // A menu teleport ends the attempt the area and challenge ghosts were racing.
        public void OnTeleport()
        {
            _area.Stop();
            _challenge.Stop();
        }

        /// <summary>Applies the race toggles right away (called when the menu changes them).</summary>
        public void ApplyToggles(PlayerRef player)
        {
            if (!GhostPlugin.RaceAreas.Value) _area.Stop();
            if (!GhostPlugin.RaceChallenges.Value) _challenge.Stop();
            if (!GhostPlugin.RaceFullRun.Value) _run.Stop();
            else if (!_run.IsPlaying && _mapId != null && player != null) StartRunGhost(player);
        }

        public void LateUpdate()
        {
            _run.Update();
            _area.Update();
            _challenge.Update();
        }

        private void StartRunGhost(PlayerRef player)
        {
            if (!GhostPlugin.RaceFullRun.Value) return;
            var pb = _pbs.Load(_mapId, _difficulty, GhostCategory.FullRun, "");
            if (pb == null) return;
            // The run clock of a full-run recording is the playtime, so the ghost is where the
            // PB was at this exact point of its run, including after loading a save.
            if (_run.StartAbsolute(pb, player, () => GameAccess.Player?.PlayerProgressHandler?.Playtime ?? 0))
                GhostPlugin.Logger.LogInfo($"[RACE] Full run PB {pb.FinalTime:0.000} s");
        }
    }
}
