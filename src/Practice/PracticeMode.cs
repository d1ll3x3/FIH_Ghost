using System;
using System.Linq;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Practice mode, like the Map Editor's spawn and goal: routes with a start and an end
    /// zone placed anywhere on the map (flying there if needed), and attempts at them. An
    /// attempt teleports the phone to the start, times from the first input after every key
    /// was released (as the game's timer and the level teleports do) until the phone enters
    /// the end zone, and keeps the best one as the route's PB ghost, which races the next
    /// attempts. While practice mode is on, the automatic run recording is paused
    /// (GhostController).
    /// </summary>
    internal sealed class PracticeMode
    {
        public enum AttemptState { None, Armed, Running }

        private static readonly Color GhostColor = new Color(0.8f, 0.55f, 1f, 0.35f);
        private static readonly Color StartColor = new Color(0.3f, 1f, 0.45f, 1f);
        private static readonly Color EndColor = new Color(1f, 0.8f, 0.25f, 1f);
        private const float StartMarkerHeight = 3f;

        private readonly PersonalBests _pbs;
        private readonly Action<string, bool> _notify;
        private readonly GhostRecorder _recorder = new GhostRecorder();
        private readonly GhostPlayback _ghost = new GhostPlayback(GhostColor);
        private readonly LineBox _startMarker = new LineBox("FIHGhost_RouteStart");
        private readonly LineBox _endMarker = new LineBox("FIHGhost_RouteEnd");
        // Every input released since the teleport to the start: the click or key that
        // started the attempt must not also start its timer.
        private bool _released;

        public readonly PracticeRoutes Routes = new PracticeRoutes();
        public readonly FlyController Fly = new FlyController();

        public PracticeMode(PersonalBests pbs, Action<string, bool> notify)
        {
            _pbs = pbs;
            _notify = notify;
        }

        public bool Active { get; private set; }
        public PracticeRoute Selected { get; private set; }
        public AttemptState Attempt { get; private set; }
        public double AttemptSeconds => _recorder.IsRecording ? _recorder.Seconds : 0;
        public GhostPlayback Ghost => _ghost;

        public void Enter(PlayerRef player)
        {
            if (Active) return;
            Active = true;
            SyncMap(player);
            _notify("Practice mode: the run recording is paused", false);
        }

        public void Exit()
        {
            if (!Active) return;
            StopAttempt();
            Fly.Exit();
            _startMarker.Hide();
            _endMarker.Hide();
            Active = false;
            _notify("Practice mode off: the run recording resumes", false);
        }

        public void Select(PracticeRoute route)
        {
            if (route == Selected) return;
            StopAttempt();
            Selected = route;
        }

        public void ToggleFly(PlayerRef player)
        {
            if (Fly.IsActive)
            {
                Fly.Exit();
                return;
            }
            StopAttempt();
            Fly.Enter(player);
        }

        public void NewRoute() => Select(Routes.Add());

        public void DeleteSelected()
        {
            if (Selected == null) return;
            StopAttempt();
            Routes.Remove(Selected);
            Selected = Routes.Routes.FirstOrDefault();
        }

        /// <summary>The phone's pose and the camera yaw become the selected route's start.</summary>
        public void SetStart(PlayerRef player)
        {
            if (!RequireRoute() || player?.Rb == null) return;
            var rb = player.Rb;
            // Placed while flying, the start would hang in the air: put it on the floor below.
            Selected.StartPosition = Fly.IsActive ? WorldIndex.OnFloor(rb.position) : rb.position;
            Selected.StartRotation = rb.rotation;
            Selected.StartCameraYaw = GameAccess.CameraYaw(player);
            Selected.HasStart = true;
            Routes.Save();
            _notify($"{Selected.Name}: start set", false);
        }

        /// <summary>The end zone of the selected route is centred on the phone.</summary>
        public void SetEnd(PlayerRef player)
        {
            if (!RequireRoute() || player?.Rb == null) return;
            Selected.EndCenter = player.Rb.position;
            Selected.HasEnd = true;
            Routes.Save();
            _notify($"{Selected.Name}: end set", false);
        }

        public void ResizeEnd(float delta)
        {
            if (!RequireRoute()) return;
            Selected.EndSize = Mathf.Clamp(Selected.EndSize + delta, PracticeRoutes.MinEndSize, PracticeRoutes.MaxEndSize);
            Routes.Save();
        }

        /// <summary>Teleports to the selected route's start; the timer waits for the first input.</summary>
        public void StartAttempt(PlayerRef player)
        {
            if (!RequireRoute() || player == null) return;
            if (!Selected.IsReady)
            {
                _notify($"{Selected.Name} needs a start and an end first", true);
                return;
            }
            StopAttempt();
            Fly.Exit();
            if (!GameAccess.Teleport(player, Selected.StartPosition, Selected.StartRotation, Selected.StartCameraYaw))
            {
                _notify("The player can't be teleported right now", true);
                return;
            }
            Attempt = AttemptState.Armed;
            _released = false;
        }

        public void StopAttempt()
        {
            if (_recorder.IsRecording) _recorder.Stop(GameAccess.Player);
            _ghost.Stop();
            Attempt = AttemptState.None;
        }

        /// <summary>Call every Update while a player exists.</summary>
        public void Update(PlayerRef player)
        {
            if (!Active) return;
            SyncMap(player);
            Fly.Move(player);
            if (Input.GetKeyDown(GhostPlugin.RestartKey.Value)) StartAttempt(player);
            DrawMarkers();
        }

        /// <summary>Call every FixedUpdate while a player exists.</summary>
        public void FixedUpdate(PlayerRef player)
        {
            if (!Active || Selected == null) return;
            if (Attempt == AttemptState.Armed)
            {
                bool input = GameAccess.HasMoveInput(player);
                if (!_released) _released = !input;
                else if (input) BeginTiming(player);
            }
            if (Attempt != AttemptState.Running) return;
            _recorder.Step(player);
            if (player.Rb != null && Selected.EndBounds.Contains(player.Rb.position)) Finish(player);
        }

        public void LateUpdate() => _ghost.Update();

        /// <summary>The scene went away with the player: nothing of the attempt is left.</summary>
        public void OnPlayerLost()
        {
            if (_recorder.IsRecording) _recorder.Stop(null);
            _ghost.Stop();
            Attempt = AttemptState.None;
            Fly.Exit();
        }

        private void BeginTiming(PlayerRef player)
        {
            _recorder.Start(player);
            Attempt = AttemptState.Running;
            var pb = _pbs.Load(Map(player), Difficulty(player), GhostCategory.Route, Selected.Id.ToString());
            if (pb != null) _ghost.StartRelative(pb, player);
        }

        private void Finish(PlayerRef player)
        {
            var rec = _recorder.Stop(player);
            Attempt = AttemptState.None;
            if (rec == null || rec.Samples.Count < 2) return;
            double time = rec.Duration;
            var split = rec.Slice(0, rec.Samples.Count - 1, GhostCategory.Route, Selected.Id.ToString(), time);
            double? previous = _pbs.Submit(split, out bool improved);
            string t = GhostController.FormatTime(time);
            if (improved)
                _notify(previous.HasValue
                    ? $"New PB! {Selected.Name} {t} (-{GhostController.FormatTime(previous.Value - time)})   {GhostPlugin.RestartKey.Value}: again"
                    : $"First PB: {Selected.Name} {t}   {GhostPlugin.RestartKey.Value}: again", false);
            else
                _notify($"{Selected.Name} {t} (PB {GhostController.FormatTime(previous ?? time)})   {GhostPlugin.RestartKey.Value}: again", false);
        }

        // A new map (another save, a custom map) brings its own routes.
        private void SyncMap(PlayerRef player)
        {
            string map = Map(player);
            if (map == Routes.MapId) return;
            StopAttempt();
            Routes.Load(map);
            Selected = Routes.Routes.FirstOrDefault();
        }

        private void DrawMarkers()
        {
            if (Selected != null && Selected.HasStart)
                _startMarker.ShowLine(Selected.StartPosition, Selected.StartPosition + Vector3.up * StartMarkerHeight, StartColor);
            else _startMarker.Hide();
            if (Selected != null && Selected.HasEnd)
                _endMarker.ShowBox(Selected.EndCenter, Vector3.one * Selected.EndSize, EndColor);
            else _endMarker.Hide();
        }

        private bool RequireRoute()
        {
            if (Selected != null) return true;
            _notify("Create a route first", true);
            return false;
        }

        private static string Map(PlayerRef player)
            => player.GameRefs?.WorldSaveManager?.GetCurrentProgress()?.MapId ?? "";

        private static string Difficulty(PlayerRef player)
            => player.GameRefs?.WorldSaveManager?.GetCurrentProgress()?.DifficultyType.ToString() ?? "";
    }
}
