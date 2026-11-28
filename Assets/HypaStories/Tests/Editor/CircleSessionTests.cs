using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;
using UnityEngine;

namespace HypaStories.Tests
{
    public class CircleSessionTests
    {
        sealed class FakeStory : ICircleNarration
        {
            public int LineIndex { get; set; }
            public int LineCount { get; set; } = 20;
            public bool Paused { get; set; }
            public bool Ended { get; set; }
            public bool Narrating { get; set; }
            public bool Following { get; set; }
            public Func<int, CancellationToken, Task> LineGate { get; set; }
            public event Action UserPaused;
            public readonly List<string> Calls = new List<string>();
            public void FollowTo(int line) { Calls.Add("follow " + line); LineIndex = line; Paused = false; }
            public void Pause() { Calls.Add("pause"); Paused = true; }
            public void Interrupt() { Calls.Add("interrupt"); Paused = true; }
            public void Resume(int line) { Calls.Add("resume " + line); Paused = false; }
            public void PauseByUser() { Paused = true; UserPaused?.Invoke(); }
            /// <summary>What StoryPlayer does when line i's audio is ready.</summary>
            public void Reach(int line) { LineIndex = line; LineGate?.Invoke(line, CancellationToken.None).GetAwaiter().GetResult(); }
        }

        sealed class FakeSpeech : IStorySpeech
        {
            public readonly List<string> Spoken = new List<string>();
            public Task<SpeechLine> PrepareAsync(string text, StoryLanguage language, CancellationToken ct) => Task.FromResult(new SpeechLine { Text = text });
            public Task PlayAsync(SpeechLine line, Action<double> onStart, CancellationToken ct) { Spoken.Add(line.Text); return Task.CompletedTask; }
            public void SkipCurrent() { }
            public void StopAll() { }
        }

        static T Run<T>(Task<T> t) => t.GetAwaiter().GetResult();
        static void Run(Task t) => t.GetAwaiter().GetResult();
        static readonly StoryLanguage En = StoryLanguages.Find("en");
        static RoomPlayer Me(string name, string platform) => new RoomPlayer { name = name, platform = platform };

        long _now;
        FakeRoomServer _server;
        FakeRoomSync _aliceRoom, _bobRoom;
        FakeSpeech _aliceVoice, _bobVoice;
        CircleSession _alice, _bob;
        FakeStory _aliceStory, _bobStory;
        readonly List<int> _waits = new List<int>();
        string _code;

        [SetUp] public void SetUp()
        {
            _now = 5_000_000;
            _waits.Clear();
            _server = new FakeRoomServer { Now = () => _now };
            _aliceRoom = new FakeRoomSync(_server, "alice");
            _bobRoom = new FakeRoomSync(_server, "bob");
            _aliceVoice = new FakeSpeech(); _bobVoice = new FakeSpeech();
            Func<int, CancellationToken, Task> delay = (ms, ct) => { _waits.Add(ms); return Task.CompletedTask; };
            _alice = new CircleSession(_aliceRoom, "alice", _aliceVoice, () => En, delay, n => 0);
            _bob = new CircleSession(_bobRoom, "bob", _bobVoice, () => En, delay, n => 0);
            _aliceStory = new FakeStory(); _bobStory = new FakeStory();
        }

        void HostAndGuest()
        {
            _code = Run(_alice.CreateAsync("kings-drum", "en", Me("Alice", Platforms.Web)));
            _alice.AttachStory(_aliceStory);
            Run(_bob.JoinAsync(_code, Me("Bob", Platforms.Android)));
            _bob.AttachStory(_bobStory);
        }

        [Test] public void TheHostAnnouncesEachLine_AndTheGuestPlaysItAtTheSameServerTime()
        {
            HostAndGuest();
            Assert.IsFalse(_aliceStory.Following);
            Assert.IsTrue(_bobStory.Following);

            _aliceStory.Reach(2);
            CollectionAssert.AreEqual(new[] { CircleSession.LeadMs }, _waits, "the host waits for the start it announced");
            CollectionAssert.AreEqual(new[] { "follow 2" }, _bobStory.Calls);

            _waits.Clear();
            _now += 120; // the clock took 120 ms to arrive and the line's audio is ready
            _bobStory.LineGate(2, CancellationToken.None).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { CircleSession.LeadMs - 120 }, _waits, "the guest starts at the same server time");
        }

