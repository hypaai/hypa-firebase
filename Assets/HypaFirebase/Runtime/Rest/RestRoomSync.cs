using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// <see cref="IRoomSync"/> over the Realtime Database REST API and server-sent events (G09): Quest, Android and iOS
    /// (docs/contracts.md, section 4). REST has no <c>onDisconnect</c> and no <c>.info/serverTimeOffset</c>, so this client
    /// refreshes <c>players/{uid}/lastSeen</c> every <see cref="RoomPresence.HeartbeatMs"/>, reports a player silent for longer than
    /// <see cref="RoomPresence.TimeoutMs"/> as offline, measures the server clock from that same heartbeat (<see cref="ServerClock"/>),
    /// and takes the room when the host is gone and it is next (<see cref="RoomHandover"/>).
    /// The room node itself is not readable, so <c>host</c>, <c>state</c>, <c>clock</c>, <c>players</c>, <c>poses</c>, <c>answers</c>
    /// and <c>game</c> are each streamed. Network work runs on worker threads; state changes and events happen on the thread that
    /// built this object (Unity's main thread).
    /// </summary>
    public sealed class RestRoomSync : IRoomSync
    {
        const int MaxCodeTries = 5;
        const int MaxAnswerTries = 3;
        const int ReconnectMs = 1000;

        readonly RestRealtimeDatabase _db;
        readonly IHypaAuth _auth;
        readonly SynchronizationContext _main;
        readonly Func<long> _localNow;
        readonly ServerClock _clockEstimate = new ServerClock();

        CancellationTokenSource _cts;
        RoomPlayer _me;
        int _seat;
        readonly RoomInfo _info = new RoomInfo();
        readonly Dictionary<string, RtdbTree> _trees = new Dictionary<string, RtdbTree>();
        readonly Dictionary<string, long> _poseSeen = new Dictionary<string, long>();
        IReadOnlyDictionary<string, RoomPlayer> _players = new Dictionary<string, RoomPlayer>();
        bool _answersPrimed;
        int _nextAnswer, _answerSeen = -1;
        long _lastPoseLocal = long.MinValue;
        bool _poseInFlight, _takingOver;
        string _lastPlayersKey;

        public RestRoomSync(RestRealtimeDatabase db, IHypaAuth auth, Func<long> localNow = null)
        {
            _db = db;
            _auth = auth;
            _main = SynchronizationContext.Current;
            _localNow = localNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // Seven event streams stay open while in a room; the old .NET default of 2 connections per host would starve the writes.
            if (ServicePointManager.DefaultConnectionLimit < 24) ServicePointManager.DefaultConnectionLimit = 24;
        }

        public string Code { get; private set; }
        public bool IsHost => Code != null && _info.host != null && _info.host == _auth.Uid;
        public long ServerTimeMs => _clockEstimate.ServerNow(_localNow());

        public event Action<RoomInfo> RoomChanged;
        public event Action<RoomClock> ClockChanged;
        public event Action<IReadOnlyDictionary<string, RoomPlayer>> PlayersChanged;
        public event Action<string, PoseFrame> PoseReceived;
        public event Action<RoomAnswer> AnswerReceived;
        public event Action<string, GameResult> GameResultReceived;
        public event Action Closed;

        string Room(string child = null) => child == null ? "rooms/" + Code : "rooms/" + Code + "/" + child;
        static CancellationToken None => CancellationToken.None;

        // ---------- create, join, leave ----------

        public async Task<string> CreateAsync(string storyId, string language, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            var uid = _auth.Uid ?? await _auth.SignInAnonymouslyAsync();
            var room = new JObject { ["host"] = uid, ["storyId"] = storyId ?? "", ["language"] = language ?? "", ["state"] = RoomStates.Lobby, ["createdAt"] = ServerTimestamp() };
            for (var attempt = 0; ; attempt++)
            {
                var code = RoomCode.New();
                try
                {
                    // the rules let a room be created only where there is none, so a taken code is refused
                    await _db.SetAsync("rooms/" + code, room.ToString(Newtonsoft.Json.Formatting.None), None);
                    Code = code;
                    break;
                }
                catch (HypaFirebaseException) when (attempt < MaxCodeTries - 1) { }
            }
            _info.host = uid; _info.storyId = storyId; _info.language = language; _info.state = RoomStates.Lobby;
            await SeatAsync(me, 0);
            Start();
            return Code;
        }

        public async Task<RoomInfo> JoinAsync(string code, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            var normalized = RoomCode.Normalize(code) ?? throw new RoomException(RoomError.NotFound, "No room " + code + ".");
            var uid = _auth.Uid ?? await _auth.SignInAnonymouslyAsync();
            var basePath = "rooms/" + normalized;
            var host = ReadString(await _db.GetAsync(basePath + "/host", None));
            if (host == null) throw new RoomException(RoomError.NotFound, "No room " + normalized + ".");
            var players = ParsePlayers(JToken.Parse(await _db.GetAsync(basePath + "/players", None)), long.MaxValue);
            int seat;
            if (players.TryGetValue(uid, out var existing)) seat = existing.seat;
            else
            {
                seat = -1;
                for (var s = 0; s < RoomSeats.MaxPlayers && seat < 0; s++) if (players.Values.All(p => p.seat != s)) seat = s;
                if (seat < 0) throw new RoomException(RoomError.Full, "Room " + normalized + " is full.");
            }
            Code = normalized;
            try
            {
                await SeatAsync(me, seat);
                // two players may have taken the same seat at once: the lower uid keeps it
                var after = ParsePlayers(JToken.Parse(await _db.GetAsync(Room("players"), None)), long.MaxValue);
                if (after.Any(p => p.Key != uid && p.Value.seat == seat && string.CompareOrdinal(p.Key, uid) < 0))
                    throw new RoomException(RoomError.Full, "Room " + normalized + " is full.");
                _info.host = host;
                _info.storyId = ReadString(await _db.GetAsync(Room("storyId"), None));
                _info.language = ReadString(await _db.GetAsync(Room("language"), None));
                _info.state = ReadString(await _db.GetAsync(Room("state"), None));
                _info.createdAt = ReadLong(await _db.GetAsync(Room("createdAt"), None));
                if (RoomCode.IsExpired(_info.createdAt, ServerTimeMs)) throw new RoomException(RoomError.Expired, "Room " + normalized + " has expired.");
            }
            catch
            {
                try { await _db.DeleteAsync(Room("players/" + uid), None); } catch { /* best effort */ }
                Code = null;
                throw;
            }
            Start();
            return Copy(_info);
        }

        public async Task LeaveAsync()
        {
            if (Code == null) return;
            var uid = _auth.Uid;
            var wasHost = IsHost;
            var nobodyElse = RoomHandover.NextHost(_players, uid) == null;
            var path = Room();
            Stop();
            try
            {
                // the host alone with nobody to take over closes the room; otherwise leaving frees the seat and, for the host, lets
                // the next player take the room (an entry that is gone means the player is gone)
                if (wasHost && nobodyElse) await _db.DeleteAsync(path, None);
                else await _db.DeleteAsync(path + "/players/" + uid, None);
            }
            catch (Exception e) { Debug.LogWarning("Hypa rooms: leaving was not recorded (" + e.GetType().Name + "); the room forgets you after 10 s."); }
            Code = null;
            Closed?.Invoke();
        }

        async Task SeatAsync(RoomPlayer me, int seat)
        {
            _seat = seat;
            _me = new RoomPlayer { name = me?.name ?? "", platform = me?.platform ?? Platforms.Editor, seat = seat, online = true, handUp = me != null && me.handUp };
            await WriteMeAsync();
            await HeartbeatAsync(); // the first measurement of the server clock, before anyone's presence is judged
        }

        Task WriteMeAsync()
        {
            var json = new JObject
            {
                ["name"] = Trim(_me.name, 24), ["platform"] = _me.platform, ["seat"] = _seat, ["online"] = true, ["handUp"] = _me.handUp,
                ["lastSeen"] = ServerTimestamp(),
            };
            return _db.SetAsync(Room("players/" + _auth.Uid), json.ToString(Newtonsoft.Json.Formatting.None), None);
        }

        // ---------- host-only and own writes ----------

        public Task SetStateAsync(string state)
        {
            RequireHost();
            if (!RoomStates.Valid(state)) throw new ArgumentException("Unknown room state " + state + ".");
            _info.state = state;
            return _db.SetAsync(Room("state"), new JValue(state).ToString(Newtonsoft.Json.Formatting.None), None);
        }

        public Task SetClockAsync(RoomClock clock)
        {
            RequireHost();
            var json = new JObject { ["lineIndex"] = Math.Max(0, clock.lineIndex), ["startedAtServerMs"] = clock.startedAtServerMs, ["paused"] = clock.paused };
            return _db.SetAsync(Room("clock"), json.ToString(Newtonsoft.Json.Formatting.None), None);
        }

        public Task UpdateMeAsync(RoomPlayer me)
        {
            RequireRoom();
            _me.name = me?.name ?? _me.name;
            _me.platform = me?.platform ?? _me.platform;
            _me.handUp = me != null && me.handUp;
            return WriteMeAsync();
        }

        public void SendPose(PoseFrame pose)
        {
            if (Code == null || pose == null) return;
            if (!PoseFrame.Valid(pose.h) || !PoseFrame.Valid(pose.l) || !PoseFrame.Valid(pose.r)) throw new ArgumentException("Pose parts are [x,y,z,qx,qy,qz,qw] or empty.");
            if (pose.h.Length == 0) return; // the rules need a head
            var now = _localNow();
            if (_poseInFlight || (_lastPoseLocal != long.MinValue && now - _lastPoseLocal < PoseFrame.IntervalMs)) return; // at most 10 a second, never queued
            _lastPoseLocal = now;
            var json = new JObject { ["h"] = new JArray(pose.h), ["t"] = ServerTimeMs };
            if (pose.l.Length > 0) json["l"] = new JArray(pose.l);
            if (pose.r.Length > 0) json["r"] = new JArray(pose.r);
            _poseInFlight = true;
            _ = SendPoseAsync(Room("poses/" + _auth.Uid), json.ToString(Newtonsoft.Json.Formatting.None));
        }

        async Task SendPoseAsync(string path, string json)
        {
            try { await _db.SetAsync(path, json, None); }
            catch { /* fire and forget: the next pose replaces it */ }
            finally { _poseInFlight = false; }
        }

        public async Task SendAnswerAsync(RoomAnswer sentence)
        {
            RequireRoom();
            if (sentence == null || string.IsNullOrEmpty(sentence.text) || sentence.text.Length > RoomAnswer.MaxTextLength)
                throw new ArgumentException("An answer sentence is 1 to " + RoomAnswer.MaxTextLength + " characters.");
            if (!_me.handUp) throw new RoomException(RoomError.NotYourTurn, "Raise your hand before answering.");
            var json = new JObject { ["uid"] = _auth.Uid, ["q"] = Math.Max(0, sentence.q), ["i"] = Math.Max(0, sentence.i), ["text"] = sentence.text, ["last"] = sentence.last }
                .ToString(Newtonsoft.Json.Formatting.None);
            for (var attempt = 0; ; attempt++)
            {
                var n = _nextAnswer++;
                try { await _db.SetAsync(Room("answers/" + n), json, None); return; }
                catch (HypaFirebaseException) when (attempt < MaxAnswerTries - 1) { } // that n was taken: try the next one
            }
        }

        public Task SubmitGameAsync(GameResult result)
        {
            RequireRoom();
            var json = new JObject { ["startedAt"] = result.startedAt, ["finishedMs"] = Math.Max(0, result.finishedMs), ["score"] = result.score };
            return _db.SetAsync(Room("game/" + _auth.Uid), json.ToString(Newtonsoft.Json.Formatting.None), None);
        }

        void RequireRoom() { if (Code == null) throw new RoomException(RoomError.NotInRoom, "Not in a room."); }
        void RequireHost() { RequireRoom(); if (!IsHost) throw new RoomException(RoomError.NotHost, "Only the host can do that."); }

        // ---------- streams, heartbeat ----------

        void Start()
        {
            _cts = new CancellationTokenSource();
            _trees.Clear(); _poseSeen.Clear();
            _answersPrimed = false; _nextAnswer = 0; _lastPlayersKey = null;
            var ct = _cts.Token;
            foreach (var child in new[] { "host", "state", "clock", "players", "poses", "answers", "game" })
            {
                _trees[child] = new RtdbTree();
                _ = StreamAsync(child, ct);
            }
            _ = HeartbeatLoopAsync(ct);
        }

        void Stop()
        {
            _cts?.Cancel();
            _cts = null;
        }

        async Task StreamAsync(string child, CancellationToken ct)
        {
            var path = Room(child);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var evt in _db.StreamAsync(path, ct).ConfigureAwait(false))
                    {
                        if (evt.Name == "auth_revoked") break; // the ID token expired: reconnect with a fresh one
                        if (evt.Name == "cancel") { Post(() => OnCancelled(child)); return; }
                        if (evt.Name != "put" && evt.Name != "patch") continue;
                        Post(() => Apply(child, evt));
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Debug.LogWarning("Hypa rooms: the " + child + " stream dropped (" + e.GetType().Name + "); reconnecting."); }
                try { await Task.Delay(ReconnectMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }

        async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay((int)RoomPresence.HeartbeatMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                try { await HeartbeatAsync().ConfigureAwait(false); }
                catch (Exception e) { Debug.LogWarning("Hypa rooms: heartbeat failed (" + e.GetType().Name + ")."); }
                Post(() => { if (Code != null) RaisePlayersIfChanged(); });
            }
        }

        async Task HeartbeatAsync()
        {
            var uid = _auth.Uid;
            var path = Room("players/" + uid + "/lastSeen");
            var t0 = _localNow();
            var stored = await _db.SetAsync(path, ServerTimestamp().ToString(Newtonsoft.Json.Formatting.None), None).ConfigureAwait(false);
            var t1 = _localNow();
            var server = ReadLong(stored);
            if (server <= 0) { server = ReadLong(await _db.GetAsync(path, None).ConfigureAwait(false)); t1 = _localNow(); }
            if (server > 0) _clockEstimate.Add(t0, t1, server);
        }

        void OnCancelled(string child)
        {
            // the server stopped a stream: we are no longer allowed to read the room (it was deleted, or we were removed)
            if (Code == null) return;
            Debug.Log("Hypa rooms: the room stopped answering (" + child + "); leaving it.");
            Stop();
            Code = null;
            Closed?.Invoke();
        }

        void Post(Action action)
        {
            if (_main == null || SynchronizationContext.Current == _main) action();
            else _main.Post(_ => action(), null);
        }

        void Apply(string child, RtdbEvent evt)
        {
            if (Code == null || !_trees.TryGetValue(child, out var tree)) return;
            JToken changedRoot;
            try { changedRoot = tree.Apply(evt.Name, evt.Data); }
            catch (Exception e) { Debug.LogWarning("Hypa rooms: unreadable " + child + " update (" + e.GetType().Name + ")."); return; }
            switch (child)
            {
                case "host":
                    var host = tree.Root?.Type == JTokenType.String ? (string)tree.Root : null;
                    if (host == null) { OnCancelled(child); return; } // the room was deleted
                    if (host == _info.host) return;
                    _info.host = host;
                    RoomChanged?.Invoke(Copy(_info));
                    RaisePlayersIfChanged(force: true);
                    break;
                case "state":
                    var state = tree.Root?.Type == JTokenType.String ? (string)tree.Root : null;
                    if (state == null || state == _info.state) return;
                    _info.state = state;
                    RoomChanged?.Invoke(Copy(_info));
                    break;
                case "clock":
                    if (!(tree.Root is JObject c)) return;
                    ClockChanged?.Invoke(new RoomClock { lineIndex = (int?)c["lineIndex"] ?? 0, startedAtServerMs = (long?)c["startedAtServerMs"] ?? 0, paused = (bool?)c["paused"] ?? false });
                    break;
                case "players":
                    RaisePlayersIfChanged();
                    break;
                case "poses":
                    if (!(tree.Root is JObject poses)) return;
                    foreach (var pair in poses)
                    {
                        if (pair.Key == _auth.Uid || !(pair.Value is JObject p)) continue;
                        var t = (long?)p["t"] ?? 0;
                        if (_poseSeen.TryGetValue(pair.Key, out var seen) && t <= seen) continue;
                        _poseSeen[pair.Key] = t;
                        PoseReceived?.Invoke(pair.Key, new PoseFrame { h = Floats(p["h"]), l = Floats(p["l"]), r = Floats(p["r"]), t = t });
                    }
                    break;
                case "answers":
                    var answers = tree.Root as JObject;
                    var keys = answers == null ? new List<int>() : answers.Properties().Select(x => int.TryParse(x.Name, out var n) ? n : -1).Where(n => n >= 0).OrderBy(n => n).ToList();
                    if (!_answersPrimed)
                    {
                        // the first snapshot is what was said before we came: count past it, never replay it
                        _answersPrimed = true;
                        _nextAnswer = keys.Count == 0 ? 0 : keys[keys.Count - 1] + 1;
                        _answerSeen = _nextAnswer - 1;
                        return;
                    }
                    foreach (var n in keys)
                    {
                        if (n <= _answerSeen) continue;
                        _answerSeen = n;
                        _nextAnswer = Math.Max(_nextAnswer, n + 1);
                        if (!(answers[n.ToString()] is JObject a) || (string)a["uid"] == _auth.Uid) continue;
                        AnswerReceived?.Invoke(new RoomAnswer { uid = (string)a["uid"], q = (int?)a["q"] ?? 0, i = (int?)a["i"] ?? 0, text = (string)a["text"], last = (bool?)a["last"] ?? false });
                    }
                    break;
                case "game":
                    if (!(changedRoot is JObject games)) return;
                    foreach (var pair in games)
                        if (pair.Value is JObject g)
                            GameResultReceived?.Invoke(pair.Key, new GameResult { startedAt = (long?)g["startedAt"] ?? 0, finishedMs = (long?)g["finishedMs"] ?? 0, score = (int?)g["score"] ?? 0 });
                    break;
            }
        }

        /// <summary>Raises <see cref="PlayersChanged"/> when someone's entry or presence changed, and takes the room if the host is gone and this device is next.</summary>
        void RaisePlayersIfChanged(bool force = false)
        {
            if (!_trees.TryGetValue("players", out var tree)) return;
            var players = ParsePlayers(tree.Root, ServerTimeMs);
            var key = string.Join("|", players.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value.seat}:{p.Value.online}:{p.Value.handUp}:{p.Value.name}"));
            _players = players;
            if (force || key != _lastPlayersKey)
            {
                _lastPlayersKey = key;
                PlayersChanged?.Invoke(players);
            }
            if (!_takingOver && RoomHandover.HostGone(players, _info.host) && RoomHandover.NextHost(players, _info.host) == _auth.Uid
                && players.TryGetValue(_auth.Uid, out var mine) && mine.online)
                _ = TakeOverAsync();
        }

        async Task TakeOverAsync()
        {
            _takingOver = true;
            try
            {
                await _db.SetAsync(Room("host"), new JValue(_auth.Uid).ToString(Newtonsoft.Json.Formatting.None), None);
                // the host stream brings the change to everyone, this device included
            }
            catch (HypaFirebaseException) { /* someone else was first, or the host came back */ }
            catch (Exception e) { Debug.LogWarning("Hypa rooms: couldn't take over the room (" + e.GetType().Name + ")."); }
            finally { _takingOver = false; }
        }

        // ---------- parsing ----------

        /// <summary>
        /// Players from a <c>players</c> snapshot. A player whose <c>lastSeen</c> is older than <see cref="RoomPresence.TimeoutMs"/> at
        /// <paramref name="serverNowMs"/> is reported offline; <c>long.MaxValue</c> skips that check.
        /// </summary>
        public static Dictionary<string, RoomPlayer> ParsePlayers(JToken root, long serverNowMs)
        {
            var result = new Dictionary<string, RoomPlayer>();
            if (!(root is JObject all)) return result;
            foreach (var pair in all)
            {
                if (!(pair.Value is JObject p)) continue;
                var online = RoomPresence.IsOnline((bool?)p["online"] ?? false, (long?)p["lastSeen"], serverNowMs);
                result[pair.Key] = new RoomPlayer
                {
                    name = (string)p["name"], platform = (string)p["platform"], seat = (int?)p["seat"] ?? 0, online = online, handUp = (bool?)p["handUp"] ?? false,
                };
            }
            return result;
        }

        static float[] Floats(JToken t) => t is JArray a && a.Count == PoseFrame.Length ? a.Select(v => (float)v).ToArray() : new float[0];
        static JObject ServerTimestamp() => new JObject { [".sv"] = "timestamp" };
        static string ReadString(string json) { var t = JToken.Parse(json); return t.Type == JTokenType.String ? (string)t : null; }
        static long ReadLong(string json) { try { var t = JToken.Parse(json); return t.Type == JTokenType.Integer || t.Type == JTokenType.Float ? (long)t : 0; } catch { return 0; } }
        static string Trim(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max);
        static RoomInfo Copy(RoomInfo i) => new RoomInfo { host = i.host, storyId = i.storyId, language = i.language, state = i.state, createdAt = i.createdAt };
    }

    /// <summary>
    /// The server's clock as seen from this device, for REST (which has no <c>.info/serverTimeOffset</c>): from a write of a server
    /// timestamp, local time before (<c>t0</c>) and after (<c>t1</c>) and the stored value <c>s</c>, the offset is <c>s - (t0 + t1) / 2</c>.
    /// The sample with the shortest round trip among the last few wins (its error is at most half that round trip).
    /// </summary>
    public sealed class ServerClock
    {
        const int Keep = 5;
        readonly List<(long rtt, long offset)> _samples = new List<(long, long)>();
        readonly object _lock = new object();

        public long Offset { get { lock (_lock) return _samples.Count == 0 ? 0 : _samples.OrderBy(s => s.rtt).First().offset; } }
        public long ServerNow(long localNowMs) => localNowMs + Offset;

        public void Add(long t0, long t1, long serverMs)
        {
            if (t1 < t0) return;
            lock (_lock)
            {
                _samples.Add((t1 - t0, serverMs - (t0 + t1) / 2));
                if (_samples.Count > Keep) _samples.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// A Realtime Database event stream folded into one JSON tree: <c>put</c> replaces the value at a path, <c>patch</c> merges
    /// children there. Arrays (how the database sends keys 0, 1, 2…) become objects keyed by index, so later changes by key apply.
    /// </summary>
    public sealed class RtdbTree
    {
        public JToken Root { get; private set; }

        /// <summary>Applies one event's <c>data</c> (<c>{"path": "/a/b", "data": ...}</c>). Returns the value that changed.</summary>
        public JToken Apply(string name, string data)
        {
            var o = JObject.Parse(data);
            var path = (string)o["path"] ?? "/";
            var value = o["data"];
            if (name == "patch" && value is JObject patch)
            {
                foreach (var p in patch) Set(path.TrimEnd('/') + "/" + p.Key, p.Value);
                return patch;
            }
            Set(path, value);
            return value;
        }

        void Set(string path, JToken value)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var gone = value == null || value.Type == JTokenType.Null;
            if (parts.Length == 0) { Root = gone ? null : Normalize(value.DeepClone()); return; }
            if (!(Root is JObject)) Root = Root is JArray arr ? ToObject(arr) : new JObject();
            var node = (JObject)Root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = node[parts[i]];
                if (next is JArray a) { next = ToObject(a); node[parts[i]] = next; }
                if (!(next is JObject child)) { child = new JObject(); node[parts[i]] = child; next = child; }
                node = (JObject)next;
            }
            var last = parts[parts.Length - 1];
            if (gone) node.Remove(last);
            else node[last] = value.DeepClone();
        }

        /// <summary>A whole-tree array becomes an object; the arrays inside a value (a pose's numbers) stay arrays.</summary>
        static JToken Normalize(JToken t) => t is JArray a ? ToObject(a) : t;

        static JObject ToObject(JArray a)
        {
            var o = new JObject();
            for (var i = 0; i < a.Count; i++) if (a[i] != null && a[i].Type != JTokenType.Null) o[i.ToString()] = a[i];
            return o;
        }
    }
}
