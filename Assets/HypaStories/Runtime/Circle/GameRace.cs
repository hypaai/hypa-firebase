using System;
using System.Collections.Generic;
using HypaCore.Hybrid;

namespace HypaStories
{
    /// <summary>
    /// Points for a race (G11), from each game's own rules and nothing else (no network timing). Higher is better, as
    /// <c>stories/{id}.bestGameScore</c> keeps the higher. A tie on points goes to the faster player (their own game time).
    /// </summary>
    public static class RaceScore
    {
        /// <summary>Dodo: 10 a stone swallowed, and 10 a life left if Dodo falls (max 100).</summary>
        public static int Dodo(DodoRules r) => r == null ? 0 : r.Hits * 10 + (r.Phase == DodoPhase.Won ? r.LivesLeft * 10 : 0);
        public static bool DodoOver(DodoRules r) => r != null && (r.Phase == DodoPhase.Won || r.Phase == DodoPhase.Lost);

        /// <summary>Eshu's hat: 25 a round done and 25 for the end (max 100), less 10 a fight.</summary>
        public static int Hat(HatRules r) => r == null ? 0 : Math.Max(0, (r.Over ? HatRules.Rounds + 1 : r.Round) * 25 - r.Fights * 10);
        public static bool HatOver(HatRules r) => r != null && r.Over;

        /// <summary>The king's drum: 10 a dish served, less 3 a missed beat.</summary>
        public static int Drum(DrumRules r) => r == null ? 0 : Math.Max(0, r.Served * 10 - r.Misses * 3);
        public static bool DrumOver(DrumRules r) => r != null && r.Phase == DrumPhase.Won;
    }

    public enum RaceState { Idle, Countdown, Playing, Waiting, Result }

    /// <summary>One player in the race, for the live bar and the result card.</summary>
    public sealed class RaceStanding
    {
        public string Uid, Name;
        public int Score;
        /// <summary>Their own game time in ms once finished (0 while playing).</summary>
        public long FinishedMs;
        public bool Finished, Left;
    }

    public sealed class RaceResult
    {
        /// <summary>Null for a draw.</summary>
        public string WinnerUid;
        public string WinnerName;
        public int WinnerScore;
        public long WinnerMs;
        public bool YouWon, Draw;
        /// <summary>The other player left (or never joined): you win by finishing.</summary>
        public bool ByDefault;
    }

    /// <summary>
    /// A race in a Story Circle (G11): both players play the story's game at the same time, each on their own device, and the best
    /// result wins. Over <c>rooms/{code}/game/{uid} { startedAt, finishedMs, score }</c> (docs/contracts.md, section 4):
    /// <c>startedAt</c> is the round's agreed start in server time (the same for both), <c>finishedMs</c> is 0 while playing and then
    /// the player's own game time, <c>score</c> the game's points so far.
    /// <para>Either player can start a round (<see cref="Start"/>: the host taps Play together, either taps Rematch); the other joins
    /// it when their entry with a newer <c>startedAt</c> arrives. Both count down to that server time (<see cref="CountdownSeconds"/>),
    /// then <see cref="Go"/> starts the game locally. A player who leaves (or drops) mid-game forfeits: the other finishes and
    /// wins.</para>
    /// Call <see cref="Tick"/> every frame. Events come on the main thread (from <see cref="IRoomSync"/> and <see cref="Tick"/>).
    /// </summary>
    public sealed class GameRace : IDisposable
    {
        /// <summary>Time for the round to reach the other device before the count starts.</summary>
        public const int LeadMs = 1000;
        /// <summary>3-2-1.</summary>
        public const int CountdownMs = 3000;
        /// <summary>Live score updates are sent at most this often.</summary>
        public const int LiveEveryMs = 1000;
        /// <summary>A round that started longer ago than this is not joined (an old entry replayed when a stream reconnects).</summary>
        public const int JoinLateMs = 5000;
        /// <summary>The game time written by a player who leaves mid-game (with 0 points): a forfeit, never a real time.</summary>
        public const long ForfeitMs = 99_999_999;

