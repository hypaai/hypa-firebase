using System.Collections.Generic;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    /// <summary>A head or hand: where it is in the camp's space, or not tracked.</summary>
    public struct PosePart
    {
        public bool Tracked;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>
    /// The other player's head and hands, smoothed (Story Circle, G09). Poses arrive at most 10 a second, stamped with the sender's
    /// server time; this plays them back <see cref="DelayMs"/> late, blending between the two frames around that moment, so the
    /// figure moves smoothly instead of jumping each frame. Late or repeated frames are dropped. Pure, unit tested.
    /// </summary>
    public sealed class PoseTrack
    {
        /// <summary>How far behind the newest pose the figure is shown: a little more than one frame at 10 a second.</summary>
        public const int DelayMs = 150;
        const int Keep = 10;
        readonly List<PoseFrame> _frames = new List<PoseFrame>();

        public int Count => _frames.Count;

        public void Push(PoseFrame frame)
        {
            if (frame == null || !PoseFrame.Valid(frame.h) || !PoseFrame.Valid(frame.l) || !PoseFrame.Valid(frame.r)) return;
            if (_frames.Count > 0 && frame.t <= _frames[_frames.Count - 1].t) return; // out of order or repeated
            _frames.Add(frame);
            if (_frames.Count > Keep) _frames.RemoveAt(0);
        }

        public void Clear() => _frames.Clear();

        /// <summary>The pose to show at <paramref name="serverNowMs"/> (this device's estimate of server time). False before the first pose.</summary>
        public bool Sample(long serverNowMs, out PosePart head, out PosePart left, out PosePart right)
        {
            head = left = right = default;
            if (_frames.Count == 0) return false;
            var at = serverNowMs - DelayMs;
            PoseFrame a = _frames[0], b = a;
            var u = 0f;
            var newest = _frames[_frames.Count - 1];
            if (at >= newest.t) a = b = newest; // nothing newer yet: hold the last pose
            else if (at > a.t)
            {
                for (var i = 1; i < _frames.Count; i++)
                {
                    if (_frames[i].t < at) continue;
                    a = _frames[i - 1]; b = _frames[i];
                    u = (float)(at - a.t) / (b.t - a.t);
                    break;
                }
            }
            head = Mix(a.h, b.h, u);
            left = Mix(a.l, b.l, u);
            right = Mix(a.r, b.r, u);
            return true;
        }

        static PosePart Part(float[] p) => p == null || p.Length != PoseFrame.Length ? default
            : new PosePart { Tracked = true, Position = new Vector3(p[0], p[1], p[2]), Rotation = new Quaternion(p[3], p[4], p[5], p[6]).normalized };

        static PosePart Mix(float[] a, float[] b, float u)
        {
            var pa = Part(a);
            var pb = Part(b);
            if (pa.Tracked && pb.Tracked)
                return new PosePart { Tracked = true, Position = Vector3.Lerp(pa.Position, pb.Position, u), Rotation = Quaternion.Slerp(pa.Rotation, pb.Rotation, u) };
            return u < 0.5f ? pa : pb; // a hand appearing or going: switch halfway rather than fly in from the origin
        }
    }
}
