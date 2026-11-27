using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    /// <summary>The story side a <see cref="CircleSession"/> drives: <see cref="StoryPlayer"/>, or a fake in tests.</summary>
    public interface ICircleNarration
    {
        /// <summary>The line being (or last) told.</summary>
        int LineIndex { get; }
        int LineCount { get; }
        bool Paused { get; }
        bool Ended { get; }
        /// <summary>A line is being fetched, waited for or spoken.</summary>
        bool Narrating { get; }
        /// <summary>True for a guest: play only the line the host's clock names, then wait.</summary>
        bool Following { get; set; }
        /// <summary>Awaited with the line's index when its audio is ready, just before it plays.</summary>
        Func<int, CancellationToken, Task> LineGate { get; set; }
        /// <summary>Paused on this device by the player or the system (not for a question).</summary>
        event Action UserPaused;
        /// <summary>Play this line now (through <see cref="LineGate"/>), clearing a pause.</summary>
        void FollowTo(int lineIndex);
        void Pause();
        /// <summary>Stop the line being told for a question.</summary>
        void Interrupt();
        /// <summary>After a question: restart this line (a guest waits for the clock instead).</summary>
        void Resume(int lineIndex);
    }

    /// <summary>
    /// Story Circle (G09): two players at one fire, over <see cref="IRoomSync"/> (docs/contracts.md, section 4).
    /// <para>Clock: the host announces each line when its audio is ready, to start <see cref="LeadMs"/> later in server time, and
    /// waits for that moment; the guest plays the line it is told at that same moment. Each device speaks the line itself.
    /// A late or returning guest starts at the current line.</para>
    /// <para>Questions: a player raises a hand (<see cref="RequestAsk"/>); the lowest seat with a hand up has the turn
    /// (<see cref="TurnHolder"/>). The asker's device runs its own question (<see cref="BeginAsk"/>, then AskFlow) and shares the
    /// answer sentence by sentence (<see cref="OnLocalSentence"/>); the other device stops the story, speaks each sentence, and when
    /// the asker's hand is down says "Now, where was I…". The host then restarts the interrupted line and the guest follows.</para>
    /// <para>Handover: when the room passes to this device it stops following and carries the story on itself.</para>
    /// Events from <see cref="IRoomSync"/> arrive on the main thread, and so do this class's.
    /// </summary>
    public sealed class CircleSession : IDisposable
    {
        /// <summary>The host announces a line this long before it starts, so the guest hears it in time.</summary>
        public const int LeadMs = 500;
        /// <summary>A guest never waits longer than this for a line's start (a clock far ahead is wrong, not a reason to stall).</summary>
        public const int MaxWaitMs = 2000;
        /// <summary>After the asker's hand drops, how long to wait at most for the answer's last sentence (it travels apart from the hand).</summary>
        public const int AnswerGraceMs = 2000;
        const int GraceStepMs = 100;

        readonly IRoomSync _room;
        readonly IStorySpeech _speech;
        readonly Func<StoryLanguage> _language;
        readonly Func<int, CancellationToken, Task> _delay;
        readonly Func<int, int> _pick;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        ICircleNarration _story;
        RoomPlayer _me;
        IReadOnlyDictionary<string, RoomPlayer> _players = new Dictionary<string, RoomPlayer>();
        RoomClock _clock;
        int _appliedLine = -1;
        long _appliedStart = long.MinValue;
        bool _wasHost, _wantAsk, _relaying, _lastHeard;
        int _q, _interrupted = -1;
        Task _writes = Task.CompletedTask, _speaking = Task.CompletedTask;

        public string Uid { get; }
        /// <summary>The player whose question it is, or null.</summary>
        public string Turn { get; private set; }
        public RoomInfo Info { get; private set; }
        public IReadOnlyDictionary<string, RoomPlayer> Players => _players;
        public bool IsHost => _room.IsHost;
        public string Code => _room.Code;

        /// <summary>Who the storyteller should face: a player's uid during their question, null for the fire.</summary>
        public event Action<string> AttendChanged;
        /// <summary>This device has the turn: start the question now (AskFlow.BeginAsync).</summary>
        public event Action BeginAsk;
        /// <summary>This device lost a raised-hand race to a lower seat: cancel its question (AskFlow.Cancel).</summary>
        public event Action CancelAsk;
        /// <summary>A sentence of the other player's answer is being spoken (null when the answer is over), for the story panel.</summary>
        public event Action<string> AnswerShown;
        public event Action<RoomInfo> InfoChanged;
        public event Action<IReadOnlyDictionary<string, RoomPlayer>> PlayersChanged;
        /// <summary>This device is out of the room (it left, or the room closed).</summary>
        public event Action Closed;
        /// <summary>Another player's head and hands (uid, pose).</summary>
        public event Action<string, PoseFrame> PoseReceived;

        /// <summary>Sends this player's head and hands (at most 10 a second; the room drops the rest).</summary>
        public void SendPose(PoseFrame pose) { if (_room.Code != null) _room.SendPose(pose); }
        /// <summary>This device's estimate of the server's clock.</summary>
        public long ServerTimeMs => _room.ServerTimeMs;
        /// <summary>Host only: the room's state (lobby, story, game).</summary>
        public Task SetStateAsync(string state) => _room.IsHost ? _room.SetStateAsync(state) : Task.CompletedTask;

        /// <param name="uid">This device's signed-in uid.</param>
        /// <param name="speech">Speaks the other player's answer (the storyteller's voice).</param>
        /// <param name="delay">For tests; defaults to <see cref="HypaAsync.Delay"/>.</param>
        /// <param name="pick">For tests: which "Now, where was I…" line to say.</param>
        public CircleSession(IRoomSync room, string uid, IStorySpeech speech, Func<StoryLanguage> language,
            Func<int, CancellationToken, Task> delay = null, Func<int, int> pick = null)
        {
            _room = room ?? throw new ArgumentNullException(nameof(room));
            Uid = uid;
            _speech = speech;
            _language = language;
            _delay = delay ?? ((ms, ct) => HypaAsync.Delay(ms, ct));
            _pick = pick ?? (n => UnityEngine.Random.Range(0, n));
            _room.RoomChanged += OnRoom;
            _room.ClockChanged += OnClock;
            _room.PlayersChanged += OnPlayers;
            _room.AnswerReceived += OnAnswer;
            _room.Closed += OnClosed;
            _room.PoseReceived += OnPose;
        }

        void OnPose(string uid, PoseFrame pose) => PoseReceived?.Invoke(uid, pose);

        // ---------- the room ----------

        /// <summary>Creates a room for this story and language, and seats you as its host.</summary>
        public async Task<string> CreateAsync(string storyId, string language, RoomPlayer me)
        {
            _me = Copy(me);
            var code = await _room.CreateAsync(storyId, language, Copy(me));
            _wasHost = true;
            Info = new RoomInfo { host = Uid, storyId = storyId, language = language, state = RoomStates.Lobby, createdAt = _room.ServerTimeMs };
            return code;
        }

        /// <summary>Joins a room (<see cref="RoomException"/> NotFound, Full or Expired). Load its story, then <see cref="AttachStory"/>.</summary>
        public async Task<RoomInfo> JoinAsync(string code, RoomPlayer me)
        {
            _me = Copy(me);
            var info = await _room.JoinAsync(code, Copy(me));
            Info = info;
            _wasHost = _room.IsHost;
            return info;
        }

        public Task LeaveAsync() => _room.LeaveAsync();

        /// <summary>
        /// Connects the loaded story. The host starts it as usual (each line is then announced); a guest jumps to the current line
        /// if the story is already under way, or waits for the host's first line.
        /// </summary>
        public void AttachStory(ICircleNarration story)
        {
            DetachStory();
            _story = story;
            story.LineGate = Gate;
            story.UserPaused += OnUserPaused;
            _wasHost = _room.IsHost;
            story.Following = !_wasHost;
            if (!_wasHost) ApplyClock();
        }

        public void DetachStory()
        {
            if (_story == null) return;
            _story.LineGate = null;
            _story.Following = false;
            _story.UserPaused -= OnUserPaused;
            _story = null;
        }

        public void Dispose()
        {
            _room.RoomChanged -= OnRoom;
            _room.ClockChanged -= OnClock;
            _room.PlayersChanged -= OnPlayers;
            _room.AnswerReceived -= OnAnswer;
            _room.Closed -= OnClosed;
            _room.PoseReceived -= OnPose;
            DetachStory();
            _cts.Cancel();
            _cts.Dispose();
        }

        // ---------- the clock ----------

        async Task Gate(int line, CancellationToken ct)
        {
            if (_room.Code == null) return;
            if (_room.IsHost)
            {
                var clock = new RoomClock { lineIndex = line, startedAtServerMs = _room.ServerTimeMs + LeadMs, paused = false };
                _clock = clock;
                _appliedLine = line; _appliedStart = clock.startedAtServerMs;
                Observe(_room.SetClockAsync(clock), "story clock");
                await _delay(LeadMs, ct);
                LogStart(line, clock.startedAtServerMs);
                return;
            }
            var c = _clock;
            if (c == null || c.paused || c.lineIndex != line) { LogStart(line, 0); return; } // not the line the host named: play at once
            var wait = c.startedAtServerMs - _room.ServerTimeMs;
            if (wait > 0) await _delay((int)Math.Min(wait, MaxWaitMs), ct);
            LogStart(line, c.startedAtServerMs);
        }

        /// <summary>
        /// One log line per story line, on both devices: compare the two "starts at server" times for the same line to measure how
        /// far apart they speak (the ~300 ms target). "late" is how far after the agreed start this device began.
        /// </summary>
        void LogStart(int line, long agreedMs)
        {
            var now = _room.ServerTimeMs;
            Debug.Log($"Story Circle: line {line + 1} starts at server {now} ({(IsHost ? "host" : "guest")}" +
                      (agreedMs > 0 ? $", {now - agreedMs} ms late)" : ", no agreed start)"));
        }

        void OnClock(RoomClock c)
        {
            _clock = c;
            if (!_room.IsHost) ApplyClock();
        }

        /// <summary>A guest catches up with the host's clock, unless a question is on here first.</summary>
        void ApplyClock()
        {
            var c = _clock;
            if (_story == null || c == null) return;
            if (c.paused) { _story.Pause(); return; }
            if (Turn == Uid || _relaying) return; // applied when the question is over
            if (c.lineIndex == _appliedLine && c.startedAtServerMs == _appliedStart) return;
            _appliedLine = c.lineIndex; _appliedStart = c.startedAtServerMs;
            _story.FollowTo(c.lineIndex);
        }

        void OnUserPaused()
        {
            if (!_room.IsHost || _room.Code == null || Turn != null || _story == null) return;
            WritePausedClock(_story.LineIndex);
        }

        void WritePausedClock(int line)
        {
            var c = new RoomClock { lineIndex = Math.Max(line, 0), startedAtServerMs = _clock?.startedAtServerMs ?? _room.ServerTimeMs, paused = true };
            _clock = c;
            Observe(_room.SetClockAsync(c), "story clock");
        }

        // ---------- the host ----------

        void OnRoom(RoomInfo info)
        {
            Info = info;
            InfoChanged?.Invoke(info);
            var host = _room.IsHost;
            if (host == _wasHost) return;
            _wasHost = host;
            if (_story == null) return;
            _story.Following = !host;
            if (!host || Turn != null || _story.Narrating || _story.Ended) return;
            // Taken over: carry the story on from where the old host left it (the interrupted line if it was paused for a
            // question or a pause, else the line after the one just told).
            var c = _clock;
            var next = c != null && c.paused ? c.lineIndex : Math.Min(_story.LineIndex + 1, _story.LineCount - 1);
            _story.FollowTo(Math.Max(next, 0));
        }

        // ---------- questions ----------

        /// <summary>The player whose question it is: the online player with a hand up in the lowest seat, or null. Every device agrees.</summary>
        public static string TurnHolder(IReadOnlyDictionary<string, RoomPlayer> players)
        {
            string holder = null;
            var seat = int.MaxValue;
            foreach (var pair in players)
            {
                var p = pair.Value;
                if (p == null || !p.online || !p.handUp) continue;
                if (p.seat < seat || (p.seat == seat && string.CompareOrdinal(pair.Key, holder) < 0)) { holder = pair.Key; seat = p.seat; }
            }
            return holder;
        }

        /// <summary>Raise a hand / tap Ask. False when another player's question is on (the turn is locked) or you are not in a room.</summary>
        public bool RequestAsk()
        {
            if (_room.Code == null || _me == null || Turn != null) return false;
            if (_wantAsk) return true;
            _wantAsk = true;
            _me.handUp = true;
            Observe(_room.UpdateMeAsync(Copy(_me)), "raised hand");
            return true;
        }

        /// <summary>Wire to AskFlow.Sentence: shares each sentence of this device's answer.</summary>
        public void OnLocalSentence(int index, string text, bool last)
        {
            if (Turn != Uid || string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > RoomAnswer.MaxTextLength) text = text.Substring(0, RoomAnswer.MaxTextLength);
            var sentence = new RoomAnswer { q = _q, i = index, text = text, last = last };
            _writes = SendAfter(_writes, sentence);
        }

        /// <summary>
        /// Wire to AskFlow.Ended: the question is over, so lower the hand, once every sentence is written (the rules refuse a sentence
        /// from a lowered hand). Also call it if a question this session asked for (<see cref="BeginAsk"/>) never started.
        /// </summary>
        public void OnLocalAskEnded()
        {
            if (!_wantAsk && Turn != Uid) return;
            _wantAsk = false;
            _ = LowerHandAfterWrites();
        }

        async Task LowerHandAfterWrites()
        {
            try { await _writes; } catch { /* logged where it failed */ }
            LowerHand();
        }

        void LowerHand(bool always = false)
        {
            if (_me == null || (!_me.handUp && !always)) return;
            _me.handUp = false;
            if (_room.Code != null) Observe(_room.UpdateMeAsync(Copy(_me)), "lowered hand");
        }

        void OnPlayers(IReadOnlyDictionary<string, RoomPlayer> players)
        {
            _players = players;
            PlayersChanged?.Invoke(players);
            var winner = TurnHolder(players);
            if (winner == Turn) return;
            var prev = Turn;
            Turn = winner;
            if (prev == Uid && winner != null)
            {
                // two hands went up at once and the lower seat's won: drop this question for theirs
                _wantAsk = false;
                LowerHand();
                CancelAsk?.Invoke();
            }
            else if (prev != null) EndTurn(prev);
            if (winner != null) StartTurn(winner);
        }

        void StartTurn(string uid)
        {
            AttendChanged?.Invoke(uid);
            _lastHeard = false;
            if (_story != null && _interrupted < 0) _interrupted = _story.LineIndex;
            if (_room.IsHost) WritePausedClock(_interrupted);
            if (uid == Uid)
            {
                if (_wantAsk) BeginAsk?.Invoke();
                else LowerHand(always: true); // a hand left up from before (a rejoin): nothing to ask
                return;
            }
            if (_wantAsk) { _wantAsk = false; LowerHand(); }
            _relaying = true;
            _story?.Interrupt();
        }

        void EndTurn(string asker)
        {
            AttendChanged?.Invoke(null);
            if (asker == Uid)
            {
                // AskFlow said "Now, where was I…" and restarted the line: the host announces it, a guest follows the clock
                _q++;
                _interrupted = -1;
                if (!_room.IsHost) ApplyClock();
                return;
            }
            _ = FinishRelay(_cts.Token);
        }

        void OnAnswer(RoomAnswer a)
        {
            if (a == null || string.IsNullOrEmpty(a.text)) return;
            _q = Math.Max(_q, a.q + 1);
            if (a.uid != Turn && !(_relaying && Turn == null)) return; // not the question being answered
            if (a.last) _lastHeard = true;
            _speaking = SpeakAfter(_speaking, a.text, _cts.Token);
        }

        async Task FinishRelay(CancellationToken ct)
        {
            try
            {
                for (var waited = 0; !_lastHeard && waited < AnswerGraceMs; waited += GraceStepMs) await _delay(GraceStepMs, ct);
                Task t;
                do { t = _speaking; try { await t; } catch { /* logged in SpeakAfter */ } } while (t != _speaking);
                if (Turn != null || ct.IsCancellationRequested) return; // another question started meanwhile
                var lines = StoryLanguages.Lines(_language().Code).Resume;
                if (lines != null && lines.Length > 0 && _speech != null)
                {
                    var i = _pick(lines.Length);
                    var line = await _speech.PrepareAsync(lines[i < 0 || i >= lines.Length ? 0 : i], _language(), ct);
                    await _speech.PlayAsync(line, null, ct);
                }
                AnswerShown?.Invoke(null);
                if (Turn != null || ct.IsCancellationRequested) return;
                _relaying = false;
                var line0 = _interrupted;
                _interrupted = -1;
                if (_story == null) return;
                if (_room.IsHost) _story.Resume(Math.Max(line0, 0));
                else ApplyClock();
            }
            catch (OperationCanceledException) { }
        }

        async Task SpeakAfter(Task previous, string text, CancellationToken ct)
        {
            if (_speech == null) return;
            var prepared = _speech.PrepareAsync(text, _language(), ct); // fetched now, played in turn
            try { await previous; } catch { /* an earlier sentence failing never blocks this one */ }
            try
            {
                var line = await prepared;
                if (ct.IsCancellationRequested) return;
                AnswerShown?.Invoke(text);
                await _speech.PlayAsync(line, null, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning("Story Circle: couldn't speak a shared answer (" + e.GetType().Name + ")."); }
        }

        async Task SendAfter(Task previous, RoomAnswer sentence)
        {
            try { await previous; } catch { /* each failure is logged on its own */ }
            try { await _room.SendAnswerAsync(sentence); }
            catch (Exception e) { Debug.LogWarning("Story Circle: couldn't share an answer sentence (" + e.GetType().Name + ")."); }
        }

        void OnClosed()
        {
            Turn = null;
            _relaying = false;
            _wantAsk = false;
            _interrupted = -1;
            _clock = null;
            _appliedLine = -1; _appliedStart = long.MinValue;
            if (_story != null) _story.Following = false;
            Closed?.Invoke();
        }

        static void Observe(Task t, string what) => _ = ObserveAsync(t, what);

        static async Task ObserveAsync(Task t, string what)
        {
            // The type only: a message could carry details we never want in a log.
            try { await t; }
            catch (Exception e) { Debug.LogWarning("Story Circle: couldn't send the " + what + " (" + e.GetType().Name + ")."); }
        }

        static RoomPlayer Copy(RoomPlayer p) => p == null ? new RoomPlayer() : new RoomPlayer { name = p.name, platform = p.platform, seat = p.seat, online = p.online, handUp = p.handUp };
    }
}
