using System.Collections.Generic;
using System.Linq;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaCore.Tests
{
    public class HybridRoomsTests
    {
        static T Run<T>(System.Threading.Tasks.Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(System.Threading.Tasks.Task task) => task.GetAwaiter().GetResult();

        FakeRoomServer _server;
        long _now;
        FakeRoomSync _alice, _bob;

        [SetUp] public void SetUp()
        {
            _now = 1_000_000;
            _server = new FakeRoomServer { Now = () => _now };
            _alice = new FakeRoomSync(_server, "alice");
            _bob = new FakeRoomSync(_server, "bob");
        }

        static RoomPlayer Me(string name) => new RoomPlayer { name = name, platform = Platforms.Quest };

        [Test] public void RoomCode_IsFiveConsonants_AndNormalizesWhatWasTyped()
        {
            for (int i = 0; i < 200; i++)
            {
                var code = RoomCode.New();
                Assert.AreEqual(RoomCode.Length, code.Length);
                Assert.IsTrue(code.All(c => RoomCode.Alphabet.IndexOf(c) >= 0), code);
                Assert.IsFalse(code.Any(c => "AEIOUY".IndexOf(c) >= 0), code);
            }
            Assert.AreEqual("BCDFG", RoomCode.Normalize("  bcdfg "));
            Assert.IsNull(RoomCode.Normalize("ABCDE"));
            Assert.IsNull(RoomCode.Normalize("BCDF"));
            Assert.IsNull(RoomCode.Normalize(null));
        }

        [Test] public void RoomCode_ExpiresAfterTwoHours()
        {
            Assert.IsFalse(RoomCode.IsExpired(0, RoomCode.LifetimeMs));
            Assert.IsTrue(RoomCode.IsExpired(0, RoomCode.LifetimeMs + 1));
        }

        [Test] public void Host_CreatesARoom_AndAGuestJoinsTheOtherSeat()
        {
            IReadOnlyDictionary<string, RoomPlayer> seen = null;
            _alice.PlayersChanged += p => seen = p;
            var code = Run(_alice.CreateAsync("seven-hot-stones", "yo", Me("Alice")));
            Assert.IsTrue(_alice.IsHost);

            var info = Run(_bob.JoinAsync(code.ToLowerInvariant(), Me("Bob")));
            Assert.AreEqual("alice", info.host);
            Assert.AreEqual(RoomStates.Lobby, info.state);
            Assert.IsFalse(_bob.IsHost);
            Assert.AreEqual(0, seen["alice"].seat);
            Assert.AreEqual(1, seen["bob"].seat);
            Assert.IsTrue(seen["bob"].online);
        }

        [Test] public void Join_RefusesAMissingAFullOrAnExpiredRoom()
        {
            Assert.AreEqual(RoomError.NotFound, Assert.Throws<RoomException>(() => Run(_bob.JoinAsync("BCDFG", Me("Bob")))).Error);
            var code = Run(_alice.CreateAsync("s1", "en", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            var carol = new FakeRoomSync(_server, "carol");
            Assert.AreEqual(RoomError.Full, Assert.Throws<RoomException>(() => Run(carol.JoinAsync(code, Me("Carol")))).Error);

            var dave = new FakeRoomSync(_server, "dave");
            var late = Run(new FakeRoomSync(_server, "erin").CreateAsync("s1", "en", Me("Erin")));
            _now += RoomCode.LifetimeMs + 1;
            Assert.AreEqual(RoomError.Expired, Assert.Throws<RoomException>(() => Run(dave.JoinAsync(late, Me("Dave")))).Error);
        }

        [Test] public void OnlyTheHost_ChangesTheStateAndTheClock_AndEveryoneHearsIt()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            RoomClock heard = null; string state = null;
            _bob.ClockChanged += c => heard = c;
            _bob.RoomChanged += r => state = r.state;

            Run(_alice.SetStateAsync(RoomStates.Story));
            Run(_alice.SetClockAsync(new RoomClock { lineIndex = 3, startedAtServerMs = _now }));
            Assert.AreEqual(RoomStates.Story, state);
            Assert.AreEqual(3, heard.lineIndex);
            _now += 1500;
            Assert.AreEqual(1500, heard.ElapsedMs(_bob.ServerTimeMs));

            Assert.AreEqual(RoomError.NotHost, Assert.Throws<RoomException>(() => Run(_bob.SetClockAsync(new RoomClock { lineIndex = 9 }))).Error);
            Assert.AreEqual(RoomError.NotHost, Assert.Throws<RoomException>(() => Run(_bob.SetStateAsync(RoomStates.Game))).Error);
        }

        [Test] public void Poses_ReachTheOtherPlayer_AtMostTenASecond()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            var got = new List<string>();
            _alice.PoseReceived += (uid, _) => got.Add(uid);
            _bob.PoseReceived += (uid, _) => got.Add("echo " + uid);
            var pose = new PoseFrame { h = PoseFrame.Pack(0, 1.6f, 0, 0, 0, 0, 1) };

            _bob.SendPose(pose);
            _now += 40; _bob.SendPose(pose);   // too soon: dropped
            _now += 60; _bob.SendPose(pose);   // 100 ms after the first
            CollectionAssert.AreEqual(new[] { "bob", "bob" }, got);
            Assert.Throws<System.ArgumentException>(() => { _now += 200; _bob.SendPose(new PoseFrame { h = new[] { 1f, 2f } }); });
        }

        [Test] public void Presence_GoesOfflineWhenTheConnectionDrops()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            IReadOnlyDictionary<string, RoomPlayer> seen = null;
            _alice.PlayersChanged += p => seen = p;
            _server.Disconnect(code, "bob");
            Assert.IsFalse(seen["bob"].online);

            Run(_bob.UpdateMeAsync(new RoomPlayer { name = "Bob", platform = Platforms.Web, handUp = true }));
            Assert.IsTrue(seen["bob"].online);
            Assert.IsTrue(seen["bob"].handUp);
            Assert.AreEqual(1, seen["bob"].seat, "the seat is kept");
        }

        [Test] public void Leaving_FreesTheSeat_AndTheHostLeavingClosesTheRoom()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            int closed = 0; _bob.Closed += () => closed++;
            Run(_bob.LeaveAsync());
            Assert.IsNull(_bob.Code);
            Assert.AreEqual(1, closed);

            var carol = new FakeRoomSync(_server, "carol");
            Run(carol.JoinAsync(code, Me("Carol")));
            bool carolClosed = false; carol.Closed += () => carolClosed = true;
            Run(_alice.LeaveAsync());
            Assert.IsTrue(carolClosed);
            Assert.AreEqual(RoomError.NotFound, Assert.Throws<RoomException>(() => Run(_bob.JoinAsync(code, Me("Bob")))).Error);
        }

        [Test] public void Cleanup_RemovesRoomsOlderThanTwoHours()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            bool closed = false; _alice.Closed += () => closed = true;
            Assert.AreEqual(0, _server.RemoveExpired());
            _now += RoomCode.LifetimeMs + 1;
            Assert.AreEqual(1, _server.RemoveExpired());
            Assert.IsTrue(closed);
            Assert.IsNull(_alice.Code);
            _ = code;
        }

        [Test] public void GameResults_ReachEveryoneInTheRoom()
        {
            var code = Run(_alice.CreateAsync("s1", "yo", Me("Alice")));
            Run(_bob.JoinAsync(code, Me("Bob")));
            var scores = new Dictionary<string, int>();
            _alice.GameResultReceived += (uid, r) => scores[uid] = r.score;
            Run(_bob.SubmitGameAsync(new GameResult { startedAt = _now, finishedMs = 4200, score = 7 }));
            Assert.AreEqual(7, scores["bob"]);
        }

        [Test] public void ProfileStore_LoadsWhatWasSaved_AndReportsChangesFromElsewhere()
        {
            var store = new FakeProfileStore();
            Assert.IsNull(Run(store.LoadAsync()));
            Run(store.SaveAsync(new UserProfile { username = "timi", displayName = "Timi", appearance = "{\"skin\":4}", appearanceVersion = 2 }));
            Assert.AreEqual("timi", Run(store.LoadAsync()).username);
            UserProfile seen = null; store.ChangedElsewhere += p => seen = p;
            store.SimulateChangeElsewhere(new UserProfile { username = "timi", displayName = "Timi O." });
            Assert.AreEqual("Timi O.", seen.displayName);
            Assert.IsInstanceOf<FakeProfileStore>(new HybridServices().Profiles);
        }

        [Test] public void Profile_SerializesWithTheFirestoreFieldNames()
        {
            var json = UnityEngine.JsonUtility.ToJson(new UserProfile { username = "timi", appearanceVersion = 2, lastDevice = "quest" });
            foreach (var field in new[] { "username", "displayName", "language", "appearance", "appearanceVersion", "createdAt", "lastDevice", "lastSeenAt" })
                StringAssert.Contains("\"" + field + "\"", json);
            Assert.AreEqual("users/u1/progress/current", FirestorePaths.Progress("u1"));
            Assert.AreEqual("users/u1/words/w9", FirestorePaths.Word("u1", "w9"));
        }

        [Test] public void RouteChip_ReadsAsTheContractSays()
        {
            Assert.AreEqual("On device · Gemini Nano · 0.4 s", new RouteDecision(Route.OnDevice, "Gemini Nano", "short prompt", 400).Chip());
            Assert.AreEqual("Cloud · Gemini · 1.2 s", new RouteDecision(Route.CloudGemini, "Gemini", "long prompt", 1200).Chip());
            Assert.AreEqual("Hypa · Yorùbá · 2.1 s", new RouteDecision(Route.Hypa, "Yorùbá", "yo", 2149).Chip());
            Assert.AreEqual("Offline · on device", new RouteDecision(Route.Offline, "on device", "no network", 0).Chip());
        }
    }
}
