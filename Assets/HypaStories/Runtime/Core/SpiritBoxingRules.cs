using System;
using UnityEngine;

namespace HypaStories
{
    public enum SpiritAttack { Left, Right, Sweep }
    public enum SpiritFightPhase { Ready, Approach, Tell, Strike, Counter, Recover, LevelComplete, Won, Lost }

    /// <summary>The four practice spirits are game inventions, not additional characters claimed for the traditional tale.</summary>
    public sealed class SpiritBoxingRules
    {
        static readonly SpiritAttack[][] Patterns =
        {
            new[] { SpiritAttack.Left, SpiritAttack.Right },
            new[] { SpiritAttack.Right, SpiritAttack.Left, SpiritAttack.Right },
            new[] { SpiritAttack.Sweep, SpiritAttack.Left, SpiritAttack.Right },
            new[] { SpiritAttack.Left, SpiritAttack.Sweep, SpiritAttack.Right, SpiritAttack.Left },
        };
        public const int LevelCount = 4, MaxGuard = 5;
        /// <summary>Seconds between a cleared or lost spirit and the next one floating in. No button: the fight keeps going.</summary>
        public const float Breather = 3f;
        public int Level { get; private set; }
        public int Guard { get; private set; } = MaxGuard;
        public int Hits { get; private set; }
        /// <summary>Heads on this spirit: one, two, three, then seven.</summary>
        public int Heads => Level == LevelCount - 1 ? 7 : Level + 1;
        /// <summary>Punches that put one head down: 8, 4, 3, then 2.</summary>
        public int HitsPerHead => new[] { 8, 4, 3, 2 }[Level];
        /// <summary>Every head down: 8, 8, 9, then 14 punches.</summary>
        public int NeededHits => Heads * HitsPerHead;
        /// <summary>The head that counts now: a standing one, picked at random when your turn starts and again after every punch that lands, a different one when it can be.</summary>
        public int Mark { get; private set; }
        public bool Standing(int head) => head >= 0 && head < _headHits.Length && _headHits[head] < HitsPerHead;
        public int Beat { get; private set; }
        /// <summary>Rounds of the pattern already fought on this spirit. Each one shortens the tell a little.</summary>
        public int Round { get; private set; }
        /// <summary>Punches allowed in one counter window: one more than the spirit just threw, so you trade blow for blow.</summary>
        public int Combo => Patterns[Level].Length + 1;
        public SpiritAttack Attack => Patterns[Level][_order[Beat]];
        public SpiritFightPhase Phase { get; private set; } = SpiritFightPhase.Ready;
        public float Elapsed { get; private set; }
        public float TellDuration => Math.Max(0.6f, 1.25f - Level * 0.12f - Round * 0.05f);
        /// <summary>True for the frame a strike resolved: whether it was blocked or landed.</summary>
        public bool? LastStrikeBlocked { get; private set; }
        readonly System.Random _random;
        int[] _order = { 0, 1, 2, 3 };
        int[] _headHits = { 0 };
        int _windowPunches;
        bool _defended;

        /// <summary>From the second spirit on, each round shuffles the pattern so it is read, not memorised.</summary>
        public SpiritBoxingRules(int seed = 0) { _random = seed == 0 ? new System.Random() : new System.Random(seed); ResetHeads(); Shuffle(); }

        void ResetHeads() { _headHits = new int[Heads]; Mark = 0; }

        void PickMark()
        {
            var standing = new System.Collections.Generic.List<int>();
            for (var h = 0; h < Heads; h++) if (Standing(h)) standing.Add(h);
            if (standing.Count > 1) standing.Remove(Mark);
            if (standing.Count > 0) Mark = standing[_random.Next(standing.Count)];
        }

        void Shuffle()
        {
            var n = Patterns[Level].Length;
            _order = new int[n];
            for (var i = 0; i < n; i++) _order[i] = i;
            if (Level == 0) return;
            for (var i = n - 1; i > 0; i--) { var j = _random.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); }
        }

        public void Start()
        {
            if (Phase != SpiritFightPhase.Ready) return;
            Enter(SpiritFightPhase.Approach);
        }

