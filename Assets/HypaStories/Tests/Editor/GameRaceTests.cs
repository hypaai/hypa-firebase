using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaStories.Tests
{
    public class GameRaceTests
    {
        long _now, _local;
        FakeRoomServer _server;
        FakeRoomSync _aliceRoom, _bobRoom;
        GameRace _alice, _bob;
        int _aliceGo, _bobGo;
        string _code;

        static void Run(Task t) => t.GetAwaiter().GetResult();
        static T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();

        [SetUp] public void SetUp()
        {
            _now = 7_000_000; _local = 0;
            _aliceGo = _bobGo = 0;
            _server = new FakeRoomServer { Now = () => _now };
            _aliceRoom = new FakeRoomSync(_server, "alice");
            _bobRoom = new FakeRoomSync(_server, "bob");
            _alice = new GameRace(_aliceRoom, "alice", localMs: () => _local);
            _bob = new GameRace(_bobRoom, "bob", localMs: () => _local);
            _alice.Go += () => _aliceGo++;
            _bob.Go += () => _bobGo++;
            _code = Run(_aliceRoom.CreateAsync("seven-hot-stones", "en", new RoomPlayer { name = "Ada", platform = Platforms.Web }));
            Run(_bobRoom.JoinAsync(_code, new RoomPlayer { name = "Bayo", platform = Platforms.Android }));
        }

        void Tick() { _alice.Tick(); _bob.Tick(); }

        void StartAndGo()
        {
            _alice.Start();
            _now = _alice.StartAtServerMs;
            Tick();
        }

        [Test] public void PlayTogether_BothCountDownToTheSameServerTime_ThenGo()
        {
            string roomState = null;
            _bobRoom.RoomChanged += r => roomState = r.state;
            _alice.Start();
            Assert.AreEqual(RaceState.Countdown, _alice.State);
            Assert.AreEqual(RaceState.Countdown, _bob.State, "the other player joins the round");
            Assert.AreEqual(_alice.StartAtServerMs, _bob.StartAtServerMs);
            Assert.AreEqual(_now + GameRace.LeadMs + GameRace.CountdownMs, _alice.StartAtServerMs);
            Assert.AreEqual(RoomStates.Game, roomState, "the host put the room in its game state");

            _now = _alice.StartAtServerMs - 2500;
            Tick();
            Assert.AreEqual(3, _bob.CountdownSeconds);
            Assert.AreEqual(0, _aliceGo + _bobGo);
            _now = _alice.StartAtServerMs;
            Tick();
            Assert.AreEqual(1, _aliceGo);
            Assert.AreEqual(1, _bobGo);
            Assert.AreEqual(RaceState.Playing, _bob.State);
        }

        [Test] public void LiveScores_ReachTheOtherPlayer_AtMostOnceASecond()
        {
            StartAndGo();
            _alice.Report(10);
            Assert.AreEqual(10, _bob.Standings()[1].Score);
            Assert.AreEqual("Ada", _bob.Standings()[1].Name);
            _local += 300;
            _alice.Report(20); // too soon: shown here, sent later
            Assert.AreEqual(20, _alice.Standings()[0].Score);
            Assert.AreEqual(10, _bob.Standings()[1].Score);
            _local += 800;
            _alice.Report(20);
            Assert.AreEqual(20, _bob.Standings()[1].Score);
        }

        [Test] public void TheHigherScoreWins_AndATieGoesToTheFasterGame()
        {
            StartAndGo();
            _alice.Finish(70, 52_000);
            Assert.AreEqual(RaceState.Waiting, _alice.State, "waits for the other player");
            _bob.Finish(60, 41_000);
            Assert.AreEqual(RaceState.Result, _alice.State);
            Assert.AreEqual(RaceState.Result, _bob.State);
            Assert.IsTrue(_alice.Result.YouWon);
            Assert.IsFalse(_bob.Result.YouWon);
            Assert.AreEqual("Ada", _bob.Result.WinnerName);
            Assert.AreEqual(52_000, _bob.Result.WinnerMs, "points first: the slower game with more points wins");

            var tie = GameRace.Decide(new RaceStanding { Uid = "a", Score = 50, FinishedMs = 40_000, Finished = true },
                                      new RaceStanding { Uid = "b", Name = "Bayo", Score = 50, FinishedMs = 38_000, Finished = true });
            Assert.AreEqual("b", tie.WinnerUid);
            Assert.IsTrue(GameRace.Decide(new RaceStanding { Score = 5, FinishedMs = 1 }, new RaceStanding { Score = 5, FinishedMs = 1, Finished = true }).Draw);
        }

        [Test] public void APlayerWhoLeavesMidGame_Loses_WhenTheOtherFinishes()
        {
            StartAndGo();
            Run(_bobRoom.LeaveAsync());
            Assert.AreEqual(RaceState.Playing, _alice.State, "the other carries on");
            _alice.Finish(30, 60_000);
            Assert.AreEqual(RaceState.Result, _alice.State);
            Assert.IsTrue(_alice.Result.YouWon);
            Assert.IsTrue(_alice.Result.ByDefault);
        }

        [Test] public void BackToTheFireMidGame_IsAForfeit()
        {
            StartAndGo();
            _alice.Report(40);
            _bob.Leave();
            Assert.AreEqual(RaceState.Idle, _bob.State);
            Assert.IsTrue(_alice.Standings()[1].Left);
            _alice.Finish(40, 30_000);
            Assert.IsTrue(_alice.Result.YouWon);
        }

        [Test] public void EitherPlayer_CanStartARematch()
        {
            StartAndGo();
            _alice.Finish(10, 20_000);
            _bob.Finish(20, 25_000);
            var first = _alice.StartAtServerMs;
            _now += 5000;
            Assert.IsTrue(_bob.CanStart);
            _bob.Start(); // the guest taps Rematch
            Assert.AreEqual(RaceState.Countdown, _alice.State);
            Assert.Greater(_alice.StartAtServerMs, first);
            Assert.IsNull(_alice.Result);
            Assert.AreEqual(0, _alice.Standings()[1].Score, "a new round starts from nothing");
        }

        [Test] public void AnOldRound_IsNotJoined()
        {
            var lateRoom = new FakeRoomSync(_server, "late");
            Run(_bobRoom.LeaveAsync());
            Run(lateRoom.JoinAsync(_code, new RoomPlayer { name = "Late" }));
            var late = new GameRace(lateRoom, "late", localMs: () => _local);
            _now += 60_000;
            Run(_aliceRoom.SubmitGameAsync(new GameResult { startedAt = _now - 30_000, finishedMs = 0, score = 0 })); // replayed from long ago
            Assert.AreEqual(RaceState.Idle, late.State);
        }

        [Test] public void Scores_ComeFromTheGamesOwnRules()
        {
            Assert.AreEqual(0, RaceScore.Dodo(new DodoRules()));
            Assert.AreEqual(0, RaceScore.Hat(new HatRules()));
            Assert.AreEqual(0, RaceScore.Drum(new DrumRules()));
            Assert.IsFalse(RaceScore.DodoOver(new DodoRules()));
            Assert.IsFalse(RaceScore.HatOver(new HatRules()));
            Assert.IsFalse(RaceScore.DrumOver(new DrumRules()));
            Assert.AreEqual(0, RaceScore.Dodo(null));
        }
    }
}