        [Test] public void ALateGuest_StartsAtTheCurrentLine()
        {
            _code = Run(_alice.CreateAsync("kings-drum", "en", Me("Alice", Platforms.Quest)));
            _alice.AttachStory(_aliceStory);
            _aliceStory.Reach(7);

            Run(_bob.JoinAsync(_code, Me("Bob", Platforms.Web))); // the clock arrives before the story is loaded
            Assert.IsEmpty(_bobStory.Calls);
            _bob.AttachStory(_bobStory);
            CollectionAssert.AreEqual(new[] { "follow 7" }, _bobStory.Calls);

            _waits.Clear();
            _now += 60_000; // long after the line began: play it at once, never wait
            _bobStory.LineGate(7, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsEmpty(_waits);
        }

        [Test] public void TheHostPausing_PausesTheGuest_AndResumingCarriesThemOn()
        {
            HostAndGuest();
            _aliceStory.Reach(3);
            _aliceStory.PauseByUser();
            Assert.AreEqual("pause", _bobStory.Calls[_bobStory.Calls.Count - 1]);
            _now += 4000;
            _aliceStory.Reach(3); // resumed: the line is announced again with a new start
            Assert.AreEqual("follow 3", _bobStory.Calls[_bobStory.Calls.Count - 1]);
        }

        [Test] public void TheGuestAsks_TheHostStopsListensSpeaksTheAnswer_ThenRestartsTheLine()
        {
            HostAndGuest();
            _aliceStory.Reach(4);
            var began = 0; string attend = "fire";
            _bob.BeginAsk += () => began++;
            _alice.AttendChanged += uid => attend = uid ?? "fire";

            Assert.IsTrue(_bob.RequestAsk());
            Assert.AreEqual(1, began);
            Assert.AreEqual("bob", _alice.Turn);
            Assert.AreEqual("bob", attend, "the storyteller turns to the asker");
            CollectionAssert.Contains(_aliceStory.Calls, "interrupt");
            Assert.IsFalse(_alice.RequestAsk(), "the turn is locked");

            _bob.OnLocalSentence(0, "The drum was a gift.", false);
            _bob.OnLocalSentence(1, "Nobody else could play it.", true);
            CollectionAssert.AreEqual(new[] { "The drum was a gift.", "Nobody else could play it." }, _aliceVoice.Spoken);

            _bob.OnLocalAskEnded();
            Assert.IsNull(_alice.Turn);
            Assert.AreEqual("fire", attend);
            Assert.AreEqual(StoryLanguages.Lines("en").Resume[0], _aliceVoice.Spoken[2], "\"Now, where was I…\"");
            Assert.AreEqual("resume 4", _aliceStory.Calls[_aliceStory.Calls.Count - 1]);
        }

        [Test] public void TheHostAsks_TheGuestHearsTheAnswer_ThenFollowsTheClockAgain()
        {
            HostAndGuest();
            _aliceStory.Reach(5);
            _alice.BeginAsk += () => { };
            Assert.IsTrue(_alice.RequestAsk());
            CollectionAssert.Contains(_bobStory.Calls, "interrupt");

            _alice.OnLocalSentence(0, "It was made of iroko wood.", true);
            CollectionAssert.AreEqual(new[] { "It was made of iroko wood." }, _bobVoice.Spoken);

            _now += 9000;
            _aliceStory.Reach(5); // AskFlow restarted the line on the host, which announces it
            Assert.AreNotEqual("follow 5", _bobStory.Calls[_bobStory.Calls.Count - 1], "the guest is still speaking the answer");
            _alice.OnLocalAskEnded();
            Assert.AreEqual("follow 5", _bobStory.Calls[_bobStory.Calls.Count - 1], "then it catches up with the clock");
        }

        [Test] public void TwoHandsAtOnce_TheLowerSeatsQuestionIsTaken()
        {
            HostAndGuest();
            var cancelled = 0;
            _bob.BeginAsk += () => { };
            _bob.CancelAsk += () => cancelled++;
            Assert.IsTrue(_bob.RequestAsk());
            _alice.Dispose(); // stand in for a host whose own raised hand crossed bob's on the way
            Run(_aliceRoom.UpdateMeAsync(new RoomPlayer { name = "Alice", platform = Platforms.Web, handUp = true }));

            Assert.AreEqual("alice", _bob.Turn);
            Assert.AreEqual(1, cancelled);
            Assert.IsFalse(_bob.Players["bob"].handUp, "the guest's hand comes down");
        }

        [Test] public void WhenTheHostDrops_TheGuestTakesTheRoom_AndCarriesTheStoryOn()
        {
            HostAndGuest();
            _aliceStory.Reach(6);
            _bobStory.LineIndex = 6; // the guest told line 6 and waits for the next
            _server.Disconnect(_code, "alice");

            Assert.IsTrue(_bob.IsHost);
            Assert.IsFalse(_bobStory.Following);
            Assert.AreEqual("follow 7", _bobStory.Calls[_bobStory.Calls.Count - 1]);
            var seen = -1;
            _aliceRoom.ClockChanged += c => seen = c.lineIndex; // the old host, coming back, hears the new host's clock
            _bobStory.Reach(7);
            Assert.AreEqual(7, seen, "the new host announces lines");
        }

        [Test] public void PoseTrack_BlendsBetweenFrames_AndHoldsTheLast()
        {
            var track = new PoseTrack();
            track.Push(new PoseFrame { h = PoseFrame.Pack(0, 1.6f, 0, 0, 0, 0, 1), t = 1000 });
            track.Push(new PoseFrame { h = PoseFrame.Pack(1, 1.6f, 0, 0, 0, 0, 1), r = PoseFrame.Pack(0.3f, 1.2f, 0.2f, 0, 0, 0, 1), t = 1100 });
            track.Push(new PoseFrame { h = PoseFrame.Pack(9, 9, 9, 0, 0, 0, 1), t = 1050 }); // late: dropped
            Assert.AreEqual(2, track.Count);

            Assert.IsTrue(track.Sample(1050 + PoseTrack.DelayMs, out var head, out var left, out var right));
            Assert.AreEqual(0.5f, head.Position.x, 1e-4f);
            Assert.IsFalse(left.Tracked);
            Assert.IsTrue(right.Tracked, "a hand coming into view appears halfway, not from the origin");

            track.Sample(5000, out head, out _, out _);
            Assert.AreEqual(1f, head.Position.x, 1e-4f);
            Assert.IsFalse(new PoseTrack().Sample(5000, out _, out _, out _));
        }
    }
}