        /// <summary>No clock advances while tracking, headset focus, or the player's starting position is unavailable.</summary>
        public void Tick(float dt, bool defended, bool paused)
        {
            LastStrikeBlocked = null;
            if (paused || dt <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            Elapsed += Math.Min(dt, 0.1f);
            switch (Phase)
            {
                case SpiritFightPhase.Approach:
                    if (Elapsed >= 2f) Enter(SpiritFightPhase.Tell);
                    break;
                case SpiritFightPhase.Tell:
                    if (Elapsed >= TellDuration) Enter(SpiritFightPhase.Strike);
                    break;
                case SpiritFightPhase.Strike:
                    _defended |= defended;
                    if (Elapsed >= 0.35f)
                    {
                        LastStrikeBlocked = _defended;
                        if (!_defended && --Guard <= 0) { Enter(SpiritFightPhase.Lost); break; }
                        Beat++;
                        if (Beat >= Patterns[Level].Length) { Beat = 0; Round++; Shuffle(); _windowPunches = 0; PickMark(); Enter(SpiritFightPhase.Counter); }
                        else Enter(SpiritFightPhase.Recover);
                    }
                    break;
                case SpiritFightPhase.Counter:
                    if (Elapsed >= 4f) Enter(SpiritFightPhase.Recover);
                    break;
                case SpiritFightPhase.Recover:
                    if (Elapsed >= 0.9f) Enter(SpiritFightPhase.Tell);
                    break;
                case SpiritFightPhase.LevelComplete:
                    if (Elapsed >= Breather) { Level++; ResetLevel(); }
                    break;
                case SpiritFightPhase.Lost:
                    if (Elapsed >= Breather) ResetLevel();
                    break;
            }
        }

        /// <summary>
        /// A punch on the marked head. When it has taken its share (the spirit's punches divided by its heads) it goes down,
        /// and the next mark is one of the heads left. Another head is marked after every punch that lands.
        /// </summary>
        public bool Punch()
        {
            if (Phase != SpiritFightPhase.Counter || !Standing(Mark)) return false;
            Hits++;
            _headHits[Mark]++;
            if (Hits >= NeededHits) Enter(Level == LevelCount - 1 ? SpiritFightPhase.Won : SpiritFightPhase.LevelComplete);
            else
            {
                PickMark();
                if (++_windowPunches >= Combo) Enter(SpiritFightPhase.Recover);
            }
            return true;
        }

        /// <summary>A punch on the wrong head during your turn: it does not count, and the spirit strikes back at once.</summary>
        public bool Suffer()
        {
            if (Phase != SpiritFightPhase.Counter) return false;
            if (--Guard <= 0) Enter(SpiritFightPhase.Lost);
            return true;
        }

        /// <summary>The next spirit, or the same one again after a loss, floats straight in.</summary>
        void ResetLevel() { Guard = MaxGuard; Hits = Beat = Round = 0; ResetHeads(); Shuffle(); Enter(SpiritFightPhase.Approach); }
        void Enter(SpiritFightPhase phase) { Phase = phase; Elapsed = 0f; _defended = false; }

        /// <summary>A swept, deliberate forward jab, not a resting hand or an opponent moving into one.</summary>
        public static bool HitsTarget(Vector3 previous, Vector3 current, Vector3 target, float dt, float radius = 0.16f, Vector3 headMotion = default)
        {
            if (dt <= 0f || dt > 0.12f) return false;
            var motion = current - previous;
            var relative = motion - headMotion;
            if (motion.z / dt < 0.1f || relative.z / dt < 0.2f || relative.magnitude / dt < 0.45f || motion.magnitude / dt > 8f) return false;
            var t = motion.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(target - previous, motion) / motion.sqrMagnitude) : 0f;
            return (previous + motion * t - target).sqrMagnitude <= radius * radius;
        }

        /// <summary>Hands are measured in a head-relative, yaw-only frame. Either tracked hand can cover the indicated side.</summary>
        public static bool Defending(SpiritAttack attack, Vector3 left, Vector3 right, float headDrop)
        {
            if (attack == SpiritAttack.Sweep) return headDrop >= 0.13f;
            bool Covers(Vector3 hand) => hand.y >= -0.32f && hand.y <= 0.25f && hand.z >= -0.05f && hand.z <= 0.45f
                && (attack == SpiritAttack.Left ? hand.x >= -0.48f && hand.x <= -0.02f : hand.x >= 0.02f && hand.x <= 0.48f);
            return Covers(left) || Covers(right);
        }
    }
}
