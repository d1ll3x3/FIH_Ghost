using System;
using System.IO;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Records every single-player session automatically and cuts PB splits out of it as it
    /// grows (NOTAS_FASE1.md, "Diseño"):
    /// - area X: from first reaching X to first reaching a higher area (or the game end), the
    ///   same first-reach rule as the game's splits;
    /// - challenge: from its start to the game reporting it completed;
    /// - full run: the whole stream, when it covers the run from playtime 0 to the game end.
    ///
    /// The stream of an unfinished run is kept in Ghosts/Run/&lt;worldGuid&gt;.fihghost, so a
    /// run played over several sessions still ends up as one full-run ghost.
    /// </summary>
    internal sealed class RunSession
    {
        // A saved stream continues the run unless the loaded playtime is further than this
        // past its end (the run was played on without the mod).
        private const double ContinueTolerance = 2.0;
        // A run counts as recorded from the start if its first sample is this early.
        private const double RunStartTolerance = 1.0;
        // A challenge seen active with more elapsed time than this was restored from a save.
        private const double RestoredChallengeTolerance = 0.5;
        // How often the unfinished run is written to disk while playing (unscaled seconds).
        private const float SaveInterval = 300f;
        // After a quick restart, how long to wait for the new run before recording anyway.
        private const float RestartWaitSeconds = 15f;
        // Teleport marker id for a menu teleport to an area start: "area:<LevelInfoSO.Id>".
        public const string TeleportAreaPrefix = "area:";

        private readonly GhostRecorder _recorder = new GhostRecorder();
        private readonly PersonalBests _pbs;
        private readonly PbRacer _racer;
        private readonly Action<string> _notify;

        private int _markersSeen;
        private double _lastPlaytime;
        private float _nextSave;

        // Highest area reached in this stream, and the area split being timed (-1: none).
        private int _maxArea;
        private int _openArea;
        private int _openAreaSample;
        private string _openChallenge;
        private int _openChallengeSample;
        // False once a menu teleport happened in this stream: it can no longer be a full run.
        private bool _fullRunValid;
        // Teleported to this area's start and waiting for the first input to time it (-1: no).
        private int _practiceArea;
        // Whether every input has been released since that teleport: the click on the menu's
        // Teleport button reaches the game as a held button, so only a press after a
        // release starts the timer.
        private bool _practiceArmed;
        // After a teleport to an NPC only challenges are timed, until a level teleport.
        private bool _levelTimingBlocked;

        // Set by a quick restart until the new run is up (new player object, playtime back
        // near 0, or the wait ran out), so the old run's last ticks never start the new stream.
        private bool _awaitingRestart;
        private IntPtr _restartPlayer;
        private float _restartWaitUntil;

        public RunSession(PersonalBests pbs, PbRacer racer, Action<string> notify)
        {
            _pbs = pbs;
            _racer = racer;
            _notify = notify;
        }

        public bool IsActive => _recorder.IsRecording;
        public GhostRecording Run => _recorder.Current;
        // Highest area reached in this stream (-1: none yet), the area split being timed
        // (-1: none, e.g. the session began inside the area) and the challenge being timed.
        public int MaxArea => _maxArea;
        public int OpenArea => _openArea;
        public string OpenChallenge => _openChallenge;
        public double OpenAreaElapsed => IsActive && _openArea >= 0 ? Run.EndTime - Run.Samples[_openAreaSample].Time : 0;
        public double OpenChallengeElapsed => IsActive && _openChallenge != null ? Run.EndTime - Run.Samples[_openChallengeSample].Time : 0;
        public bool FullRunValid => _fullRunValid;
        public int PracticePending => _practiceArea;
        public bool LevelTimingBlocked => _levelTimingBlocked;

        /// <summary>A menu teleport is about to happen; marks it on the next recorded tick.</summary>
        public void MarkTeleport(string id)
        {
            if (IsActive) _recorder.MarkTeleport(id);
        }

        /// <summary>Call once per FixedUpdate.</summary>
        public void FixedUpdate()
        {
            var player = GameAccess.Player;
            if (RestartWatcher.Requested)
            {
                // Before the null check: the restart request arrives while the old player is
                // still there, and its stream must not survive the scene reload.
                RestartWatcher.Requested = false;
                if (IsActive)
                {
                    var old = Run;
                    End(player, "quick restart", keep: false);
                    DeleteRunFile(old);
                }
                _awaitingRestart = true;
                _restartPlayer = player != null ? player.Pointer : IntPtr.Zero;
                _restartWaitUntil = Time.unscaledTime + RestartWaitSeconds;
                _notify("Quick restart: the recording starts over");
            }
            if (player == null || GameAccess.IsMultiplayer)
            {
                if (IsActive) End(GameAccess.Player, "the session ended");
                return;
            }

            double playtime = player.PlayerProgressHandler?.Playtime ?? 0;
            string world = player.GameRefs?.WorldSaveManager?.GetCurrentProgress()?.WorldGuid ?? "";
            if (IsActive && (playtime < _lastPlaytime - RunStartTolerance || world != Run.WorldGuid))
            {
                // A restart puts the playtime back to 0 (and a new world a new guid): the
                // stream of the old run can never finish, so it is dropped.
                var stale = Run;
                End(player, "the run restarted", keep: false);
                DeleteRunFile(stale);
            }
            if (!IsActive)
            {
                if (_awaitingRestart && player.Pointer == _restartPlayer && playtime > RunStartTolerance
                    && Time.unscaledTime < _restartWaitUntil)
                    return;
                Begin(player, fresh: _awaitingRestart);
                _awaitingRestart = false;
            }
            _lastPlaytime = playtime;

            _recorder.Step(player);
            if (_practiceArea >= 0)
            {
                bool input = GameAccess.HasMoveInput(player);
                if (!_practiceArmed) _practiceArmed = !input;
                else if (input) _recorder.AddEvent(MarkerKind.PracticeStart, _practiceArea.ToString(), playtime);
            }
            ProcessMarkers(player, live: true);

            if (Time.unscaledTime >= _nextSave)
            {
                _nextSave = Time.unscaledTime + SaveInterval;
                SaveRunFile(Run);
            }
        }

        /// <summary>Ends the session, keeping the unfinished run on disk.</summary>
        public void End(PlayerRef player, string reason, bool keep = true)
        {
            if (!IsActive) return;
            GhostPlugin.Logger.LogInfo($"[RUN] Session end: {reason}.");
            var run = _recorder.Stop(player);
            _racer.OnSessionEnd();
            if (run == null || !keep) return;
            if (HasMarker(run, MarkerKind.GameEnd)) DeleteRunFile(run);   // finished: its PBs are cut already
            else SaveRunFile(run);
        }

        // fresh: right after a quick restart, never continue a saved stream.
        private void Begin(PlayerRef player, bool fresh)
        {
            var progress = player.GameRefs?.WorldSaveManager?.GetCurrentProgress();
            double playtime = player.PlayerProgressHandler?.Playtime ?? 0;
            var saved = fresh ? null : LoadRunFile(progress?.WorldGuid ?? "");
            bool continues = saved != null && saved.Samples.Count > 0
                             && playtime <= saved.EndTime + ContinueTolerance
                             && saved.MapId == (progress?.MapId ?? "")
                             && saved.Difficulty == (progress?.DifficultyType.ToString() ?? "")
                             && TruncateTo(saved, playtime);

            _maxArea = -1;
            _openArea = -1;
            _openChallenge = null;
            _fullRunValid = true;
            _practiceArea = -1;
            _levelTimingBlocked = false;
            _markersSeen = 0;
            _lastPlaytime = playtime;
            _nextSave = Time.unscaledTime + SaveInterval;

            _recorder.Start(player, continues ? saved : null);
            // Rebuild the split state from the earlier sessions' markers; their splits were
            // judged when they happened, so nothing is submitted or raced now.
            ProcessMarkers(player, live: false);
            GhostPlugin.Logger.LogInfo($"[RUN] Session start: {(continues ? $"continuing a {saved.Duration:0} s run" : "new stream")}, " +
                                       $"playtime {playtime:0.00}");
            _racer.OnSessionStart(Run.MapId, Run.Difficulty, player);
        }

        private void ProcessMarkers(PlayerRef player, bool live)
        {
            var markers = Run.Markers;
            for (; _markersSeen < markers.Count; _markersSeen++)
            {
                var m = markers[_markersSeen];
                switch (m.Kind)
                {
                    case MarkerKind.AreaAtStart:
                        // A session that begins at the very start of the run is the real
                        // first entry into the first area; any later one is not an entry.
                        if (m.Time <= RunStartTolerance) OnAreaReached(m, player, live);
                        else _maxArea = Math.Max(_maxArea, ParseArea(m.Id));
                        break;
                    case MarkerKind.AreaEnter:
                        OnAreaReached(m, player, live);
                        break;
                    case MarkerKind.ChallengeStart:
                        if (m.Value <= RestoredChallengeTolerance)
                        {
                            _openChallenge = m.Id;
                            _openChallengeSample = IndexAt(m.Time);
                            if (live) _racer.OnChallengeStarted(m.Id, player);
                        }
                        // else: restored from a save. It keeps timing only if this stream saw
                        // it start, which leaves _openChallenge as it was.
                        else if (_openChallenge != m.Id) _openChallenge = null;
                        break;
                    case MarkerKind.ChallengeComplete:
                        if (_openChallenge == m.Id && live)
                            Submit(_openChallengeSample, IndexAt(m.Time), GhostCategory.Challenge, m.Id, m.Value);
                        if (_openChallenge == m.Id) _openChallenge = null;
                        if (live) _racer.OnChallengeEnded();
                        break;
                    case MarkerKind.ChallengeCancel:
                        if (_openChallenge == m.Id) _openChallenge = null;
                        if (live) _racer.OnChallengeEnded();
                        break;
                    case MarkerKind.GameEnd:
                        int end = IndexAt(m.Time);
                        if (live) CloseArea(end, m.Time);
                        _openArea = -1;
                        if (live && _fullRunValid && Run.StartTime <= RunStartTolerance)
                            Submit(0, end, GhostCategory.FullRun, "", m.Value);
                        break;
                    case MarkerKind.Teleport:
                        // Whatever was being timed is no longer a clean attempt, and the
                        // stream is no longer a full run.
                        _fullRunValid = false;
                        _openArea = -1;
                        _practiceArea = -1;
                        if (live) _racer.OnTeleport();
                        if (m.Id.StartsWith(TeleportAreaPrefix)
                            && int.TryParse(m.Id.Substring(TeleportAreaPrefix.Length), out int area))
                        {
                            // Teleported to an area start: that area is practised from the
                            // first input (PracticeStart) until a higher area is reached.
                            _maxArea = area;
                            _practiceArea = area;
                            _practiceArmed = false;
                            _levelTimingBlocked = false;
                        }
                        else
                        {
                            // Teleported to an NPC: its challenge is what is being practised.
                            // The AreaAtStart the recorder writes next sets the area.
                            _maxArea = -1;
                            _levelTimingBlocked = true;
                        }
                        break;
                    case MarkerKind.PracticeStart:
                        if (ParseArea(m.Id) != _practiceArea) break;
                        _openArea = _practiceArea;
                        _openAreaSample = IndexAt(m.Time);
                        _practiceArea = -1;
                        if (live) _racer.OnAreaReached(m.Id, player);
                        break;
                }
            }
        }

        private void OnAreaReached(GhostMarker m, PlayerRef player, bool live)
        {
            int id = ParseArea(m.Id);
            if (id <= _maxArea) return;   // a fall back down, or an area already reached
            if (_levelTimingBlocked)
            {
                _maxArea = id;
                return;
            }
            int index = IndexAt(m.Time);
            if (live) CloseArea(index, m.Time);
            _maxArea = id;
            _openArea = id;
            _openAreaSample = index;
            if (live) _racer.OnAreaReached(m.Id, player);
        }

        private void CloseArea(int endSample, double endTime)
        {
            if (_openArea < 0) return;
            var samples = Run.Samples;
            double time = endTime - samples[_openAreaSample].Time;
            Submit(_openAreaSample, endSample, GhostCategory.Area, _openArea.ToString(), time);
            _openArea = -1;
        }

        private void Submit(int from, int to, GhostCategory category, string id, double time)
        {
            if (to <= from) return;
            var split = Run.Slice(from, to, category, id, time);
            double? previous = _pbs.Submit(split, out bool improved);
            string what = category == GhostCategory.FullRun ? "Full run" : $"{category} {Short(id)}";
            if (improved)
                _notify(previous.HasValue
                    ? $"New PB! {what} {GhostController.FormatTime(time)} (-{GhostController.FormatTime(previous.Value - time)})"
                    : $"First PB: {what} {GhostController.FormatTime(time)}");
            else if (previous.HasValue)
                _notify($"{what} {GhostController.FormatTime(time)} (PB {GhostController.FormatTime(previous.Value)})");
        }

        // The sample a marker was written with: markers are added right after their sample.
        private int IndexAt(double time)
        {
            var samples = Run.Samples;
            int lo = 0, hi = samples.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (samples[mid].Time <= time) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        // The game's last save can be a little older than the end of the stream (it saved,
        // then the session went on before quitting): the run resumes from the saved playtime,
        // so whatever the stream holds past it never happened in this run. False when
        // nothing of the stream is left.
        private static bool TruncateTo(GhostRecording run, double playtime)
        {
            int keep = run.Samples.FindLastIndex(s => s.Time <= playtime) + 1;
            if (keep == 0) return false;
            run.Samples.RemoveRange(keep, run.Samples.Count - keep);
            run.Markers.RemoveAll(m => m.Time > playtime);
            return true;
        }

        private static int ParseArea(string id) => int.TryParse(id, out int v) ? v : -1;

        // Challenge ids are 32-char GUIDs; the first 6 are enough to tell them apart on screen.
        private static string Short(string id) => id.Length > 8 ? id.Substring(0, 6) : id;

        private static bool HasMarker(GhostRecording rec, MarkerKind kind)
        {
            foreach (var m in rec.Markers)
                if (m.Kind == kind) return true;
            return false;
        }

        private static string RunFilePath(string worldGuid)
            => Path.Combine(GhostPlugin.RecordingsDir.Value, "Run",
                (string.IsNullOrEmpty(worldGuid) ? "unknown" : worldGuid) + GhostRecording.Extension);

        private static GhostRecording LoadRunFile(string worldGuid)
        {
            string path = RunFilePath(worldGuid);
            if (!File.Exists(path)) return null;
            try
            {
                return GhostRecording.Load(path);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogWarning($"[RUN] Ignoring unreadable {path}: {ex.Message}");
                return null;
            }
        }

        private static void SaveRunFile(GhostRecording run)
        {
            if (run == null || run.Samples.Count == 0) return;
            string path = RunFilePath(run.WorldGuid);
            try
            {
                run.Category = GhostCategory.FullRun;
                run.Save(path);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[RUN] Could not save {path}: {ex}");
            }
        }

        private static void DeleteRunFile(GhostRecording run)
        {
            if (run == null) return;
            string path = RunFilePath(run.WorldGuid);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogWarning($"[RUN] Could not delete {path}: {ex.Message}");
            }
        }
    }
}
