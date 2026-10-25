using UnityEngine;

namespace FIHGhost
{
    // Copied from the Map Editor's GuiWidgets.cs (agreed: shared code is copied, not
    // linked), trimmed to the widgets this mod uses and without its UI scale.
    //
    // IMGUI interactive controls (GUI.Button/Toggle/TextField) don't fire events in this
    // IL2CPP build, and GUI.DragWindow is equally broken. Every widget here is a GUI.Box
    // visual + a manual hit-test against legacy Input, the pattern proven in the trainer
    // and TAS tool. One GuiWindowHelper per window keeps the rect, drag state and the
    // click-consumed-this-frame flag.
    public class GuiWindowHelper
    {
        public Rect WindowRect;
        // False while the mouse turns the camera instead (the menu's cursor key): the window
        // is still drawn but nothing in it reacts.
        public bool Interactive = true;

        private bool _dragging = false;
        private Vector2 _dragOffset;
        private bool _clickHandledThisFrame = false;

        public GuiWindowHelper(Rect initialRect)
        {
            WindowRect = initialRect;
        }

        public static Vector2 MouseGuiPosition()
        {
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            return m;
        }

        private Rect ToScreen(Rect r) => new Rect(WindowRect.x + r.x, WindowRect.y + r.y, r.width, r.height);

        // Call once at the start of the window's Draw() (outside the window function).
        public void BeginFrame()
        {
            if (Event.current.type == EventType.Repaint)
                _clickHandledThisFrame = false;
            HandleDrag();
        }

        private void HandleDrag()
        {
            if (!Interactive) { _dragging = false; return; }
            if (Event.current.type != EventType.Repaint) return;

            Vector2 m = MouseGuiPosition();

            if (Input.GetMouseButtonDown(0) && !_dragging)
            {
                // Title bar = top strip of the window, minus the X button corner
                var titleRect = new Rect(WindowRect.x, WindowRect.y, WindowRect.width - 44, GuiTheme.TITLE_H);
                if (titleRect.Contains(m))
                {
                    _dragging = true;
                    _dragOffset = m - new Vector2(WindowRect.x, WindowRect.y);
                }
            }

            if (_dragging)
            {
                if (!Input.GetMouseButton(0))
                    _dragging = false;
                else
                {
                    WindowRect.x = Mathf.Clamp(m.x - _dragOffset.x, -WindowRect.width + 60, Screen.width - 60);
                    WindowRect.y = Mathf.Clamp(m.y - _dragOffset.y, 0, Screen.height - 40);
                }
            }
        }

        // Rect is in window-local coordinates.
        public bool Button(Rect rect, string text)
        {
            DrawButton(rect, text, false);
            return HitTest(rect);
        }

        // Button drawn in the accent colour when "on" (toggles, selected rows, listening).
        public bool ToggleButton(Rect rect, string text, bool on)
        {
            DrawButton(rect, text, on);
            return HitTest(rect);
        }

        // Callers colour a button by setting GUI.backgroundColor first (red REC...);
        // white means the theme's own colour.
        private void DrawButton(Rect rect, string text, bool on)
        {
            Color tint = GUI.backgroundColor;
            bool tinted = tint.r < 0.99f || tint.g < 0.99f || tint.b < 0.99f;
            Color fill = on ? GuiTheme.Accent
                : tinted ? Color.Lerp(GuiTheme.PanelRaised, new Color(tint.r, tint.g, tint.b, 1f), 0.7f)
                : GuiTheme.PanelRaised;
            if (IsHovered(rect))
                fill = Input.GetMouseButton(0) ? Color.Lerp(fill, Color.black, 0.18f) : Color.Lerp(fill, Color.white, 0.09f);
            GuiTheme.Fill(rect, fill);
            GUI.Label(rect, text, on ? GuiTheme.ButtonTextBold : GuiTheme.ButtonText);
        }

        public bool IsHovered(Rect rect) => Interactive && ToScreen(rect).Contains(MouseGuiPosition());

        // A click on an area drawn by the caller (list rows).
        public bool Clicked(Rect rect) => HitTest(rect);

        // Solid colour rectangle. Unlike a tinted GUI.Box it shows up on this skin.
        public static void FillRect(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // Scroll wheel delta while the mouse is over the given window-local rect.
        // Legacy Input — IMGUI ScrollWheel events never fire in this game.
        public float ScrollDeltaOver(Rect localRect)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (!Interactive || scroll == 0f) return 0f;
            return ToScreen(localRect).Contains(MouseGuiPosition()) ? scroll : 0f;
        }

        private bool HitTest(Rect rect)
        {
            if (!Interactive || _clickHandledThisFrame) return false;

            if (Input.GetMouseButtonDown(0) && Event.current.type == EventType.Repaint
                && ToScreen(rect).Contains(MouseGuiPosition()))
            {
                _clickHandledThisFrame = true;
                return true;
            }
            return false;
        }
    }
}