        readonly IRoomSync _room;
        readonly string _uid;
        readonly Func<long> _localMs;
        readonly Dictionary<string, GameResult> _entries = new Dictionary<string, GameResult>();
        IReadOnlyDictionary<string, RoomPlayer> _players = new Dictionary<string, RoomPlayer>();
        long _round;
        int _myScore, _sentScore = -1;
        long _myFinishedMs, _lastSent = long.MinValue;
        bool _myDone;

        public RaceState State { get; private set; } = RaceState.Idle;
        /// <summary>The current round's start, server time (Unix ms).</summary>
        public long StartAtServerMs => _round;
        public RaceResult Result { get; private set; }

        public event Action<RaceState> StateChanged;
        /// <summary>The count reached zero: start the game now.</summary>
        public event Action Go;
        /// <summary>The live bar changed (scores, someone finished or left).</summary>
        public event Action StandingsChanged;

        /// <param name="localMs">This device's own clock in ms (for live-update pacing only, never for the score).</param>
        public GameRace(IRoomSync room, string uid, IReadOnlyDictionary<string, RoomPlayer> players = null, Func<long> localMs = null)
        {
            _room = room;
            _uid = uid;
            if (players != null) _players = players;
            _localMs = localMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _room.GameResultReceived += OnEntry;
            _room.PlayersChanged += OnPlayers;
        }

        public void Dispose()
        {
            _room.GameResultReceived -= OnEntry;
            _room.PlayersChanged -= OnPlayers;
        }

        /// <summary>Seconds left in the count (3, 2, 1), or 0 once it is time to go.</summary>
        public int CountdownSeconds => State != RaceState.Countdown ? 0 : (int)Math.Max(0, Math.Ceiling((_round - _room.ServerTimeMs) / 1000.0));

        public bool CanStart => _room.Code != null && (State == RaceState.Idle || State == RaceState.Result);

        /// <summary>Starts a round for everyone in the room: Play together, or Rematch.</summary>
        public void Start()
        {
            if (!CanStart) return;
            BeginRound(_room.ServerTimeMs + LeadMs + CountdownMs);
            if (_room.IsHost) Observe(_room.SetStateAsync(RoomStates.Game));
        }

        /// <summary>Back to the fire: this device stops racing. Mid-game, that is a forfeit.</summary>
        public void Leave()
        {
            if (State == RaceState.Idle) return;
            if (State == RaceState.Countdown || State == RaceState.Playing) Send(0, ForfeitMs); // the other player wins by finishing
            Set(RaceState.Idle);
        }

        public void Tick()
        {
            if (State == RaceState.Countdown && _room.ServerTimeMs >= _round)
            {
                Set(RaceState.Playing);
                Go?.Invoke();
            }
        }

        /// <summary>The game's points so far, while playing. Sent at most once a second, and only when it changed.</summary>
        public void Report(int score)
        {
            if (State != RaceState.Playing) return;
            if (score != _myScore) { _myScore = score; StandingsChanged?.Invoke(); }
            var now = _localMs();
            if (_myScore == _sentScore || (_lastSent != long.MinValue && now - _lastSent < LiveEveryMs)) return;
            _lastSent = now;
            Send(_myScore, 0);
        }

        /// <summary>The game is over on this device: its points and the player's own game time.</summary>
        public void Finish(int score, long gameMs)
        {
            if (State != RaceState.Playing) return;
            _myScore = score;
            _myFinishedMs = Math.Max(1, gameMs); // 0 means "still playing"
            _myDone = true;
            Send(_myScore, _myFinishedMs);
            Set(RaceState.Waiting);
            StandingsChanged?.Invoke();
            Evaluate();
        }

        /// <summary>You and the other player (if any), for the live bar.</summary>
        public IReadOnlyList<RaceStanding> Standings()
        {
            var list = new List<RaceStanding>
            {
                new RaceStanding { Uid = _uid, Name = NameOf(_uid), Score = _myScore, FinishedMs = _myDone ? _myFinishedMs : 0, Finished = _myDone },
            };
            var them = Opponent();
            if (them != null)
            {
                _entries.TryGetValue(them, out var e);
                list.Add(new RaceStanding
                {
                    Uid = them, Name = NameOf(them), Score = e?.score ?? 0, FinishedMs = e?.finishedMs ?? 0,
                    Finished = e != null && e.finishedMs > 0 && e.finishedMs < ForfeitMs,
                    Left = Gone(them) || (e != null && e.finishedMs >= ForfeitMs),
                });
            }
            return list;
        }

