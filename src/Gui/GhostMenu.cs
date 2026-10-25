using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// The mod's window, in tabs: every PB to watch, the imported and hand-made ghosts,
    /// teleports to the level starts and to the NPCs, practice routes, and the settings.
    /// Drawn with the Map Editor's theme and hit-tested widgets (GuiWindowHelper), since
    /// stock IMGUI controls don't fire in this build.
    ///
    /// Like the Map Editor's free cursor, while the mouse is on the menu the game's own
    /// keyboard and mouse are held (GameAccess.HoldGameInput), so the camera doesn't turn
    /// and clicks don't reach the game; the cursor key hands the mouse back to the camera
    /// with the menu still open, and back again.
    /// </summary>
    internal sealed class GhostMenu
    {
        private enum Tab { Pbs, Ghosts, Levels, Npcs, Practice, Settings }

        private const int WINDOW_ID = 51701;
        private const float W = 540f;
        private const float H = 600f;
        private const float PAD = 14f;
        private const float CONTENT_Y = 72f;
        private const float ROW_H = 40f;
        private const float TP_ROW_H = 34f;

        // Keyboard keys only: everything from Mouse0 on is mouse and joystick buttons.
        private static readonly KeyCode[] CapturableKeys = ((KeyCode[])Enum.GetValues(typeof(KeyCode)))
            .Where(k => k != KeyCode.None && k < KeyCode.Mouse0).Distinct().ToArray();

        private static readonly (Tab tab, string label)[] Tabs =
        {
            (Tab.Pbs, "PBs"), (Tab.Ghosts, "Ghosts"), (Tab.Levels, "Levels"), (Tab.Npcs, "NPCs"),
            (Tab.Practice, "Practice"), (Tab.Settings, "Settings"),
        };

        private readonly GhostController _c;
        private readonly GuiWindowHelper _win;
        private readonly GUI.WindowFunction _windowDelegate;
        private readonly (ConfigEntry<KeyCode> entry, string label)[] _binds;

        private Tab _tab = Tab.Pbs;
        private int _top;
        private string _selectedPath;
        private ConfigEntry<KeyCode> _listening;
        private bool _cursorFree;
        private float _deleteConfirmUntil;
        private const int ROUTE_ROWS = 5;
        private float _pbCountAt;
        private int _areaPbs, _challengePbs;

        private GUIStyle _styleSection, _styleRow, _styleRowBold, _styleSmall, _styleRight, _styleRec, _styleRightSmall;
        private bool _stylesReady;

        public bool Visible { get; private set; }

        public GhostMenu(GhostController controller)
        {
            _c = controller;
            _win = new GuiWindowHelper(new Rect(Screen.width / 2f - W / 2f, Screen.height / 2f - H / 2f, W, H));
            _windowDelegate = new Action<int>(WindowFunction);
            _binds = new[]
            {
                (GhostPlugin.MenuKey, "Open / close this menu"),
                (GhostPlugin.RecordKey, "Record / stop and save"),
                (GhostPlugin.PlayKey, "Play the latest recording / stop a ghost"),
                (GhostPlugin.CursorKey, "Menu open: mouse on the menu / on the camera"),
                (GhostPlugin.RestartKey, "Practice: restart the route"),
            };
        }

        public void Toggle()
        {
            if (Visible) Close();
            else Open();
        }

        private void Open()
        {
            Visible = true;
            _top = 0;
            _c.Library.Refresh();
            SetCursorFree(true);
        }

        private void Close()
        {
            Visible = false;
            _listening = null;
            SetCursorFree(false);
        }

        // Free: the mouse works the menu and the game's input is held. Not free: the game
        // has its input and its locked cursor back (unless its own pause menu wants the
        // cursor), and the menu stays on screen without reacting.
        private void SetCursorFree(bool free)
        {
            _cursorFree = free;
            _win.Interactive = free;
            GameAccess.HoldGameInput(free);
            if (free)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (!GameManager.IsPauseMenuShown)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        /// <summary>Gives the game its input back, e.g. when the mod shuts down with the menu open.</summary>
        public void ReleaseInput() => GameAccess.HoldGameInput(false);

        /// <summary>
        /// Call from Update before the hotkeys: takes the key for a pending rebind. True while
        /// a rebind is pending, including the frame that completes it, so the key just bound
        /// doesn't also fire its action.
        /// </summary>
        public bool Update()
        {
            if (_listening == null)
            {
                if (Visible && Input.GetKeyDown(GhostPlugin.CursorKey.Value)) SetCursorFree(!_cursorFree);
                return false;
            }
            foreach (var key in CapturableKeys)
            {
                if (!Input.GetKeyDown(key)) continue;
                CaptureKey(key);
                break;
            }
            return true;
        }

        private void CaptureKey(KeyCode key)
        {
            if (key == KeyCode.Escape)
            {
                _listening = null;
                return;
            }
            foreach (var (entry, label) in _binds)
            {
                if (entry != _listening && entry.Value == key)
                {
                    _c.Notify($"'{key}' is already bound to: {label}", true);
                    return;
                }
            }
            // Setting the value saves FIHGhost.cfg (ConfigFile.SaveOnConfigSet).
            _listening.Value = key;
            _c.Notify($"Bound: {key}");
            _listening = null;
        }

        public void Draw()
        {
            if (!Visible) return;
            InitStyles();
            if (_cursorFree)
            {
                // The game re-locks the cursor aggressively; free it on every pass.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            _win.BeginFrame();
            _win.WindowRect = GUI.Window(WINDOW_ID, _win.WindowRect, _windowDelegate, "", GuiTheme.WindowStyle);
        }

        private void WindowFunction(int id)
        {
            GuiTheme.WindowFrame(W, H, "FIH GHOST");
            if (_win.Button(new Rect(W - 38, 2, 34, GuiTheme.TITLE_H - 4), "X"))
            {
                Close();
                return;
            }

            DrawTabs();
            switch (_tab)
            {
                case Tab.Pbs: DrawPbsTab(); break;
                case Tab.Ghosts: DrawGhostsTab(); break;
                case Tab.Levels: DrawLevelsTab(); break;
                case Tab.Npcs: DrawNpcsTab(); break;
                case Tab.Practice: DrawPracticeTab(); break;
                case Tab.Settings: DrawSettingsTab(); break;
            }
            DrawFooter();
        }

        private void DrawTabs()
        {
            float y = GuiTheme.TITLE_H + 8;
            float bw = (W - 2 * PAD - 6 * (Tabs.Length - 1)) / Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                var (tab, label) = Tabs[i];
                if (_win.ToggleButton(new Rect(PAD + i * (bw + 6), y, bw, 28), label, _tab == tab) && _tab != tab)
                {
                    _tab = tab;
                    _top = 0;
                    _listening = null;
                    if (tab == Tab.Pbs || tab == Tab.Ghosts) _c.Library.Refresh();
                    // Rescanned on opening: an index built while the level was still
                    // spawning its objects could be missing some.
                    if (tab == Tab.Levels || tab == Tab.Npcs) _c.World.Refresh(GameAccess.Player, force: true);
                }
            }
        }

        // --- PBs ------------------------------------------------------------------------

        private void DrawPbsTab()
        {
            float y = CONTENT_Y;
            var run = _c.Session.Run;
            if (run != null)
            {
                double? fullRun = _c.Pbs.TimeOf(run.MapId, run.Difficulty, GhostCategory.FullRun, "");
                // Counting lists the PB folder: once a second, not on every OnGUI pass.
                if (Time.unscaledTime >= _pbCountAt)
                {
                    _pbCountAt = Time.unscaledTime + 1f;
                    _c.Pbs.Count(run.MapId, run.Difficulty, out _areaPbs, out _challengePbs);
                }
                GUI.Label(new Rect(PAD, y, W - 2 * PAD, 20),
                    $"Now: {run.MapId}  ·  {run.Difficulty}   —   full run {(fullRun.HasValue ? GhostController.FormatTime(fullRun.Value) : "—")}, " +
                    $"{_areaPbs} level PBs, {_challengePbs} challenge PBs", _styleSmall);
            }
            else
                GUI.Label(new Rect(PAD, y, W - 2 * PAD, 20),
                    "Load into a level: every single-player session is recorded and timed automatically.", _styleSmall);
            y += 24;

            GUI.Label(new Rect(PAD, y + 4, 120, 20), "Race your PBs:", _styleRow);
            float bw = (W - 2 * PAD - 120 - 16) / 3f;
            float x = PAD + 120;
            if (RaceToggle(new Rect(x, y, bw, 26), "Full run", GhostPlugin.RaceFullRun)
                | RaceToggle(new Rect(x + bw + 8, y, bw, 26), "Levels", GhostPlugin.RaceAreas)
                | RaceToggle(new Rect(x + 2 * (bw + 8), y, bw, 26), "Challenges", GhostPlugin.RaceChallenges))
                _c.Racer.ApplyToggles(GameAccess.Player);
            y += 36;

            var pbs = _c.Library.Entries.Where(e => e.Source == GhostLibrary.Source.Pb).ToList();
            DrawSection($"ALL PBS ({pbs.Count})", ref y);
            y = DrawGhostList(pbs, y, 7, "No PBs yet: finish a level, a challenge or the game.");
            DrawWatchButtons(pbs, y, canImport: false);
        }

        // '|' above, not '||': every toggle is drawn even when an earlier one was clicked.
        private bool RaceToggle(Rect r, string label, ConfigEntry<bool> entry)
        {
            if (!_win.ToggleButton(r, $"{label}: {(entry.Value ? "ON" : "OFF")}", entry.Value)) return false;
            entry.Value = !entry.Value;
            return true;
        }

        // --- Ghosts ---------------------------------------------------------------------

        private void DrawGhostsTab()
        {
            float y = CONTENT_Y;
            if (_c.LastImportedPath != null)
            {
                _selectedPath = _c.LastImportedPath;
                _top = 0;
                // Selected once; the import result is not re-applied every frame.
                _c.ClearLastImported();
            }

            var ghosts = _c.Library.Entries.Where(e => e.Source != GhostLibrary.Source.Pb).ToList();
            DrawSection($"IMPORTED AND RECORDED ({ghosts.Count})", ref y);
            y = DrawGhostList(ghosts, y, 6, $"Nothing yet: import a ghost, or record one with {GhostPlugin.RecordKey.Value}.");
            y = DrawWatchButtons(ghosts, y, canImport: true);

            DrawSection("MANUAL RECORDING", ref y);
            var rec = _c.Recorder;
            if (rec.IsRecording)
                GUI.Label(new Rect(PAD, y, 260, 28), $"● REC  {GhostController.FormatTime(rec.Seconds)}", _styleRec);
            else
                GUI.Label(new Rect(PAD, y, W - 2 * PAD - 160, 28), "Records your phone from now until you stop.", _styleSmall);
            if (rec.IsRecording) GUI.backgroundColor = GuiTheme.Danger;
            if (_win.Button(new Rect(W - PAD - 150, y, 150, 28), rec.IsRecording ? "Stop & save" : "Record"))
                _c.ToggleRecording();
            GUI.backgroundColor = Color.white;
        }

        // --- shared ghost list ----------------------------------------------------------

        private float DrawGhostList(List<GhostLibrary.Entry> entries, float y, int rows, string empty)
        {
            var listRect = new Rect(PAD, y, W - 2 * PAD, rows * ROW_H);
            GuiTheme.Fill(listRect, GuiTheme.Inset);
            Scroll(listRect, entries.Count, rows);

            if (entries.Count == 0)
                GUI.Label(new Rect(PAD + 10, y + 8, W - 2 * PAD - 20, 22), empty, _styleSmall);

            for (int i = 0; i < rows && _top + i < entries.Count; i++)
            {
                var entry = entries[_top + i];
                var row = new Rect(listRect.x + 2, y + i * ROW_H + 2, listRect.width - 4, ROW_H - 4);
                if (entry.Path == _selectedPath) GuiTheme.Fill(row, GuiTheme.AccentSoft);
                else if (_win.IsHovered(row)) GuiTheme.Fill(row, GuiTheme.PanelHover);
                if (_win.Clicked(row)) _selectedPath = entry.Path;

                var h = entry.Header;
                GUI.Label(new Rect(row.x + 8, row.y + 1, row.width - 110, 18), entry.Title, _styleRowBold);
                GUI.Label(new Rect(row.xMax - 100, row.y + 1, 92, 18), GhostController.FormatTime(entry.Time), _styleRight);
                GUI.Label(new Rect(row.x + 8, row.y + 18, row.width - 16, 16),
                    $"{h.MapId}  ·  {h.Difficulty}  ·  {h.RecordedAtUtc.ToLocalTime():dd/MM HH:mm}  ·  {h.CheckpointsUsed} checkpoints",
                    _styleSmall);
            }
            return y + listRect.height + 8;
        }

        private float DrawWatchButtons(List<GhostLibrary.Entry> entries, float y, bool canImport)
        {
            var selected = entries.FirstOrDefault(e => e.Path == _selectedPath);
            float bw = 92f;
            if (_win.Button(new Rect(PAD, y, bw, 28), "Play") && Require(selected)) _c.Play(selected);
            if (_win.Button(new Rect(PAD + (bw + 6), y, bw, 28), "Spectate") && Require(selected)) _c.Spectate(selected);
            if (_win.Button(new Rect(PAD + 2 * (bw + 6), y, bw, 28), "Stop")) _c.StopPlayback();
            if (_win.Button(new Rect(W - PAD - (canImport ? 2 * bw + 6 : bw), y, bw, 28), "Export") && Require(selected))
                _c.Export(selected);
            if (canImport && _win.Button(new Rect(W - PAD - bw, y, bw, 28), "Import")) _c.Import();
            y += 34;

            string playing = _c.PlayingName == null
                ? "Play: the ghost runs where it was recorded.  Spectate: the camera follows it."
                : $"{(_c.Spectating ? "Spectating" : "Playing")} {_c.PlayingName}  " +
                  $"{GhostController.FormatTime(_c.Playback.Seconds)} / {GhostController.FormatTime(_c.Playback.Duration)}";
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 20), playing, _styleSmall);
            return y + 28;
        }

        private bool Require(GhostLibrary.Entry selected)
        {
            if (selected != null) return true;
            _c.Notify("Pick a ghost in the list first", true);
            return false;
        }

        // --- Levels ---------------------------------------------------------------------

        private void DrawLevelsTab()
        {
            float y = CONTENT_Y;
            var areas = _c.World.Areas;
            var run = _c.Session.Run;
            DrawSection($"LEVELS ({areas.Count})", ref y);

            int rows = 12;
            var listRect = new Rect(PAD, y, W - 2 * PAD, rows * TP_ROW_H);
            GuiTheme.Fill(listRect, GuiTheme.Inset);
            Scroll(listRect, areas.Count, rows);
            if (areas.Count == 0)
                GUI.Label(new Rect(PAD + 10, y + 8, W - 2 * PAD - 20, 22), "Load into a level first.", _styleSmall);

            for (int i = 0; i < rows && _top + i < areas.Count; i++)
            {
                var area = areas[_top + i];
                var row = new Rect(listRect.x + 2, y + i * TP_ROW_H + 2, listRect.width - 4, TP_ROW_H - 4);
                bool current = _c.Session.MaxArea == area.Id || _c.Session.OpenArea == area.Id;
                if (current) GuiTheme.Fill(row, GuiTheme.AccentSoft);
                GUI.Label(new Rect(row.x + 8, row.y, 220, row.height), $"{area.Id}.  {area.Name}", _styleRowBold);
                string pb = run != null ? _c.PbText(run, GhostCategory.Area, area.Id.ToString()) : "";
                GUI.Label(new Rect(row.x + 230, row.y, 150, row.height), pb, _styleSmall);
                if (DrawTeleportButton(row, area.HasStart))
                    _c.TeleportToArea(area);
            }
            y += listRect.height + 6;
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 18),
                "The timer starts when you move and runs until the next level.",
                _styleSmall);
        }

        // --- NPCs -----------------------------------------------------------------------

        private void DrawNpcsTab()
        {
            float y = CONTENT_Y;
            var npcs = _c.World.Npcs;
            var run = _c.Session.Run;
            DrawSection($"NPCS ({npcs.Count}, {npcs.Count(n => n.ChallengeId != null)} with a challenge)", ref y);

            int rows = 12;
            var listRect = new Rect(PAD, y, W - 2 * PAD, rows * TP_ROW_H);
            GuiTheme.Fill(listRect, GuiTheme.Inset);
            Scroll(listRect, npcs.Count, rows);
            if (npcs.Count == 0)
                GUI.Label(new Rect(PAD + 10, y + 8, W - 2 * PAD - 20, 22), "Load into a level first.", _styleSmall);

            for (int i = 0; i < rows && _top + i < npcs.Count; i++)
            {
                var npc = npcs[_top + i];
                var row = new Rect(listRect.x + 2, y + i * TP_ROW_H + 2, listRect.width - 4, TP_ROW_H - 4);
                GUI.Label(new Rect(row.x + 8, row.y, 220, row.height), npc.Name, _styleRowBold);
                string challenge = npc.ChallengeId == null ? "no challenge"
                    : run != null ? _c.PbText(run, GhostCategory.Challenge, npc.ChallengeId) : "";
                GUI.Label(new Rect(row.x + 230, row.y, 150, row.height), challenge, _styleSmall);
                if (DrawTeleportButton(row, true))
                    _c.TeleportToNpc(npc);
            }
            y += listRect.height + 6;
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 18),
                "Only the challenge is timed after this teleport; to time levels, use a level teleport.", _styleSmall);
        }

        // The row's Teleport button, right-aligned. True when it was clicked.
        private bool DrawTeleportButton(Rect row, bool hasTarget)
        {
            if (!hasTarget)
            {
                GUI.Label(new Rect(row.xMax - 96, row.y, 90, row.height), "no start", _styleSmall);
                return false;
            }
            return _win.Button(new Rect(row.xMax - 96, row.y + 2, 90, row.height - 4), "Teleport");
        }

        // --- Practice -------------------------------------------------------------------

        private void DrawPracticeTab()
        {
            float y = CONTENT_Y;
            var practice = _c.Practice;
            var player = GameAccess.Player;

            if (practice.Active) GUI.backgroundColor = GuiTheme.Danger;
            if (_win.Button(new Rect(PAD, y, 210, 28), practice.Active ? "Exit practice mode" : "Enter practice mode"))
                _c.TogglePractice();
            GUI.backgroundColor = Color.white;
            GUI.Label(new Rect(PAD + 222, y, W - 2 * PAD - 222, 28),
                practice.Active ? "The run recording is paused." : "Routes with a start and an end, raced against their PB.",
                _styleSmall);
            y += 38;
            if (!practice.Active)
            {
                GUI.Label(new Rect(PAD, y, W - 2 * PAD, 60),
                    "In practice mode: fly to place a route's start and end, then try it as many times as you\n" +
                    $"want with {GhostPlugin.RestartKey.Value}. The timer starts when you move and stops in the end zone;\n" +
                    "your best attempt is kept as the route's PB ghost.", _styleSmall);
                return;
            }

            if (_win.ToggleButton(new Rect(PAD, y, 120, 26), $"Fly: {(practice.Fly.IsActive ? "ON" : "OFF")}", practice.Fly.IsActive))
                practice.ToggleFly(player);
            GUI.Label(new Rect(PAD + 132, y, W - 2 * PAD - 132, 26),
                $"WASD move · E / Q up / down · Shift faster · {GhostPlugin.CursorKey.Value}: mouse to the camera", _styleSmall);
            y += 34;

            var routes = practice.Routes.Routes;
            var run = _c.Session.Run;
            DrawSection($"ROUTES ({routes.Count})", ref y);
            var listRect = new Rect(PAD, y, W - 2 * PAD, ROUTE_ROWS * TP_ROW_H);
            GuiTheme.Fill(listRect, GuiTheme.Inset);
            Scroll(listRect, routes.Count, ROUTE_ROWS);
            if (routes.Count == 0)
                GUI.Label(new Rect(PAD + 10, y + 8, W - 2 * PAD - 20, 22), "No routes yet: create one with New route.", _styleSmall);
            for (int i = 0; i < ROUTE_ROWS && _top + i < routes.Count; i++)
            {
                var route = routes[_top + i];
                var row = new Rect(listRect.x + 2, y + i * TP_ROW_H + 2, listRect.width - 4, TP_ROW_H - 4);
                if (route == practice.Selected) GuiTheme.Fill(row, GuiTheme.AccentSoft);
                else if (_win.IsHovered(row)) GuiTheme.Fill(row, GuiTheme.PanelHover);
                if (_win.Clicked(row)) practice.Select(route);
                GUI.Label(new Rect(row.x + 8, row.y, 150, row.height), route.Name, _styleRowBold);
                GUI.Label(new Rect(row.x + 160, row.y, 200, row.height),
                    $"start {(route.HasStart ? "set" : "—")}   end {(route.HasEnd ? "set" : "—")}   zone {route.EndSize:0} m", _styleSmall);
                if (player != null)
                    GUI.Label(new Rect(row.xMax - 140, row.y, 132, row.height),
                        RoutePb(player, route), _styleRight);
            }
            y += listRect.height + 8;

            if (_win.Button(new Rect(PAD, y, 120, 28), "New route")) practice.NewRoute();
            bool confirming = Time.unscaledTime < _deleteConfirmUntil;
            if (confirming) GUI.backgroundColor = GuiTheme.Danger;
            if (_win.Button(new Rect(PAD + 128, y, 150, 28), confirming ? "Click to confirm" : "Delete route"))
            {
                // Two clicks: a route and its PB are not something to lose by a misclick.
                if (confirming)
                {
                    practice.DeleteSelected();
                    _deleteConfirmUntil = 0f;
                }
                else if (practice.Selected != null) _deleteConfirmUntil = Time.unscaledTime + 3f;
            }
            GUI.backgroundColor = Color.white;
            y += 36;

            var selected = practice.Selected;
            DrawSection(selected != null ? $"EDIT {selected.Name.ToUpperInvariant()}" : "EDIT", ref y);
            float bw = 112f;
            if (_win.Button(new Rect(PAD, y, bw, 28), "Set start here")) practice.SetStart(player);
            if (_win.Button(new Rect(PAD + bw + 6, y, bw, 28), "Set end here")) practice.SetEnd(player);
            GUI.Label(new Rect(PAD + 2 * (bw + 6) + 6, y, 80, 28), $"End zone {(selected != null ? selected.EndSize : PracticeRoutes.DefaultEndSize):0} m", _styleSmall);
            if (_win.Button(new Rect(W - PAD - 70, y, 32, 28), "-")) practice.ResizeEnd(-1f);
            if (_win.Button(new Rect(W - PAD - 32, y, 32, 28), "+")) practice.ResizeEnd(1f);
            y += 36;

            GUI.backgroundColor = GuiTheme.Success;
            if (_win.Button(new Rect(PAD, y, W - 2 * PAD, 30), $"Start attempt   ({GhostPlugin.RestartKey.Value})"))
                practice.StartAttempt(player);
            GUI.backgroundColor = Color.white;
            y += 36;
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 18),
                "The start is the phone's spot and facing; the end zone is centred on the phone.", _styleSmall);
        }

        private string RoutePb(PlayerRef player, PracticeRoute route)
        {
            var progress = player.GameRefs?.WorldSaveManager?.GetCurrentProgress();
            double? pb = _c.Pbs.TimeOf(progress?.MapId ?? "", progress?.DifficultyType.ToString() ?? "",
                GhostCategory.Route, route.Id.ToString());
            return pb.HasValue ? $"PB {GhostController.FormatTime(pb.Value)}" : "no PB yet";
        }

        // --- Settings -------------------------------------------------------------------

        private void DrawSettingsTab()
        {
            float y = CONTENT_Y;
            DrawSection("KEYS", ref y);
            foreach (var (entry, label) in _binds)
            {
                GUI.Label(new Rect(PAD, y + 3, 300, 22), label, _styleRow);
                bool listening = _listening == entry;
                if (_win.ToggleButton(new Rect(W - PAD - 160, y, 160, 26),
                        listening ? "[ press a key... ]" : $"[ {entry.Value} ]", listening))
                    _listening = listening ? null : entry;
                y += 31;
            }
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 18),
                "Click a key, then press the new one (Esc cancels). Saved instantly.", _styleSmall);
            y += 30;

            DrawSection("DISPLAY", ref y);
            var hud = GhostPlugin.ShowHud;
            if (_win.ToggleButton(new Rect(PAD, y, 200, 26), $"HUD panel: {(hud.Value ? "ON" : "OFF")}", hud.Value))
                hud.Value = !hud.Value;
            y += 40;

            DrawSection("TROUBLESHOOTING", ref y);
            if (_win.Button(new Rect(PAD, y, 160, 26), "Diagnostics dump"))
            {
                Diagnostics.Dump();
                _c.Notify("Diagnostics written to LogOutput.log");
            }
            if (_win.Button(new Rect(PAD + 170, y, 160, 26), "Rescan level"))
            {
                _c.World.Refresh(GameAccess.Player, force: true);
                _c.Notify("Level starts and NPCs rescanned");
            }
        }

        // --- helpers --------------------------------------------------------------------

        private void Scroll(Rect listRect, int count, int rows)
        {
            float scroll = _win.ScrollDeltaOver(listRect);
            if (scroll != 0f) _top -= Math.Sign(scroll);
            _top = Mathf.Clamp(_top, 0, Mathf.Max(0, count - rows));
        }

        private void DrawFooter()
        {
            float y = H - 34;
            GuiWindowHelper.FillRect(new Rect(1, y - 6, W - 2, 1), GuiTheme.Line);
            GUI.Label(new Rect(PAD, y, W - 2 * PAD - 170, 26), _c.Message, _styleSmall);
            GUI.Label(new Rect(W - PAD - 170, y, 170, 26),
                _cursorFree ? $"{GhostPlugin.CursorKey.Value}: mouse to the camera" : $"{GhostPlugin.CursorKey.Value}: mouse to the menu",
                _styleRightSmall);
        }

        private void DrawSection(string title, ref float y)
        {
            GUI.Label(new Rect(PAD, y, W - 2 * PAD, 18), title, _styleSection);
            GuiWindowHelper.FillRect(new Rect(PAD, y + 19, W - 2 * PAD, 1), GuiTheme.Line);
            y += 26;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;
            _styleSection = GuiTheme.MakeText(11, TextAnchor.MiddleLeft, true);
            _styleSection.normal.textColor = GuiTheme.TextDim;
            _styleRow = GuiTheme.MakeText(13, TextAnchor.MiddleLeft, false);
            _styleRowBold = GuiTheme.MakeText(13, TextAnchor.MiddleLeft, true);
            _styleRight = GuiTheme.MakeText(13, TextAnchor.MiddleRight, true);
            _styleSmall = GuiTheme.MakeText(11, TextAnchor.MiddleLeft, false);
            _styleSmall.normal.textColor = GuiTheme.TextDim;
            _styleRightSmall = GuiTheme.MakeText(11, TextAnchor.MiddleRight, false);
            _styleRightSmall.normal.textColor = GuiTheme.TextDim;
            _styleRec = GuiTheme.MakeText(15, TextAnchor.MiddleLeft, true);
            _styleRec.normal.textColor = GuiTheme.Danger;
            _stylesReady = true;
        }
    }
}
