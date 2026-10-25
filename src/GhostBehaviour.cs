using System;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Host component of the mod: hotkeys, the recorders on the physics tick, the ghosts
    /// after the game moved the player each frame, and the HUD and menu.
    /// </summary>
    public class GhostBehaviour : MonoBehaviour
    {
        public GhostBehaviour(IntPtr ptr) : base(ptr) { }

        // Field initializers, not a constructor: IL2CPP creates the component through the
        // IntPtr constructor only.
        private readonly GhostController _controller = new GhostController();
        private GhostMenu _menu;
        private GhostHud _hud;

        private void Start()
        {
            _menu = new GhostMenu(_controller);
            _hud = new GhostHud(_controller);
        }

        private void Update()
        {
            _controller.Update();
            if (_menu.Update()) return;
            if (Input.GetKeyDown(GhostPlugin.MenuKey.Value)) _menu.Toggle();
            if (Input.GetKeyDown(GhostPlugin.RecordKey.Value)) _controller.ToggleRecording();
            if (Input.GetKeyDown(GhostPlugin.PlayKey.Value)) _controller.TogglePlayLatest();
        }

        private void FixedUpdate() => _controller.FixedUpdate();

        private void LateUpdate() => _controller.LateUpdate();

        private void OnGUI()
        {
            _hud.Draw();
            _menu.Draw();
        }

        private void OnDestroy()
        {
            _menu?.ReleaseInput();
            _controller.Shutdown();
        }
    }
}
