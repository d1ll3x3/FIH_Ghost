using System;
using EHS;
using UnityEngine;

namespace FIHGhost
{
    /// <summary>
    /// Plays a recording on a ghost body, on one of two clocks:
    /// - relative: from the recording's first sample, starting now, on game time
    ///   (Time.timeAsDouble), the timeline the physics samples were taken on;
    /// - absolute: at the sample whose run-clock time matches a given clock (the player's
    ///   playtime), so a full-run ghost is wherever it was at this point of its run.
    /// Both freeze in the pause menu like the player does.
    /// </summary>
    internal sealed class GhostPlayback
    {
        private readonly GhostBody _body;
        private GhostRecording _rec;
        private Func<double> _clock;
        // Ghost time = _clock() + _offset.
        private double _offset;
        private int _index;

        public GhostPlayback(Color color) => _body = new GhostBody(color);

        public bool IsPlaying => _rec != null;
        public GhostRecording Recording => _rec;
        public Transform Body => _body.Root;
        // Seconds into the recording.
        public double Seconds => IsPlaying ? _clock() + _offset - _rec.StartTime : 0;
        public double Duration => _rec?.Duration ?? 0;

        public bool StartRelative(GhostRecording rec, PlayerRef player)
            => Start(rec, player, () => Time.timeAsDouble, rec.StartTime - Time.timeAsDouble);

        public bool StartAbsolute(GhostRecording rec, PlayerRef player, Func<double> clock)
            => Start(rec, player, clock, 0);

        private bool Start(GhostRecording rec, PlayerRef player, Func<double> clock, double offset)
        {
            Stop();
            if (rec.Samples.Count == 0 || !_body.Create(player)) return false;
            _rec = rec;
            _clock = clock;
            _offset = offset;
            _index = 0;
            Apply(_clock() + _offset);
            return true;
        }

        public void Stop()
        {
            _body.Destroy();
            _rec = null;
        }

        /// <summary>Call once per frame, after the game moved the player (LateUpdate).</summary>
        public void Update()
        {
            if (_rec == null) return;
            if (!_body.IsAlive)
            {
                GhostPlugin.Logger.LogInfo("[GHOST] Body destroyed with the scene; playback stopped.");
                Stop();
                return;
            }
            double t = _clock() + _offset;
            if (t > _rec.EndTime)
            {
                GhostPlugin.Logger.LogInfo($"[GHOST] Finished {_rec.Category} '{_rec.CategoryId}' ({_rec.Duration:0.00} s).");
                Stop();
                return;
            }
            Apply(t);
        }

        private void Apply(double t)
        {
            var samples = _rec.Samples;
            // Frame to frame the clock only moves forward a little; anything else (the
            // first frame of an absolute ghost, a clock that went back) gets a binary search.
            if (samples[_index].Time > t || (_index + 8 < samples.Count && samples[_index + 8].Time <= t))
                _index = IndexAtOrBefore(t);
            while (_index + 1 < samples.Count && samples[_index + 1].Time <= t) _index++;

            var a = samples[_index];
            // Hold the last pose before a teleport instead of sliding through the level.
            if (t <= a.Time || _index + 1 >= samples.Count || samples[_index + 1].Cut)
            {
                _body.SetPose(a.Position, a.Rotation);
                return;
            }
            var b = samples[_index + 1];
            float f = (float)((t - a.Time) / (b.Time - a.Time));
            _body.SetPose(Vector3.Lerp(a.Position, b.Position, f), Quaternion.Slerp(a.Rotation, b.Rotation, f));
        }

        private int IndexAtOrBefore(double t)
        {
            var samples = _rec.Samples;
            int lo = 0, hi = samples.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (samples[mid].Time <= t) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }
    }
}