        /// <summary>
        /// Who won: the higher score; on equal points, the faster game time; then a draw. A player who left without finishing loses to
        /// one who finished. Pure.
        /// </summary>
        public static RaceResult Decide(RaceStanding you, RaceStanding them)
        {
            RaceResult Win(RaceStanding w, bool byDefault = false) => new RaceResult
            {
                WinnerUid = w.Uid, WinnerName = w.Name, WinnerScore = w.Score, WinnerMs = w.FinishedMs, YouWon = w == you, ByDefault = byDefault,
            };
            if (them == null || (!them.Finished && them.Left)) return Win(you, byDefault: true);
            if (you.Score != them.Score) return Win(you.Score > them.Score ? you : them);
            if (you.FinishedMs != them.FinishedMs) return Win(you.FinishedMs < them.FinishedMs ? you : them);
            return new RaceResult { Draw = true, WinnerScore = you.Score, WinnerMs = you.FinishedMs };
        }

        // ---------- room events ----------

        void OnEntry(string uid, GameResult r)
        {
            if (uid == _uid || r == null) return;
            if (r.startedAt > _round && r.finishedMs == 0 && (State == RaceState.Idle || State == RaceState.Result)
                && r.startedAt >= _room.ServerTimeMs - JoinLateMs)
                BeginRound(r.startedAt); // the other player started a round: join it
            if (r.startedAt != _round) return; // an older round
            _entries[uid] = r;
            StandingsChanged?.Invoke();
            Evaluate();
        }

        void OnPlayers(IReadOnlyDictionary<string, RoomPlayer> players)
        {
            _players = players;
            if (State == RaceState.Idle) return;
            StandingsChanged?.Invoke();
            Evaluate();
        }

        void BeginRound(long startAt)
        {
            _round = startAt;
            _entries.Clear();
            _myScore = 0; _sentScore = -1; _myFinishedMs = 0; _myDone = false; _lastSent = long.MinValue;
            Result = null;
            Send(0, 0);
            Set(RaceState.Countdown);
            StandingsChanged?.Invoke();
        }

        void Evaluate()
        {
            if (!_myDone || State == RaceState.Result || State == RaceState.Idle) return;
            var standings = Standings();
            var them = standings.Count > 1 ? standings[1] : null;
            if (them != null && !them.Finished && !them.Left) return; // still playing
            Result = Decide(standings[0], them);
            Set(RaceState.Result);
        }

        /// <summary>The other player: someone in this round, else anyone else in the room.</summary>
        string Opponent()
        {
            foreach (var uid in _entries.Keys) if (uid != _uid) return uid;
            foreach (var uid in _players.Keys) if (uid != _uid) return uid;
            return null;
        }

        bool Gone(string uid) => !_players.TryGetValue(uid, out var p) || p == null || !p.online || (_room.Code != null && !_entries.ContainsKey(uid) && State != RaceState.Countdown);

        string NameOf(string uid) => _players.TryGetValue(uid, out var p) && p != null && !string.IsNullOrEmpty(p.name) ? p.name : uid == _uid ? "You" : "Friend";

        void Send(int score, long finishedMs)
        {
            if (_room.Code == null) return;
            _sentScore = finishedMs > 0 ? -1 : score;
            Observe(_room.SubmitGameAsync(new GameResult { startedAt = _round, finishedMs = finishedMs, score = score }));
        }

        void Set(RaceState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        static void Observe(System.Threading.Tasks.Task t) => _ = ObserveAsync(t);

        static async System.Threading.Tasks.Task ObserveAsync(System.Threading.Tasks.Task t)
        {
            try { await t; }
            catch (Exception e) { UnityEngine.Debug.LogWarning("Story Circle race: couldn't send a result (" + e.GetType().Name + ")."); }
        }
    }
}
