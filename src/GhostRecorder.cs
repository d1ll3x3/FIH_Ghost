using System;
using EHS;
using EHS.Interactables;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Records the local phone every physics tick, plus the area / challenge / game-end
    /// markers, by polling the game state (no Harmony patches). Sample times are the run
    /// clock: the playtime at Start plus the physics time since (Time.fixedTimeAsDouble),
    /// which advances exactly one fixedDeltaTime per sample and stops with the game, since
    /// FixedUpdate does not run at timeScale 0.
    /// </summary>
    internal sealed class GhostRecorder
    {
        // Further than this in one 20 ms tick is ~500 m/s: a teleport, not movement.
        private const float CutDistance = 10f;

        private GhostRecording _rec;
        private double _timeBase;
        private double _startFixedTime;
        private int _startCheckpoints;
        private bool _firstStep;

        private int _areaId;
        private bool _areaSeen;
        private string _pendingTeleport;
        private NpcChallengeController _challenge;
        private string _challengeId;
        private double _challengeCompletedBefore;
        private double _challengeElapsed;
        private bool _gameEnded;

        public bool IsRecording => _rec != null;
        public double Seconds => _rec?.Duration ?? 0;
        // The recording in progress, for callers that cut splits from it while it grows.
        public GhostRecording Current => _rec;

        /// <summary>
        /// Starts recording; with <paramref name="continueFrom"/> the samples are appended to
        /// that recording (the same run in a new session).
        /// </summary>
        public void Start(PlayerRef player, GhostRecording continueFrom = null)
        {
            var progress = player.GameRefs?.WorldSaveManager?.GetCurrentProgress();
            double playtime = player.PlayerProgressHandler?.Playtime ?? 0;
            _rec = continueFrom ?? new GhostRecording
            {
                MapId = progress?.MapId ?? "",
                Difficulty = progress?.DifficultyType.ToString() ?? "",
                StartPlaytime = playtime,
                RecordedAtUtc = DateTime.UtcNow,
                WorldGuid = progress?.WorldGuid ?? "",
            };
            _timeBase = playtime;
            _startFixedTime = Time.fixedTimeAsDouble;
            _startCheckpoints = player.GameRefs?.CheckpointsManager?.UsedCheckpoints ?? 0;
            _firstStep = true;
            // Unset, so the first tick writes the area and challenge the session starts in.
            _areaId = -1;
            _areaSeen = false;
            _pendingTeleport = null;
            _challenge = null;
            _challengeId = null;
            _gameEnded = GameManager.IsGameEnded;
            GhostPlugin.Logger.LogInfo($"[REC] {(continueFrom != null ? "Continuing" : "Started")}: " +
                                       $"map '{_rec.MapId}' {_rec.Difficulty}, playtime {playtime:0.00}");
        }

        /// <summary>Ends the recording; null if nothing was recorded.</summary>
        public GhostRecording Stop(PlayerRef player)
        {
            var rec = _rec;
            _rec = null;
            if (rec == null) return null;
            var checkpoints = player?.GameRefs?.CheckpointsManager;
            if (checkpoints != null)
                rec.CheckpointsUsed += Math.Max(0, checkpoints.UsedCheckpoints - _startCheckpoints);
            GhostPlugin.Logger.LogInfo($"[REC] Stopped: {rec.Samples.Count} samples ({rec.Duration:0.00} s), " +
                                       $"{rec.Markers.Count} markers, {rec.CheckpointsUsed} checkpoints used");
            return rec.Samples.Count > 0 ? rec : null;
        }

        /// <summary>Call once per FixedUpdate while recording.</summary>
        public void Step(PlayerRef player)
        {
            var rb = player.Rb;
            if (rb == null) return;
            double t = _timeBase + (Time.fixedTimeAsDouble - _startFixedTime);

            var samples = _rec.Samples;
            Vector3 pos = rb.position;
            // The first sample of a continued session is always a cut: the phone may have
            // been anywhere when the previous session ended.
            bool cut = samples.Count > 0 && (_firstStep
                || (pos - samples[samples.Count - 1].Position).sqrMagnitude > CutDistance * CutDistance);
            samples.Add(new GhostSample { Time = t, Position = pos, Rotation = rb.rotation, Cut = cut });

            double playtime = player.PlayerProgressHandler?.Playtime ?? 0;
            if (_pendingTeleport != null)
            {
                AddMarker(t, MarkerKind.Teleport, _pendingTeleport, playtime);
                _pendingTeleport = null;
                // Where the phone landed is not an area entry: report it as AreaAtStart.
                _areaId = -1;
                _areaSeen = false;
            }
            TrackArea(player, t, playtime);
            TrackChallenge(player, t);
            if (!_gameEnded && GameManager.IsGameEnded)
            {
                _gameEnded = true;
                AddMarker(t, MarkerKind.GameEnd, "", playtime);
            }
            _firstStep = false;
        }

        /// <summary>A marker at the last recorded tick, for events the caller detects.</summary>
        public void AddEvent(MarkerKind kind, string id, double value)
        {
            if (_rec == null || _rec.Samples.Count == 0) return;
            AddMarker(_rec.Samples[_rec.Samples.Count - 1].Time, kind, id, value);
        }

        /// <summary>
        /// Marks a teleport on the next recorded tick, the first one at the new position.
        /// </summary>
        public void MarkTeleport(string id) => _pendingTeleport = id;

        private void TrackArea(PlayerRef player, double t, double playtime)
        {
            var area = player.GameRefs?.LevelsInfoManager?.ActiveLevelInfo;
            int id = area != null ? area.Id : -1;
            if (id == _areaId) return;
            _areaId = id;
            if (id < 0) return;
            AddMarker(t, _areaSeen ? MarkerKind.AreaEnter : MarkerKind.AreaAtStart, id.ToString(), playtime);
            _areaSeen = true;
        }

        private void TrackChallenge(PlayerRef player, double t)
        {
            var active = player.GameRefs?.NpcChallengeManager?.ActiveChallenge;
            string activeId = active != null ? active.PersistentId : null;

            if (_challengeId != null && activeId != _challengeId)
            {
                // The game sets LastCompletedTime when the win trigger is reached; a quit, a
                // restart or leaving the bounds clears the challenge without touching it. A
                // destroyed controller (scene unloaded) compares equal to null.
                if (_challenge != null && _challenge.LastCompletedTime != _challengeCompletedBefore)
                    AddMarker(t, MarkerKind.ChallengeComplete, _challengeId, _challenge.LastCompletedTime);
                else
                    AddMarker(t, MarkerKind.ChallengeCancel, _challengeId, _challengeElapsed);
                _challenge = null;
                _challengeId = null;
            }

            if (activeId != null && _challengeId == null)
            {
                _challenge = active;
                _challengeId = activeId;
                _challengeCompletedBefore = active.LastCompletedTime;
                AddMarker(t, MarkerKind.ChallengeStart, activeId, active.ElapsedTime);
            }

            if (_challenge != null) _challengeElapsed = _challenge.ElapsedTime;
        }

        private void AddMarker(double t, MarkerKind kind, string id, double value)
        {
            _rec.Markers.Add(new GhostMarker { Time = t, Kind = kind, Id = id, Value = value });
            GhostPlugin.Logger.LogInfo($"[REC] {t:0.00}s {kind} '{id}' {value:0.000}");
        }
    }
}
