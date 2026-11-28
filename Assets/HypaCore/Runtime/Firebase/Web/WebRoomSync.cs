using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HypaCore.Hybrid.Web
{
    /// <summary>
    /// <see cref="IRoomSync"/> on the web (G09): the Firebase JS SDK's Realtime Database through <see cref="WebBridge"/>
    /// (<c>room*</c> methods in firebase-bridge.js). The SDK keeps presence (<c>onDisconnect</c> sets <c>online</c> to false) and
    /// the server time offset (<c>.info/serverTimeOffset</c>); this side takes the room when the host is gone and it is next
    /// (<see cref="RoomHandover"/>), as the REST client does. WebGL has one thread, so everything here runs on it.
    /// </summary>
    public sealed class WebRoomSync : IRoomSync
    {
        const int MaxCodeTries = 5;
        readonly IHypaAuth _auth;
        readonly Func<long> _localNow;
        readonly Dictionary<string, long> _poseSeen = new Dictionary<string, long>();
        readonly List<(string name, JToken value)> _early = new List<(string, JToken)>();
        RoomInfo _info;
        RoomPlayer _me;
        JObject _playersRaw;
        long _offset, _lastPose = long.MinValue;
        bool _takingOver, _acceptEarly;
        IReadOnlyDictionary<string, RoomPlayer> _players = new Dictionary<string, RoomPlayer>();

        public WebRoomSync(IHypaAuth auth, Func<long> localNow = null)
        {
            _auth = auth;
            _localNow = localNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WebBridge.Event += OnEvent;
            // A phone host writes lastSeen and then goes quiet. Nothing in the database changes when the 10 s pass, so look again
            // on a timer or the web guest never notices and never takes the room.
            _ = WatchPresenceAsync();
        }

        public string Code { get; private set; }
        public bool IsHost => Code != null && _info?.host != null && _info.host == _auth.Uid;
        public long ServerTimeMs => _localNow() + _offset;

        public event Action<RoomInfo> RoomChanged;
        public event Action<RoomClock> ClockChanged;
        public event Action<IReadOnlyDictionary<string, RoomPlayer>> PlayersChanged;
        public event Action<string, PoseFrame> PoseReceived;
        public event Action<RoomAnswer> AnswerReceived;
        public event Action<string, GameResult> GameResultReceived;
        public event Action Closed;

        public async Task<string> CreateAsync(string storyId, string language, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            await _auth.SignInAnonymouslyAsync();
            for (var attempt = 0; ; attempt++)
            {
                var code = RoomCode.New();
                ArmEarly();
                try
                {
                    var info = await WebBridge.CallAsync("roomCreate", new JObject { ["code"] = code, ["storyId"] = storyId, ["language"] = language, ["me"] = Me(me, 0) });
                    _me = Seat(me, 0);
                    Code = code;
                    _info = Info(info);
                    CommitEarly();
                    return code;
                }
                catch (WebBridgeException e) when (e.Code == "taken" && attempt < MaxCodeTries - 1) { DropEarly(); }
                catch { DropEarly(); throw; }
            }
        }

        public async Task<RoomInfo> JoinAsync(string code, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            var normalized = RoomCode.Normalize(code) ?? throw new RoomException(RoomError.NotFound, "No room " + code + ".");
            await _auth.SignInAnonymouslyAsync();
            JToken result;
            ArmEarly();
            try
            {
                result = await WebBridge.CallAsync("roomJoin", new JObject
                {
                    ["code"] = normalized, ["me"] = Me(me, 0), ["maxPlayers"] = RoomSeats.MaxPlayers, ["serverNow"] = ServerTimeMs,
                });
            }
            catch (WebBridgeException e) when (e.Code == "not_found") { DropEarly(); throw new RoomException(RoomError.NotFound, "No room " + normalized + "."); }
            catch (WebBridgeException e) when (e.Code == "full") { DropEarly(); throw new RoomException(RoomError.Full, "Room " + normalized + " is full."); }
            catch (WebBridgeException e) when (e.Code == "expired") { DropEarly(); throw new RoomException(RoomError.Expired, "Room " + normalized + " has expired."); }
            catch { DropEarly(); throw; }
            _me = Seat(me, (int?)result["seat"] ?? 1);
            Code = normalized;
            _info = Info(result["info"]);
            CommitEarly();
            return Copy(_info);
        }

        public async Task LeaveAsync()
        {
            if (Code == null) return;
            var close = IsHost && RoomHandover.NextHost(_players, _auth.Uid) == null; // nobody to take the room
            Code = null;
            try { await WebBridge.CallAsync("roomLeave", new JObject { ["closeRoom"] = close }); }
            catch (WebBridgeException) { /* logged in JS; the room forgets us when the connection goes */ }
            Closed?.Invoke();
        }

        public Task SetStateAsync(string state)
        {
            RequireHost();
            if (!RoomStates.Valid(state)) throw new ArgumentException("Unknown room state " + state + ".");
            return WebBridge.CallAsync("roomSetState", new JObject { ["state"] = state });
        }

        public Task SetClockAsync(RoomClock clock)
        {
            RequireHost();
            return WebBridge.CallAsync("roomSetClock", new JObject
            {
                ["clock"] = new JObject { ["lineIndex"] = clock.lineIndex, ["startedAtServerMs"] = clock.startedAtServerMs, ["paused"] = clock.paused },
            });
        }

        public Task UpdateMeAsync(RoomPlayer me)
        {
            RequireRoom();
            _me.name = me?.name ?? _me.name;
            _me.platform = me?.platform ?? _me.platform;
            _me.handUp = me != null && me.handUp;
            return WebBridge.CallAsync("roomUpdateMe", new JObject { ["me"] = Me(_me, _me.seat) });
        }

        public void SendPose(PoseFrame pose)
        {
            if (Code == null || pose == null) return;
            if (!PoseFrame.Valid(pose.h) || !PoseFrame.Valid(pose.l) || !PoseFrame.Valid(pose.r)) throw new ArgumentException("Pose parts are [x,y,z,qx,qy,qz,qw] or empty.");
            if (pose.h.Length == 0) return;
            var now = _localNow();
            if (_lastPose != long.MinValue && now - _lastPose < PoseFrame.IntervalMs) return; // at most 10 a second
            _lastPose = now;
            _ = WebBridge.CallAsync("roomPose", new JObject
            {
                ["pose"] = new JObject { ["h"] = new JArray(pose.h), ["l"] = new JArray(pose.l), ["r"] = new JArray(pose.r), ["t"] = ServerTimeMs },
            }).ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);
        }

        public Task SendAnswerAsync(RoomAnswer sentence)
        {
            RequireRoom();
            if (sentence == null || string.IsNullOrEmpty(sentence.text) || sentence.text.Length > RoomAnswer.MaxTextLength)
                throw new ArgumentException("An answer sentence is 1 to " + RoomAnswer.MaxTextLength + " characters.");
            if (!_me.handUp) throw new RoomException(RoomError.NotYourTurn, "Raise your hand before answering.");
            return WebBridge.CallAsync("roomAnswer", new JObject
            {
                ["sentence"] = new JObject { ["q"] = sentence.q, ["i"] = sentence.i, ["text"] = sentence.text, ["last"] = sentence.last },
            });
        }

        public Task SubmitGameAsync(GameResult result)
        {
            RequireRoom();
            return WebBridge.CallAsync("roomGame", new JObject
            {
                ["result"] = new JObject { ["startedAt"] = result.startedAt, ["finishedMs"] = Math.Max(0, result.finishedMs), ["score"] = result.score },
            });
        }

        void RequireRoom() { if (Code == null) throw new RoomException(RoomError.NotInRoom, "Not in a room."); }
        void RequireHost() { RequireRoom(); if (!IsHost) throw new RoomException(RoomError.NotHost, "Only the host can do that."); }

        /// <summary>Events from firebase-bridge.js. Public for tests.</summary>
        public void OnEvent(string name, JToken value)
        {
            if (name == "roomOffset") { _offset = value?.Type == JTokenType.Integer || value?.Type == JTokenType.Float ? (long)value : 0; return; }
            if (name == null || !name.StartsWith("room", StringComparison.Ordinal)) return;
            // The JS listeners fire the room's current children as soon as they attach, which is still inside roomJoin, before
            // this side has a code. Hold those until the join succeeds or a late guest never sees the clock, state or players.
            if (Code == null)
            {
                if (_acceptEarly) _early.Add((name, value?.DeepClone()));
                return;
            }
            Dispatch(name, value);
        }

        void ArmEarly() { _acceptEarly = true; _early.Clear(); }
        void DropEarly() { _acceptEarly = false; _early.Clear(); }

        void CommitEarly()
        {
            _acceptEarly = false;
            var queued = _early.ToArray();
            _early.Clear();
            foreach (var e in queued) if (Code != null) Dispatch(e.name, e.value);
        }

        async Task WatchPresenceAsync()
        {
            while (true)
            {
                await Task.Delay((int)RoomPresence.HeartbeatMs);
                if (Code != null && _playersRaw != null) ApplyPlayers(_playersRaw);
            }
        }

        void Dispatch(string name, JToken value)
        {
            switch (name)
            {
                case "roomHost":
                    var host = (string)value;
                    if (host == null || host == _info.host) return;
                    _info.host = host;
                    RoomChanged?.Invoke(Copy(_info));
                    CheckHandover();
                    break;
                case "roomState":
                    var state = (string)value;
                    if (state == null || state == _info.state) return;
                    _info.state = state;
                    RoomChanged?.Invoke(Copy(_info));
                    break;
                case "roomClock":
                    if (!(value is JObject c)) return;
                    ClockChanged?.Invoke(new RoomClock { lineIndex = (int?)c["lineIndex"] ?? 0, startedAtServerMs = (long?)c["startedAtServerMs"] ?? 0, paused = (bool?)c["paused"] ?? false });
                    break;
                case "roomPlayers":
                    ApplyPlayers(value as JObject);
                    break;
                case "roomPoses":
                    if (!(value is JObject poses)) return;
                    foreach (var pair in poses)
                    {
                        if (pair.Key == _auth.Uid || !(pair.Value is JObject p)) continue;
                        var t = (long?)p["t"] ?? 0;
                        if (_poseSeen.TryGetValue(pair.Key, out var seen) && t <= seen) continue;
                        _poseSeen[pair.Key] = t;
                        PoseReceived?.Invoke(pair.Key, new PoseFrame { h = Floats(p["h"]), l = Floats(p["l"]), r = Floats(p["r"]), t = t });
                    }
                    break;
                case "roomAnswer":
                    if (!(value is JObject a)) return;
                    AnswerReceived?.Invoke(new RoomAnswer { uid = (string)a["uid"], q = (int?)a["q"] ?? 0, i = (int?)a["i"] ?? 0, text = (string)a["text"], last = (bool?)a["last"] ?? false });
                    break;
                case "roomGame":
                    if (!(value is JObject games)) return;
                    foreach (var pair in games)
                        if (pair.Value is JObject g)
                            GameResultReceived?.Invoke(pair.Key, new GameResult { startedAt = (long?)g["startedAt"] ?? 0, finishedMs = (long?)g["finishedMs"] ?? 0, score = (int?)g["score"] ?? 0 });
                    break;
                case "roomClosed":
                    Code = null;
                    _playersRaw = null;
                    Closed?.Invoke();
                    break;
            }
        }

        /// <summary>
        /// Players from a snapshot. A phone or Quest host keeps <c>online</c> true and only refreshes <c>lastSeen</c>, so a stale
        /// heartbeat counts as gone (<see cref="RoomPresence.IsOnline"/>). Called again on a timer: the database does not change
        /// when the 10 s run out.
        /// </summary>
        void ApplyPlayers(JObject all)
        {
            _playersRaw = all;
            var players = new Dictionary<string, RoomPlayer>();
            if (all != null)
                foreach (var pair in all)
                    if (pair.Value is JObject p)
                        players[pair.Key] = new RoomPlayer
                        {
                            name = (string)p["name"], platform = (string)p["platform"], seat = (int?)p["seat"] ?? 0,
                            online = RoomPresence.IsOnline((bool?)p["online"] ?? false, (long?)p["lastSeen"], ServerTimeMs),
                            handUp = (bool?)p["handUp"] ?? false,
                        };
            if (Same(_players, players)) { CheckHandover(); return; }
            _players = players;
            PlayersChanged?.Invoke(players);
            CheckHandover();
        }

        static bool Same(IReadOnlyDictionary<string, RoomPlayer> a, IReadOnlyDictionary<string, RoomPlayer> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out var other)) return false;
                var p = pair.Value;
                if (p.online != other.online || p.handUp != other.handUp || p.seat != other.seat || p.name != other.name || p.platform != other.platform) return false;
            }
            return true;
        }

        void CheckHandover()
        {
            if (_takingOver || _info == null || !RoomHandover.HostGone(_players, _info.host) || RoomHandover.NextHost(_players, _info.host) != _auth.Uid) return;
            _ = TakeOverAsync();
        }

        async Task TakeOverAsync()
        {
            _takingOver = true;
            try { await WebBridge.CallAsync("roomTakeOver"); } // the host listener brings the change back
            catch (WebBridgeException) { }
            finally { _takingOver = false; }
        }

        static JObject Me(RoomPlayer me, int seat) => new JObject
        {
            ["name"] = me?.name ?? "", ["platform"] = me?.platform ?? Platforms.Web, ["seat"] = seat, ["handUp"] = me != null && me.handUp,
        };

        static RoomPlayer Seat(RoomPlayer me, int seat) => new RoomPlayer { name = me?.name ?? "", platform = me?.platform ?? Platforms.Web, seat = seat, online = true, handUp = me != null && me.handUp };

        static RoomInfo Info(JToken t) => new RoomInfo
        {
            host = (string)t?["host"], storyId = (string)t?["storyId"], language = (string)t?["language"], state = (string)t?["state"] ?? RoomStates.Lobby,
            createdAt = (long?)t?["createdAt"] ?? 0,
        };

        static float[] Floats(JToken t) => t is JArray a && a.Count == PoseFrame.Length ? a.ToObject<float[]>() : new float[0];
        static RoomInfo Copy(RoomInfo i) => new RoomInfo { host = i.host, storyId = i.storyId, language = i.language, state = i.state, createdAt = i.createdAt };
    }
}
