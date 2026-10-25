using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// The mod's actions, shared by the hotkeys and the menu: the automatic run session with
    /// its PB ghosts, the hand-made recording, the ghost picked from the list (next to the
    /// player or followed by the camera), teleports, and export / import, plus the last
    /// message worth showing the player.
    /// </summary>
    internal sealed class GhostController
    {
        // How long a message stays on the HUD (unscaled seconds).
        public const float MessageSeconds = 6f;
        private const float SessionRetrySeconds = 10f;
        private static readonly Color FreeColor = new Color(0.9f, 0.9f, 0.95f, 0.3f);

        public readonly GhostRecorder Recorder = new GhostRecorder();
        public readonly GhostPlayback Playback = new GhostPlayback(FreeColor);
        public readonly GhostLibrary Library;
        public readonly PersonalBests Pbs = new PersonalBests();
        public readonly WorldIndex World = new WorldIndex();
        public readonly PbRacer Racer;
        public readonly RunSession Session;
        public readonly PracticeMode Practice;

        public string PlayingName { get; private set; }
        public bool Spectating { get; private set; }
        public string Message { get; private set; } = "";
        public float MessageTime { get; private set; } = float.NegativeInfinity;
        // The file the last import created, for the menu to select it.
        public string LastImportedPath { get; private set; }
        public void ClearLastImported() => LastImportedPath = null;

        private float _sessionRetryAt;
        // The phone is frozen while the camera follows a ghost; these put it back.
        private IntPtr _spectatePlayer;
        private RigidbodyConstraints _savedConstraints;
        // File dialogs answer on their own thread; the answer is run here, on the main one.
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        public GhostController()
        {
            Racer = new PbRacer(Pbs);
            Session = new RunSession(Pbs, Racer, m => Notify(m));
            Practice = new PracticeMode(Pbs, (m, warning) => Notify(m, warning));
            Library = new GhostLibrary(World, Practice.Routes);
        }

        public void Update()
        {
            while (_mainThread.TryDequeue(out var action)) action();
            var player = GameAccess.Player;
            if (player == null) return;
            World.Refresh(player);
            Practice.Update(player);
        }

        public void FixedUpdate()
        {
            if (Practice.Active)
            {
                // Practice teleports and flies around: none of it belongs in the run.
                var p = GameAccess.Player;
                if (Session.IsActive) Session.End(p, "practice mode");
                if (p == null) Practice.OnPlayerLost();
                else Practice.FixedUpdate(p);
            }
            else if (Time.unscaledTime >= _sessionRetryAt)
            {
                try
                {
                    Session.FixedUpdate();
                }
                catch (Exception ex)
                {
                    // Ended and paused for a while rather than failing again every tick.
                    GhostPlugin.Logger.LogError($"[RUN] Session failed: {ex}");
                    Session.End(GameAccess.Player, "of an error");
                    _sessionRetryAt = Time.unscaledTime + SessionRetrySeconds;
                    Notify("Auto-recording paused after an error (see the log)", true);
                }
            }

            if (!Recorder.IsRecording) return;
            var player = GameAccess.Player;
            if (player == null)
            {
                StopRecording("the player left the level");
                return;
            }
            if (GameAccess.IsMultiplayer)
            {
                StopRecording("a multiplayer session started");
                return;
            }
            try
            {
                Recorder.Step(player);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[REC] Step failed: {ex}");
                StopRecording("of an error (see the log)");
            }
        }

        public void LateUpdate()
        {
            try
            {
                Racer.LateUpdate();
                Practice.LateUpdate();
                Playback.Update();
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[GHOST] Playback failed: {ex}");
                Racer.OnSessionEnd();
                Playback.Stop();
                Notify("Ghost stopped: playback error (see the log)", true);
            }
            if (!Playback.IsPlaying)
            {
                PlayingName = null;
                if (Spectating) StopSpectating();
            }
        }

        // --- manual recording -----------------------------------------------------------

        public void ToggleRecording()
        {
            if (Recorder.IsRecording)
            {
                StopRecording("stopped by the player");
                return;
            }
            var player = ReadyPlayer();
            if (player == null) return;
            Recorder.Start(player);
            Notify("Recording");
        }

        public void StopRecording(string reason)
        {
            if (!Recorder.IsRecording) return;
            GhostPlugin.Logger.LogInfo($"[REC] Stopping: {reason}.");
            var rec = Recorder.Stop(GameAccess.Player);
            if (rec == null)
            {
                Notify("Recording stopped: nothing was recorded");
                return;
            }
            string name = $"ghost_{DateTime.Now:yyyyMMdd_HHmmss}{GhostRecording.Extension}";
            string path = Path.Combine(GhostPlugin.RecordingsDir.Value, name);
            try
            {
                rec.Save(path);
                GhostPlugin.Logger.LogInfo($"[REC] Saved {path}");
                Notify($"Saved {name} ({FormatTime(rec.Duration)})");
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[REC] Could not save {path}: {ex}");
                Notify($"Could not save the ghost: {ex.Message}", true);
            }
            Library.Refresh();
        }

        // --- watching ghosts --------------------------------------------------------------

        /// <summary>Hotkey: stops the ghost, or plays the newest hand-made recording.</summary>
        public void TogglePlayLatest()
        {
            if (Playback.IsPlaying)
            {
                StopPlayback();
                return;
            }
            Library.Refresh();
            var latest = Library.Latest;
            if (latest == null)
            {
                Notify($"No recordings yet: record one with {GhostPlugin.RecordKey.Value}", true);
                return;
            }
            Play(latest);
        }

        /// <summary>Plays the ghost where it was recorded, from its start, next to the player.</summary>
        public bool Play(GhostLibrary.Entry entry)
        {
            var player = ReadyPlayer();
            if (player == null) return false;
            if (Spectating) StopSpectating();

            GhostRecording rec;
            try
            {
                rec = GhostRecording.Load(entry.Path);
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogError($"[GHOST] Could not load {entry.Path}: {ex}");
                Notify($"Could not load {entry.Name}: {ex.Message}", true);
                return false;
            }
            if (!Playback.StartRelative(rec, player))
            {
                Notify("Could not build the ghost (see the log)", true);
                return false;
            }
            PlayingName = entry.Title;
            GhostPlugin.Logger.LogInfo($"[GHOST] Playing {entry.Name} ({rec.Duration:0.00} s, map '{rec.MapId}').");
            Notify($"Playing {entry.Title}");
            return true;
        }

        /// <summary>
        /// Plays the ghost with the game camera following it instead of the phone, which stays
        /// frozen where it is until the ghost ends or is stopped.
        /// </summary>
        public void Spectate(GhostLibrary.Entry entry)
        {
            if (!Play(entry)) return;
            var player = GameAccess.Player;
            var cam = player.GameRefs?.CameraManager;
            var body = Playback.Body;
            if (cam == null || body == null || player.Rb == null)
            {
                Notify("Spectating is not available here; the ghost plays next to you", true);
                return;
            }
            cam.SetTrackingObject(body);
            cam.SyncPosAfterTeleport(body.position);
            var rb = player.Rb;
            _spectatePlayer = player.Pointer;
            _savedConstraints = rb.constraints;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            // Frozen by constraints, not made kinematic: the game keeps writing velocities to
            // it every tick, which a kinematic body rejects with a warning each time.
            rb.constraints = RigidbodyConstraints.FreezeAll;
            Spectating = true;
            Notify($"Spectating {entry.Title}");
        }

        public void StopPlayback()
        {
            if (!Playback.IsPlaying) return;
            Playback.Stop();
            PlayingName = null;
            if (Spectating) StopSpectating();
            GhostPlugin.Logger.LogInfo("[GHOST] Stopped by the player.");
            Notify("Ghost stopped");
        }

        private void StopSpectating()
        {
            Spectating = false;
            var player = GameAccess.Player;
            // After a scene change the frozen phone and its camera are gone with it.
            if (player == null || player.Pointer != _spectatePlayer) return;
            var cam = player.GameRefs?.CameraManager;
            if (cam != null && player.CameraTargetT != null)
            {
                cam.SetTrackingObject(player.CameraTargetT);
                cam.SyncPosAfterTeleport(player.transform.position);
            }
            if (player.Rb != null) player.Rb.constraints = _savedConstraints;
        }

        // --- teleports --------------------------------------------------------------------

        public void TeleportToArea(WorldIndex.Area area)
        {
            if (!area.HasStart)
            {
                Notify($"No start point found for {area.Name}", true);
                return;
            }
            Teleport(area.Position, area.Rotation, area.CameraYaw, area.Key, $"Teleported to {area.Name}: move to start the timer");
        }

        public void TeleportToNpc(WorldIndex.Npc npc)
            => Teleport(npc.Position, npc.Rotation, npc.CameraYaw, npc.Key, $"Teleported to {npc.Name}");


        // Same steps as the game's own checkpoint load, through its PlayerTeleportController
        // (which keeps the NetworkTransform in sync), as FIHAgent does.
        private void Teleport(Vector3 pos, Quaternion rot, float? cameraYaw, string markerId, string message)
        {
            var player = ReadyPlayer();
            if (player == null) return;
            if (Spectating) StopPlayback();
            Practice.StopAttempt();
            Practice.Fly.Exit();
            Session.MarkTeleport(markerId);
            if (!GameAccess.Teleport(player, pos, rot, cameraYaw ?? rot.eulerAngles.y))
            {
                Notify("The player can't be teleported right now", true);
                return;
            }
            Notify(message);
        }

        // --- practice -------------------------------------------------------------------

        public void TogglePractice()
        {
            if (Practice.Active)
            {
                Practice.Exit();
                return;
            }
            var player = ReadyPlayer();
            if (player == null) return;
            if (Spectating) StopPlayback();
            Practice.Enter(player);
        }

        // --- export / import --------------------------------------------------------------

        public void Export(GhostLibrary.Entry entry)
        {
            NativeFileDialog.SaveGhostFile(ExportName(entry), (path, error) => _mainThread.Enqueue(() =>
            {
                if (error != null)
                {
                    Notify($"Export failed: {error}", true);
                    return;
                }
                if (path == null) return;   // cancelled
                try
                {
                    File.Copy(entry.Path, path, true);
                    GhostPlugin.Logger.LogInfo($"[LIB] Exported {entry.Path} to {path}");
                    Notify($"Exported to {path}");
                }
                catch (Exception ex)
                {
                    GhostPlugin.Logger.LogError($"[LIB] Could not export to {path}: {ex}");
                    Notify($"Could not export: {ex.Message}", true);
                }
            }));
        }

        public void Import()
        {
            NativeFileDialog.OpenGhostFile((path, error) => _mainThread.Enqueue(() =>
            {
                if (error != null)
                {
                    Notify($"Import failed: {error}", true);
                    return;
                }
                if (path == null) return;   // cancelled
                try
                {
                    // Read before copying: a file that isn't a ghost never enters the library.
                    GhostRecording.LoadSummary(path, out _);
                    string dir = GhostLibrary.ImportedFolder;
                    Directory.CreateDirectory(dir);
                    string stem = Path.GetFileNameWithoutExtension(path);
                    string dest = Path.Combine(dir, stem + GhostRecording.Extension);
                    for (int n = 2; File.Exists(dest); n++)
                        dest = Path.Combine(dir, $"{stem} ({n}){GhostRecording.Extension}");
                    File.Copy(path, dest);
                    Library.Refresh();
                    LastImportedPath = dest;
                    GhostPlugin.Logger.LogInfo($"[LIB] Imported {path} as {dest}");
                    Notify($"Imported {Path.GetFileName(dest)}");
                }
                catch (Exception ex)
                {
                    GhostPlugin.Logger.LogError($"[LIB] Could not import {path}: {ex}");
                    Notify($"Could not import: {ex.Message}", true);
                }
            }));
        }

        private static string ExportName(GhostLibrary.Entry entry)
        {
            var h = entry.Header;
            if (entry.Source == GhostLibrary.Source.Manual) return entry.Name;
            string time = FormatTime(entry.Time).Replace(':', '-').Replace('.', '-');
            string name = $"{h.MapId}_{h.Difficulty}_{entry.Title.Replace("PB · ", "").Replace("Imported · ", "")}_{time}";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_') + GhostRecording.Extension;
        }

        // --- misc -------------------------------------------------------------------------

        /// <summary>Called when the game is closing: keeps both recordings.</summary>
        public void Shutdown()
        {
            StopRecording("the game is closing");
            Session.End(GameAccess.Player, "the game is closing");
        }

        public void Notify(string message, bool warning = false)
        {
            Message = message;
            MessageTime = Time.unscaledTime;
            if (warning) GhostPlugin.Logger.LogWarning(message);
        }

        /// <summary>The area split being timed, as "L5 ToyChest 0:45.12 / PB 0:50.00"; null when none.</summary>
        public string AreaStatus()
        {
            var run = Session.Run;
            if (run == null) return null;
            if (Session.PracticePending >= 0)
                return $"{AreaName(Session.PracticePending)}: move to start the timer  /  " +
                       PbText(run, GhostCategory.Area, Session.PracticePending.ToString());
            if (Session.OpenArea < 0 && Session.LevelTimingBlocked)
                return "Levels not timed after an NPC teleport (use a level teleport)";
            if (Session.OpenArea < 0)
                return Session.MaxArea >= 0 ? $"{AreaName(Session.MaxArea)}: not timed (you started inside it)" : null;
            string id = Session.OpenArea.ToString();
            return $"{AreaName(Session.OpenArea)}  {FormatTime(Session.OpenAreaElapsed)}  /  {PbText(run, GhostCategory.Area, id)}";
        }

        /// <summary>The challenge being timed, like AreaStatus; null when none.</summary>
        public string ChallengeStatus()
        {
            var run = Session.Run;
            if (run == null || Session.OpenChallenge == null) return null;
            return $"{World.ChallengeName(Session.OpenChallenge)}  {FormatTime(Session.OpenChallengeElapsed)}  /  " +
                   PbText(run, GhostCategory.Challenge, Session.OpenChallenge);
        }

        public string AreaName(int id) => World.FindArea(id)?.Name ?? $"Area {id}";

        public string PbText(GhostRecording run, GhostCategory category, string id)
        {
            double? pb = Pbs.TimeOf(run.MapId, run.Difficulty, category, id);
            return pb.HasValue ? $"PB {FormatTime(pb.Value)}" : "no PB yet";
        }

        public static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss\\.ff}" : $"{t:m\\:ss\\.ff}";
        }

        private PlayerRef ReadyPlayer()
        {
            var player = GameAccess.Player;
            if (player == null)
            {
                Notify("Load into a level first", true);
                return null;
            }
            if (GameAccess.IsMultiplayer)
            {
                Notify("Ghosts are single-player only", true);
                return null;
            }
            return player;
        }
    }
}
