using System;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    // Copied from the Map Editor's FlyController.cs (agreed: shared code is copied, not
    // linked), which ported it from the FlippingIsHard trainer, with its keys fixed to the
    // editor's defaults: WASD move, E / Q up / down, Left Shift faster.
    //
    // Free-fly to place practice points: kinematic body, no gravity, no collisions, moved
    // by position so the game's physics/prediction can't fight it. Keys are read through
    // legacy Input, which keeps working while the menu holds the game's input.
    internal sealed class FlyController
    {
        private const float Speed = 18f;
        private const float BoostFactor = 3f;

        private bool _active;
        private bool _wasKinematic;
        private bool _wasGravity = true;
        private IntPtr _player;

        public bool IsActive => _active;

        public void Enter(PlayerRef player)
        {
            var rb = player?.Rb;
            if (_active || rb == null) return;
            // Kept as fallbacks for Exit, which otherwise restores the game's own rule.
            _wasKinematic = rb.isKinematic;
            _wasGravity = rb.useGravity;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.detectCollisions = false;
            _player = player.Pointer;
            _active = true;
        }

        public void Exit()
        {
            if (!_active) return;
            _active = false;
            var player = GameAccess.Player;
            // After a scene change the flying body is gone and the new one was never touched.
            if (player == null || player.Pointer != _player || player.Rb == null) return;
            var rb = player.Rb;
            // Put back what the game itself would, not what was observed on Enter: fly can
            // start in the middle of a game routine that has the body changed for a moment
            // (a cannon launch switches collisions off and back on), and restoring that
            // snapshot left the player walking through everything. Collisions are always on
            // outside those routines; the body is kinematic only while the game freezes the
            // player (same rule as SceneZiplineSpline.ReleaseAttachedPlayer).
            rb.useGravity = _wasGravity;
            rb.detectCollisions = true;
            var networked = player.PlayerNetworked;
            rb.isKinematic = networked != null ? networked.IsComposedFrozen : _wasKinematic;
            // Do not carry fly movement into gameplay as residual momentum.
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Call every Update while active.</summary>
        public void Move(PlayerRef player)
        {
            if (!_active || player?.Rb == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            // Horizontal movement follows camera yaw, while vertical input remains world-up.
            // Removing pitch prevents looking down from turning forward movement into descent.
            Vector3 forward = cam.transform.forward;
            Vector3 right = cam.transform.right;
            forward.y = 0;
            right.y = 0;
            if (forward.magnitude > 0.001f) forward.Normalize();
            if (right.magnitude > 0.001f) right.Normalize();

            float speed = Speed * (Input.GetKey(KeyCode.LeftShift) ? BoostFactor : 1f);
            Vector3 velocity = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) velocity += forward * speed;
            if (Input.GetKey(KeyCode.S)) velocity -= forward * speed;
            if (Input.GetKey(KeyCode.A)) velocity -= right * speed;
            if (Input.GetKey(KeyCode.D)) velocity += right * speed;
            if (Input.GetKey(KeyCode.E)) velocity.y += speed;
            if (Input.GetKey(KeyCode.Q)) velocity.y -= speed;

            Vector3 displacement = velocity * Time.deltaTime;
            if (displacement == Vector3.zero) return;
            // Move by position (kinematic-safe; setting velocity would be ignored + warn).
            var rb = player.Rb;
            Vector3 target = rb.position + displacement;
            rb.position = target;
            player.transform.position = target;
        }
    }
}
