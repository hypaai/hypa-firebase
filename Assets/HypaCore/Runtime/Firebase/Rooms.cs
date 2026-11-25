using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HypaCore.Hybrid
{
    /// <summary>Realtime Database <c>rooms/{code}</c> (docs/contracts.md, section 4).</summary>
    [Serializable]
    public sealed class RoomInfo
    {
        public string host;
        public string storyId;
        public string language;
        /// <summary>See <see cref="RoomStates"/>.</summary>
        public string state;
        /// <summary>Server time, Unix ms.</summary>
        public long createdAt;
    }

    /// <summary><c>rooms/{code}/clock</c>. The host writes it; everyone plays the story from it.</summary>
    [Serializable]
    public sealed class RoomClock
    {
        public int lineIndex;
        /// <summary>When <see cref="lineIndex"/> started, in server time (Unix ms).</summary>
        public long startedAtServerMs;
        public bool paused;

        /// <summary>How far into the current line the story is, in ms, given the server's time now.</summary>
        public long ElapsedMs(long serverNowMs) => paused ? 0 : Math.Max(0, serverNowMs - startedAtServerMs);
    }

    /// <summary><c>rooms/{code}/players/{uid}</c>. Each player writes only their own.</summary>
    [Serializable]
    public sealed class RoomPlayer
    {
        public string name;
        /// <summary>See <see cref="Platforms"/>.</summary>
        public string platform;
        /// <summary>0 (the host) or 1.</summary>
        public int seat;
        /// <summary>Cleared by the server when the connection drops (<c>onDisconnect</c>).</summary>
        public bool online;
        public bool handUp;
    }

    /// <summary>
    /// <c>rooms/{code}/poses/{uid}</c>: head and both hands, each <c>[x, y, z, qx, qy, qz, qw]</c> in the room's space, sent at most
    /// 10 times a second. A hand that isn't tracked is an empty array.
    /// </summary>
    [Serializable]
    public sealed class PoseFrame
    {
        public float[] h = new float[0];
        public float[] l = new float[0];
        public float[] r = new float[0];
        /// <summary>Sender's server time, Unix ms (to drop stale frames).</summary>
        public long t;

        public const int Length = 7;
        /// <summary>Minimum time between two frames (10 Hz).</summary>
        public const int IntervalMs = 100;

        public static float[] Pack(float x, float y, float z, float qx, float qy, float qz, float qw) => new[] { x, y, z, qx, qy, qz, qw };
        public static bool Valid(float[] part) => part != null && (part.Length == 0 || part.Length == Length);
    }

    /// <summary><c>rooms/{code}/game/{uid}</c>.</summary>
    [Serializable]
    public sealed class GameResult
    {
        public long startedAt;
        public long finishedMs;
        public int score;
    }

    public static class RoomStates
    {
        public const string Lobby = "lobby", Story = "story", Game = "game";
        public static bool Valid(string state) => state == Lobby || state == Story || state == Game;
    }

    public static class Platforms
    {
        public const string Quest = "quest", Android = "android", Ios = "ios", Web = "web", Editor = "editor";
    }

    /// <summary>
    /// Room codes: 5 letters from 20 consonants (no vowels, so a code never spells a word, and no Y), about 3.2 million codes.
    /// Codes are typed on a headset, so they are shown and compared in upper case. Rooms expire after <see cref="LifetimeMs"/>.
    /// </summary>
    public static class RoomCode
    {
        public const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";
        public const int Length = 5;
        public const long LifetimeMs = 2L * 60 * 60 * 1000;
        static readonly Regex Pattern = new Regex("^[" + Alphabet + "]{" + Length + "}$");

        public static string New()
        {
            var bytes = new byte[Length];
            var chars = new char[Length];
            using (var rng = RandomNumberGenerator.Create())
            {
                for (int i = 0; i < Length; i++)
                {
                    // 240 is the largest multiple of 20 below 256: rejecting the rest keeps every letter equally likely
                    do rng.GetBytes(bytes, i, 1); while (bytes[i] >= 240);
                    chars[i] = Alphabet[bytes[i] % Alphabet.Length];
                }
            }
            return new string(chars);
        }

        /// <summary>Upper-cases and trims what was typed; null if it can't be a code.</summary>
        public static string Normalize(string typed)
        {
            var code = (typed ?? "").Trim().ToUpperInvariant();
            return Pattern.IsMatch(code) ? code : null;
        }

        public static bool IsExpired(long createdAtMs, long serverNowMs) => serverNowMs - createdAtMs > LifetimeMs;
    }

    public enum RoomError { NotFound, Full, Expired, NotHost, NotInRoom }

    public sealed class RoomException : Exception
    {
        public RoomError Error { get; }
        public RoomException(RoomError error, string message) : base(message) { Error = error; }
    }
}
