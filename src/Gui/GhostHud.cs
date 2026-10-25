using System.Collections.Generic;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Always-on panel in the bottom-left corner: the recording indicator, the area and
    /// challenge being timed against their PBs, the ghost being watched or raced, and the
    /// last message. Drawn on a panel so it reads over any scene.
    /// </summary>
    internal sealed class GhostHud
    {
        private const float X = 10f;
        // Gap to the bottom edge; the panel grows upwards from there.
        private const float MARGIN = 10f;
        private const float W = 470f;
        private const float LINE = 20f;
        private const float PAD = 8f;

        private readonly GhostController _c;
        private readonly List<(string text, GUIStyle style)> _lines = new List<(string, GUIStyle)>();
        private GUIStyle _styleRec, _styleText, _styleDim, _styleMessage;

        public GhostHud(GhostController controller) => _c = controller;

        public void Draw()
        {
            // In a level only: the key line alone would otherwise sit on the game's menus.
            if (!GhostPlugin.ShowHud.Value || GameAccess.Player == null) return;
            InitStyles();
            _lines.Clear();

            var practice = _c.Practice;
            if (practice.Active) AddPracticeLines(practice);
            bool auto = _c.Session.IsActive;
            bool manual = _c.Recorder.IsRecording;
            if (auto || manual)
            {
                string rec = auto ? $"● REC   {_c.Session.Run.MapId.Replace("OfficialMap_", "")} · {_c.Session.Run.Difficulty}" : "● REC";
                if (manual) rec += $"   + manual {GhostController.FormatTime(_c.Recorder.Seconds)}";
                _lines.Add((rec, _styleRec));
            }
            string area = _c.AreaStatus();
            if (area != null) _lines.Add((area, _styleText));
            string challenge = _c.ChallengeStatus();
            if (challenge != null) _lines.Add((challenge, _styleText));
            if (auto && !_c.Session.FullRunValid)
                _lines.Add(("Full run not counted: you teleported this run", _styleDim));

            if (_c.Playback.IsPlaying)
                _lines.Add(($"{(_c.Spectating ? "Spectating" : "Ghost")}: {_c.PlayingName}  " +
                            $"{GhostController.FormatTime(_c.Playback.Seconds)} / {GhostController.FormatTime(_c.Playback.Duration)}" +
                            $"  ({GhostPlugin.PlayKey.Value} stops)", _styleText));
            string racing = RacingText();
            if (racing != null) _lines.Add(($"Racing: {racing}", _styleDim));
            if (Time.unscaledTime - _c.MessageTime < GhostController.MessageSeconds)
                _lines.Add((_c.Message, _styleMessage));
            // Read from the binds every frame, so a rebind in the menu shows right away.
            _lines.Add(($"{GhostPlugin.MenuKey.Value} menu   ·   {GhostPlugin.RecordKey.Value} record / stop   ·   " +
                        $"{GhostPlugin.PlayKey.Value} play latest / stop ghost" +
                        (practice.Active ? $"   ·   {GhostPlugin.RestartKey.Value} restart route" : ""), _styleDim));

            float h = PAD * 2 + _lines.Count * LINE;
            float top = Screen.height - MARGIN - h;
            GuiTheme.Fill(new Rect(X, top, W, h), new Color(GuiTheme.Background.r, GuiTheme.Background.g, GuiTheme.Background.b, 0.82f));
            float y = top + PAD;
            foreach (var (text, style) in _lines)
            {
                GUI.Label(new Rect(X + PAD + 2, y, W - 2 * PAD, LINE), text, style);
                y += LINE;
            }
        }

        private void AddPracticeLines(PracticeMode practice)
        {
            var route = practice.Selected;
            string state = route == null ? "no route: create one in the menu"
                : !route.IsReady ? $"{route.Name}: needs a start and an end"
                : practice.Attempt == PracticeMode.AttemptState.Armed ? $"{route.Name}: move to start the timer"
                : practice.Attempt == PracticeMode.AttemptState.Running
                    ? $"{route.Name}  {GhostController.FormatTime(practice.AttemptSeconds)}"
                : $"{route.Name}: {GhostPlugin.RestartKey.Value} to start an attempt";
            _lines.Add(($"● PRACTICE   {state}", _styleRec));
            if (practice.Fly.IsActive) _lines.Add(("Flying: WASD move · E / Q up / down · Shift faster", _styleText));
        }

        private string RacingText()
        {
            var parts = new List<string>();
            if (_c.Racer.RunGhost.IsPlaying) parts.Add("full run PB");
            if (_c.Racer.AreaGhost.IsPlaying) parts.Add("area PB");
            if (_c.Racer.ChallengeGhost.IsPlaying) parts.Add("challenge PB");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private void InitStyles()
        {
            if (_styleRec != null) return;
            _styleRec = GuiTheme.MakeText(13, TextAnchor.MiddleLeft, true);
            _styleRec.normal.textColor = new Color(1f, 0.38f, 0.38f, 1f);
            _styleText = GuiTheme.MakeText(13, TextAnchor.MiddleLeft, false);
            _styleDim = GuiTheme.MakeText(12, TextAnchor.MiddleLeft, false);
            _styleDim.normal.textColor = GuiTheme.TextDim;
            _styleMessage = GuiTheme.MakeText(13, TextAnchor.MiddleLeft, true);
            _styleMessage.normal.textColor = new Color(1f, 0.85f, 0.4f, 1f);
        }
    }
}
