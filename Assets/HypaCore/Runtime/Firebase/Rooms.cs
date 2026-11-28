using System;
using System.Collections.Generic;
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
        /// <summary>0 to <see cref="RoomSeats.MaxPlayers"/> - 1. Seat 0 is the host's when the room is made; a seat stays with its player when the room is handed over.</summary>
        public int seat;
        /// <summary>
        /// Cleared by the server when the connection drops (<c>onDisconnect</c>). Where there is no <c>onDisconnect</c> (REST) the
        /// implementation keeps <c>players/{uid}/lastSeen</c> fresh, and reports a player whose <c>lastSeen</c> is more than
        /// <see cref="RoomPresence.TimeoutMs"/> old as offline here.
        /// </summary>
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

    /// <summary>
    /// <c>rooms/{code}/answers/{n}</c>: one sentence of the answer to a player's question, written by the player whose hand is up (the
    /// asker), so the other player can speak it too. <c>n</c> counts up through the room; the implementation picks it. The question
    /// itself is never sent.
    /// </summary>
    [Serializable]
    public sealed class RoomAnswer
    {
        /// <summary>Who asked and answered (the sender).</summary>
        public string uid;
        /// <summary>Which question of the room's story this answers, from 0.</summary>
        public int q;
        /// <summary>This sentence's place in the answer, from 0.</summary>
        public int i;
        /// <summary>One sentence, 1 to <see cref="MaxTextLength"/> characters.</summary>
        public string text;
        /// <summary>True on the answer's last sentence.</summary>
        public bool last;

        public const int MaxTextLength = 1000;
    }

    /// <summary>Seats at the fire.</summary>
    public static class RoomSeats
    {
        /// <summary>Players in a room (G09: 2; P06 raises it to 8, with the <c>seat</c> rule in database.rules.json).</summary>
        public const int MaxPlayers = 2;
        public static bool Valid(int seat) => seat >= 0 && seat < MaxPlayers;
    }

    /// <summary>How long a player may stay silent before the room treats them as gone.</summary>
    public static class RoomPresence
    {
        /// <summary>A player with no heartbeat (<c>lastSeen</c>) for this long is offline; the rules let the next player take the room then.</summary>
        public const long TimeoutMs = 10_000;
        /// <summary>How often a client without <c>onDisconnect</c> refreshes <c>lastSeen</c>.</summary>
        public const long HeartbeatMs = 3_000;

        /// <summary>
        /// Whether a player is still here. <paramref name="online"/> is the stored flag (<c>onDisconnect</c> clears it). A client with no
        /// <c>onDisconnect</c> (REST on a phone or Quest) also writes <paramref name="lastSeen"/>; once that is older than
        /// <see cref="TimeoutMs"/> the player is gone even if the flag is still true. <paramref name="serverNowMs"/> of
        /// <see cref="long.MaxValue"/> skips the heartbeat check. A player with no <paramref name="lastSeen"/> relies on the flag alone.
        /// </summary>
        public static bool IsOnline(bool online, long? lastSeen, long serverNowMs)
        {
            if (!online) return false;
            if (!lastSeen.HasValue || serverNowMs == long.MaxValue) return true;
            return serverNowMs - lastSeen.Value <= TimeoutMs;
        }
    }

    /// <summary>Who takes the room when its host goes (docs/contracts.md, section 4). The rules accept any online player, the first write wins.</summary>
    public static class RoomHandover
    {
        /// <summary>
        /// The player who should take the room: the online player other than the host with the lowest seat, or null if there is none.
        /// Every client works this out the same way, so the right one tries first. A host that is not in <paramref name="players"/>
        /// or is offline is gone.
        /// </summary>
        public static string NextHost(IReadOnlyDictionary<string, RoomPlayer> players, string hostUid)
        {
            string next = null;
            var seat = int.MaxValue;
            foreach (var pair in players)
            {
                if (pair.Key == hostUid || pair.Value == null || !pair.Value.online) continue;
                if (pair.Value.seat < seat || (pair.Value.seat == seat && string.CompareOrdinal(pair.Key, next) < 0)) { next = pair.Key; seat = pair.Value.seat; }
            }
            return next;
        }

        /// <summary>True when the host is missing from the room or offline, so another player may take it.</summary>
        public static bool HostGone(IReadOnlyDictionary<string, RoomPlayer> players, string hostUid)
            => hostUid != null && (!players.TryGetValue(hostUid, out var host) || host == null || !host.online);
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

        /// <summary>Where a share link points when the app is not itself a web page (the web build on Firebase Hosting).</summary>
        public const string DefaultSite = "https://hypa-firebase.web.app";

        /// <summary>The share link for a room: <c>{site}/circle/ABCDE</c>.</summary>
        public static string Link(string site, string code) => (string.IsNullOrEmpty(site) ? DefaultSite : site.TrimEnd('/')) + "/circle/" + code;

        /// <summary>The room code in a share link (<c>…/circle/ABCDE</c>, or <c>?circle=ABCDE</c>), or null.</summary>
        public static string FromLink(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            var path = uri.AbsolutePath.TrimEnd('/');
            var i = path.LastIndexOf("/circle/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return Normalize(path.Substring(i + "/circle/".Length));
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("circle=", StringComparison.OrdinalIgnoreCase)) return Normalize(Uri.UnescapeDataString(part.Substring(7)));
            return null;
        }

        /// <summary>The site of a web page's URL (scheme and host), for a share link from the page itself.</summary>
        public static string SiteOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    public enum RoomError { NotFound, Full, Expired, NotHost, NotInRoom, NotYourTurn }

    public sealed class RoomException : Exception
    {
        public RoomError Error { get; }
        public RoomException(RoomError error, string message) : base(message) { Error = error; }
    }
}
