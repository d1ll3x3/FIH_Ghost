using System;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Access to the game state the ghost depends on (see NOTAS_FASE1.md for where each
    /// member was mapped), plus the few things the mod does to the player: teleport it and
    /// hold the game's input while a menu needs the mouse.
    /// </summary>
    internal static class GameAccess
    {
        /// <summary>The local player once the game finished setting it up, else null.</summary>
        public static PlayerRef Player
        {
            get
            {
                var player = PlayerRef.LocalPlayer;
                return player != null && player.IsFullyInitialized ? player : null;
            }
        }

        // The ghost is single-player only (agreed design): in a lobby the mod stays idle.
        public static bool IsMultiplayer => (AsList(PlayerRef.ConnectedPlayersClient)?.Count ?? 0) > 1;

        // The interop wraps IReadOnlyList<T> as an interface type without Count or an indexer;
        // the game returns its List<T> fields through it, so the concrete list is always there.
        public static Il2CppSystem.Collections.Generic.List<T> AsList<T>(
            Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
            => list?.TryCast<Il2CppSystem.Collections.Generic.List<T>>();

        // Bits of EHS.PlayerButtons.ButtonType that move the phone (FIHAgent's PlayerIO):
        // jump, and secondary (auto-align on the ground, spin boost in the air).
        private const int JumpBit = 4;
        private const int SecondaryBit = 65536;

        /// <summary>
        /// Whether the player is giving the phone input: stick, jump or secondary, read from
        /// the rawData the game reads its input from. Turning the camera doesn't count.
        /// </summary>
        public static bool HasMoveInput(PlayerRef player)
        {
            var handler = player.InputHandler;
            if (handler == null) return false;
            var data = handler.rawData;
            return data.moveInputSBytes.X != 0 || data.moveInputSBytes.Y != 0
                   || (data.Buttons.HeldMask & (JumpBit | SecondaryBit)) != 0;
        }

        /// <summary>
        /// Teleports the phone the way the game's own checkpoint load does, through its
        /// PlayerTeleportController (which keeps the NetworkTransform in sync), as FIHAgent
        /// does, then stops it and turns the camera to <paramref name="cameraYaw"/>.
        /// False when the player can't be teleported.
        /// </summary>
        public static bool Teleport(PlayerRef player, Vector3 pos, Quaternion rot, float cameraYaw)
        {
            var teleport = player.PlayerTeleportController;
            var rb = player.Rb;
            if (teleport == null || rb == null) return false;
            teleport.TeleportTo(pos, rot);
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            var cam = player.GameRefs?.CameraManager;
            cam?.SyncPosAfterTeleport(pos);
            var orbital = Orbital(player);
            if (orbital != null)
            {
                var axis = orbital.HorizontalAxis;
                axis.Value = Mathf.Repeat(cameraYaw + 180f, 360f) - 180f;
                orbital.HorizontalAxis = axis;
            }
            return true;
        }

        /// <summary>The camera's orbit yaw in degrees, or the phone's heading without one.</summary>
        public static float CameraYaw(PlayerRef player)
        {
            var orbital = Orbital(player);
            return orbital != null ? orbital.HorizontalAxis.Value : player.transform.eulerAngles.y;
        }

        private static Unity.Cinemachine.CinemachineOrbitalFollow Orbital(PlayerRef player)
            => player.GameRefs?.CameraManager?.MainCinemachineCamera?.GetComponent<Unity.Cinemachine.CinemachineOrbitalFollow>();

        private static bool _gameInputHeld;

        /// <summary>
        /// Holds or releases the game's keyboard and mouse (the Input System devices), so a
        /// menu can use the mouse without the camera turning or the game reading clicks as
        /// button presses (the Map Editor's free cursor does the same). The mod's own
        /// controls read legacy Input, which keeps working.
        /// </summary>
        public static void HoldGameInput(bool hold)
        {
            if (hold == _gameInputHeld) return;
            _gameInputHeld = hold;
            try
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (hold)
                {
                    if (keyboard != null) UnityEngine.InputSystem.InputSystem.DisableDevice(keyboard);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.DisableDevice(mouse);
                }
                else
                {
                    if (keyboard != null) UnityEngine.InputSystem.InputSystem.EnableDevice(keyboard);
                    if (mouse != null) UnityEngine.InputSystem.InputSystem.EnableDevice(mouse);
                }
            }
            catch (Exception ex)
            {
                GhostPlugin.Logger.LogWarning($"[INPUT] Could not {(hold ? "hold" : "release")} the game's input: {ex.Message}");
            }
        }
    }
}
