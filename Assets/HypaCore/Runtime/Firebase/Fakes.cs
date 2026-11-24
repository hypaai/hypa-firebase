using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    // In-memory stand-ins (docs/contracts.md, section 1). The editor and the tests run on these, so a feature works
    // without Firebase. They never touch the network and never hold a real token.

    public sealed class FakeAuth : IHypaAuth
    {
        public string Uid { get; private set; }
        public event Action<string> UidChanged;

        public Task<string> SignInAnonymouslyAsync()
        {
            if (Uid == null) { Uid = "fake-uid"; UidChanged?.Invoke(Uid); }
            return Task.FromResult(Uid);
        }

        public Task LinkGoogleAsync() => Link();
        public Task LinkAppleAsync() => Link();
        public Task<string> IdTokenAsync() => Uid == null ? throw new InvalidOperationException("Not signed in.") : Task.FromResult("fake-id-token");
        public Task<string> AppCheckTokenAsync() => Task.FromResult("fake-app-check-token");

        Task Link() => Uid == null ? throw new InvalidOperationException("Not signed in.") : Task.CompletedTask;
    }

    public sealed class FakeProgressStore : IProgressStore
    {
        Progress _saved;
        public event Action<Progress> ChangedElsewhere;

        public Task<Progress> LoadAsync() => Task.FromResult(_saved);
        public Task SaveAsync(Progress p) { _saved = p; return Task.CompletedTask; }
        /// <summary>For tests: pretend another device saved this progress.</summary>
        public void SimulateChangeElsewhere(Progress p) { _saved = p; ChangedElsewhere?.Invoke(p); }
    }

    public sealed class FakeRemoteFlags : IRemoteFlags
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public FakeRemoteFlags Set(string key, string value) { _values[key] = value; return this; }
        public Task FetchAsync() => Task.CompletedTask;
        public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : (RemoteConfigKeys.Default(key) ?? fallback);
    }

    public sealed class FakeProfileStore : IProfileStore
    {
        UserProfile _saved;
        public event Action<UserProfile> ChangedElsewhere;

        public Task<UserProfile> LoadAsync() => Task.FromResult(_saved);
        public Task SaveAsync(UserProfile profile) { _saved = profile; return Task.CompletedTask; }
        /// <summary>For tests: pretend another device saved this profile.</summary>
        public void SimulateChangeElsewhere(UserProfile profile) { _saved = profile; ChangedElsewhere?.Invoke(profile); }
    }

    /// <summary>
    /// The rooms a set of <see cref="FakeRoomSync"/> clients share, standing in for the Realtime Database: two fakes on the same
    /// server see each other like two devices. <see cref="Now"/> is the server clock (settable in tests).
    /// </summary>
    public sealed class FakeRoomServer
    {
        public static readonly FakeRoomServer Shared = new FakeRoomServer();
        public Func<long> Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal sealed class Room
        {
            public RoomInfo Info;
            public RoomClock Clock;
            public readonly Dictionary<string, RoomPlayer> Players = new Dictionary<string, RoomPlayer>();
            public readonly List<FakeRoomSync> Clients = new List<FakeRoomSync>();
        }

        internal readonly Dictionary<string, Room> Rooms = new Dictionary<string, Room>();

        /// <summary>For tests: the player's connection drops, as <c>onDisconnect</c> would report it.</summary>
        public void Disconnect(string code, string uid)
        {
            if (!Rooms.TryGetValue(code, out var room) || !room.Players.TryGetValue(uid, out var p)) return;
            p.online = false;
            foreach (var c in room.Clients.ToArray()) c.RaisePlayers(room);
        }

        /// <summary>For tests: what the scheduled cleanup does to rooms older than <see cref="RoomCode.LifetimeMs"/>.</summary>
        public int RemoveExpired()
        {
            var expired = new List<string>();
            foreach (var pair in Rooms) if (RoomCode.IsExpired(pair.Value.Info.createdAt, Now())) expired.Add(pair.Key);
            foreach (var code in expired) Close(code);
            return expired.Count;
        }

        internal void Close(string code)
        {
            if (!Rooms.TryGetValue(code, out var room)) return;
            Rooms.Remove(code);
            foreach (var c in room.Clients.ToArray()) c.RaiseClosed();
        }
    }

    /// <summary>In-memory <see cref="IRoomSync"/>. Events are raised synchronously, during the call that caused them.</summary>
    public sealed class FakeRoomSync : IRoomSync
    {
        readonly FakeRoomServer _server;
        readonly string _uid;
        long _lastPose = long.MinValue;

        public FakeRoomSync(FakeRoomServer server = null, string uid = "fake-uid")
        {
            _server = server ?? FakeRoomServer.Shared;
            _uid = uid;
        }

        public string Code { get; private set; }
        public bool IsHost => Code != null && _server.Rooms.TryGetValue(Code, out var room) && room.Info.host == _uid;
        public long ServerTimeMs => _server.Now();

        public event Action<RoomInfo> RoomChanged;
        public event Action<RoomClock> ClockChanged;
        public event Action<IReadOnlyDictionary<string, RoomPlayer>> PlayersChanged;
        public event Action<string, PoseFrame> PoseReceived;
        public event Action<string, GameResult> GameResultReceived;
        public event Action Closed;

        public Task<string> CreateAsync(string storyId, string language, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            string code;
            do code = RoomCode.New(); while (_server.Rooms.ContainsKey(code));
            var room = new FakeRoomServer.Room
            {
                Info = new RoomInfo { host = _uid, storyId = storyId, language = language, state = RoomStates.Lobby, createdAt = _server.Now() },
            };
            room.Players[_uid] = Seat(me, 0);
            room.Clients.Add(this);
            _server.Rooms[code] = room;
            Code = code;
            RoomChanged?.Invoke(Copy(room.Info));
            RaisePlayers(room);
            return Task.FromResult(code);
        }

        public Task<RoomInfo> JoinAsync(string code, RoomPlayer me)
        {
            if (Code != null) throw new InvalidOperationException("Already in room " + Code + ".");
            var normalized = RoomCode.Normalize(code);
            if (normalized == null || !_server.Rooms.TryGetValue(normalized, out var room))
                throw new RoomException(RoomError.NotFound, "No room " + code + ".");
            if (RoomCode.IsExpired(room.Info.createdAt, _server.Now())) throw new RoomException(RoomError.Expired, "Room " + normalized + " has expired.");
            int seat;
            if (room.Players.TryGetValue(_uid, out var existing)) seat = existing.seat;
            else
            {
                seat = -1;
                for (int s = 0; s < 2 && seat < 0; s++) { bool taken = false; foreach (var p in room.Players.Values) taken |= p.seat == s; if (!taken) seat = s; }
                if (seat < 0) throw new RoomException(RoomError.Full, "Room " + normalized + " is full.");
            }
            room.Players[_uid] = Seat(me, seat);
            room.Clients.Add(this);
            Code = normalized;
            RoomChanged?.Invoke(Copy(room.Info));
            if (room.Clock != null) ClockChanged?.Invoke(Copy(room.Clock));
            foreach (var c in room.Clients.ToArray()) c.RaisePlayers(room);
            return Task.FromResult(Copy(room.Info));
        }

        public Task LeaveAsync()
        {
            if (Code == null || !_server.Rooms.TryGetValue(Code, out var room)) { Code = null; return Task.CompletedTask; }
            if (room.Info.host == _uid) { _server.Close(Code); return Task.CompletedTask; }
            room.Players.Remove(_uid);
            room.Clients.Remove(this);
            RaiseClosed();
            foreach (var c in room.Clients.ToArray()) c.RaisePlayers(room);
            return Task.CompletedTask;
        }

        public Task SetStateAsync(string state)
        {
            var room = Host();
            if (!RoomStates.Valid(state)) throw new ArgumentException("Unknown room state " + state + ".");
            room.Info.state = state;
            foreach (var c in room.Clients.ToArray()) c.RoomChanged?.Invoke(Copy(room.Info));
            return Task.CompletedTask;
        }

        public Task SetClockAsync(RoomClock clock)
        {
            var room = Host();
            room.Clock = Copy(clock);
            foreach (var c in room.Clients.ToArray()) c.ClockChanged?.Invoke(Copy(room.Clock));
            return Task.CompletedTask;
        }

        public Task UpdateMeAsync(RoomPlayer me)
        {
            var room = Mine();
            room.Players[_uid] = Seat(me, room.Players[_uid].seat);
            foreach (var c in room.Clients.ToArray()) c.RaisePlayers(room);
            return Task.CompletedTask;
        }

        public void SendPose(PoseFrame pose)
        {
            if (Code == null || !_server.Rooms.TryGetValue(Code, out var room) || pose == null) return;
            if (!PoseFrame.Valid(pose.h) || !PoseFrame.Valid(pose.l) || !PoseFrame.Valid(pose.r)) throw new ArgumentException("Pose parts are [x,y,z,qx,qy,qz,qw] or empty.");
            var now = _server.Now();
            if (_lastPose != long.MinValue && now - _lastPose < PoseFrame.IntervalMs) return; // at most 10 a second
            _lastPose = now;
            foreach (var c in room.Clients.ToArray()) if (c != this) c.PoseReceived?.Invoke(_uid, pose);
        }

        public Task SubmitGameAsync(GameResult result)
        {
            var room = Mine();
            foreach (var c in room.Clients.ToArray()) c.GameResultReceived?.Invoke(_uid, result);
            return Task.CompletedTask;
        }

        internal void RaisePlayers(FakeRoomServer.Room room)
        {
            var copy = new Dictionary<string, RoomPlayer>();
            foreach (var pair in room.Players) copy[pair.Key] = Copy(pair.Value);
            PlayersChanged?.Invoke(copy);
        }

        internal void RaiseClosed() { Code = null; Closed?.Invoke(); }

        FakeRoomServer.Room Mine()
        {
            if (Code == null || !_server.Rooms.TryGetValue(Code, out var room)) throw new RoomException(RoomError.NotInRoom, "Not in a room.");
            return room;
        }

        FakeRoomServer.Room Host()
        {
            var room = Mine();
            if (room.Info.host != _uid) throw new RoomException(RoomError.NotHost, "Only the host can do that.");
            return room;
        }

        static RoomPlayer Seat(RoomPlayer me, int seat) => new RoomPlayer { name = me?.name, platform = me?.platform, seat = seat, online = true, handUp = me != null && me.handUp };
        static RoomPlayer Copy(RoomPlayer p) => new RoomPlayer { name = p.name, platform = p.platform, seat = p.seat, online = p.online, handUp = p.handUp };
        static RoomInfo Copy(RoomInfo i) => new RoomInfo { host = i.host, storyId = i.storyId, language = i.language, state = i.state, createdAt = i.createdAt };
        static RoomClock Copy(RoomClock c) => new RoomClock { lineIndex = c.lineIndex, startedAtServerMs = c.startedAtServerMs, paused = c.paused };
    }

    public sealed class FakeOnDeviceModel : IOnDeviceModel
    {
        public bool Available { get; set; }
        public string Reply = "This is a fake on-device answer.";

        public Task<string> StatusAsync() => Task.FromResult(Available ? "available" : "unavailable");
        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
            => Available ? Task.FromResult(Reply) : throw new InvalidOperationException("No on-device model.");
        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => Words(Reply, Available, ct);

        internal static async IAsyncEnumerable<string> Words(string reply, bool available, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!available) throw new InvalidOperationException("No on-device model.");
            foreach (var word in reply.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return word + " ";
                // Never suspends. A real Yield would resume on Unity's main thread, which deadlocks a test that waits on it.
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }

    public sealed class FakeCloudModel : ICloudModel
    {
        public string Reply = "This is a fake cloud answer.";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct) => Task.FromResult(Reply);
        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => FakeOnDeviceModel.Words(Reply, true, ct);
    }
}
