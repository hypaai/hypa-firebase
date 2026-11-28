using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace HypaStories.Tests
{
    /// <summary>HYP-205 (G10): talking with the old woman. Turns and barge-in, her replies and actions, her persona, both engines.</summary>
    public class TalkTests
    {
        const int Frame = 1600; // 100 ms at 16 kHz

        static float[] Tone(float amp, int n = Frame)
        {
            var f = new float[n];
            for (var i = 0; i < n; i++) f[i] = amp * Mathf.Sin(i * 0.3f);
            return f;
        }

        static readonly StoryLanguage En = StoryLanguages.Find("en"), Yo = StoryLanguages.Find("yo");

        // ---------- turns ----------

        static TurnDetector.Signal[] Feed(TurnDetector d, float rms, int frames, bool sheSpeaks = false)
            => Enumerable.Range(0, frames).Select(_ => d.Update(rms, 100f, sheSpeaks)).ToArray();

        /// <summary>A detector that has listened to a quiet room (no turn starts before it has).</summary>
        static TurnDetector Ready(TurnDetector d = null)
        {
            d = d ?? new TurnDetector();
            Feed(d, 0.001f, 8);
            return d;
        }

        [Test] public void ATurn_StartsOnSpeech_AndEndsAfter700msOfQuiet()
        {
            var d = Ready();
            var s = Feed(d, 0.2f, 6);
            Assert.AreEqual(TurnDetector.Signal.Started, s[1], "after 120 ms of speech");
            Assert.IsTrue(d.InTurn);
            var q = Feed(d, 0.001f, 8);
            CollectionAssert.AreEqual(Enumerable.Repeat(TurnDetector.Signal.None, 7), q.Take(7), "not before 700 ms");
            Assert.AreEqual(TurnDetector.Signal.Ended, q[7]);
            Assert.IsFalse(d.InTurn);
        }

        [Test] public void AClick_IsNotATurn_AndTooLittleSpeech_IsDropped()
        {
            var d = Ready();
            Assert.AreEqual(TurnDetector.Signal.None, d.Update(0.3f, 100f, false), "100 ms is under the onset");
            d.Update(0.001f, 100f, false);
            Assert.IsFalse(d.InTurn);
            d.MinSpeechMs = 500f;
            Feed(d, 0.2f, 2);
            Assert.IsTrue(d.InTurn);
            Assert.Contains(TurnDetector.Signal.Dropped, Feed(d, 0.001f, 8));
        }

        [Test] public void BargeIn_Needs300msOfAVoiceLouderThanHerEcho()
        {
            var d = new TurnDetector();
            Assert.IsFalse(Feed(d, 0.04f, 10, true).Contains(TurnDetector.Signal.BargeIn), "her voice from the speaker (under 2.5× the threshold) never interrupts");
            var s = Feed(d, 0.2f, 3, true);
            CollectionAssert.AreEqual(new[] { TurnDetector.Signal.None, TurnDetector.Signal.None, TurnDetector.Signal.BargeIn }, s);
            Assert.IsTrue(d.InTurn, "the interruption is the start of the player's turn");
        }

        [Test] public void HerVoicesTail_NeverStartsATurn()
        {
            var d = Ready();
            var echo = d.Threshold * 1.5f; // her voice from the speaker, as loud as speech
            Feed(d, echo, 5, true);
            Assert.IsFalse(Feed(d, echo, 5).Contains(TurnDetector.Signal.Started), "the room's echo for 600 ms after she stops is hers");
            Assert.IsTrue(Feed(d, 0.2f, 3).Contains(TurnDetector.Signal.Started), "after that, the player is heard");
        }

        [Test] public void ASteadyRoomAboveTheThreshold_IsLearned_AndTheTurnStillEnds()
        {
            var d = new TurnDetector();
            var room = d.Threshold * 2f; // the fire and wind through a laptop's speakers, and its fan
            Assert.IsFalse(Feed(d, room, 30).Contains(TurnDetector.Signal.Started), "the room is not the player");
            Assert.Contains(TurnDetector.Signal.Started, Feed(d, 0.2f, 4));
            Assert.AreEqual(TurnDetector.Signal.Ended, Feed(d, room, 8).Last(), "back to the room's level is quiet");
        }

        [Test] public void ACrackleInThePause_DoesNotKeepTheTurnOpen()
        {
            var d = Ready(new TurnDetector { BlipMs = 150f }); // frames here are 100 ms; a 16 ms frame on a device
            Feed(d, 0.2f, 4);
            var after = Enumerable.Range(0, 8).Select(i => d.Update(i == 3 ? 0.2f : 0.001f, 100f, false)).ToArray();
            Assert.AreEqual(TurnDetector.Signal.Ended, after.Last());
        }

        [Test] public void Finish_EndsTheTurnNow_OrDropsAMumble()
        {
            var d = Ready();
            Assert.AreEqual(TurnDetector.Signal.None, d.Finish(), "nothing to end");
            Feed(d, 0.2f, 4);
            Assert.AreEqual(TurnDetector.Signal.Ended, d.Finish());
            Assert.IsFalse(d.InTurn);

            var m = Ready(new TurnDetector { MinSpeechMs = 1000f });
            Feed(m, 0.2f, 2);
            Assert.AreEqual(TurnDetector.Signal.Dropped, m.Finish(), "too little to answer");
        }

        [Test] public void WithNoEchoCanceller_NothingHeardWhileSheSpeaks_Counts()
        {
            var d = Ready(new TurnDetector { CanBargeIn = false });
            Assert.IsFalse(Feed(d, 0.5f, 10, true).Any(x => x != TurnDetector.Signal.None), "however loud: it may be her own voice");
            Assert.IsFalse(d.InTurn);
            Feed(d, 0.001f, 6);
            Assert.Contains(TurnDetector.Signal.Started, Feed(d, 0.2f, 3), "once she is done, the player is heard");
        }

        [Test] public void ANoisyRoom_RaisesTheFloor()
        {
            var d = new TurnDetector();
            Feed(d, 0.012f, 300); // steady hum, under the threshold
            Assert.Greater(d.Level(false), d.Threshold, "speech must stand out from the room");
            Assert.IsFalse(Feed(d, 0.03f, 5).Contains(TurnDetector.Signal.Started), "a little louder than the hum is not speech");
        }

        // ---------- her replies on the Hypa path ----------

        [Test] public void Reply_PlainText_IsAllSpeech()
        {
            var r = TalkReply.Parse("Ah, my child. Sit down.");
            Assert.AreEqual("Ah, my child. Sit down.", r.Say);
            Assert.IsNull(r.Action);
        }

        [Test] public void Reply_Json_GivesTheWordsAndTheAction()
        {
            var r = TalkReply.Parse("{\"say\": \"This is my pot {old}.\", \"action\": {\"name\": \"show_item\", \"args\": {\"id\": \"clay-pot\"}}}");
            Assert.AreEqual("This is my pot {old}.", r.Say);
            Assert.AreEqual("show_item", r.Action.Name);
            Assert.AreEqual("clay-pot", r.Action.Arg("id"));
        }

        [Test] public void Reply_FencedOrTrailingJson_AndFlatActions_AreRead()
        {
            var fenced = TalkReply.Parse("```json\n{\"say\": \"Listen.\", \"action\": {\"name\": \"tell_item_story\", \"args\": {\"id\": \"broom\"}}}\n```");
            Assert.AreEqual("Listen.", fenced.Say);
            Assert.AreEqual("broom", fenced.Action.Arg("id"));

            var trailing = TalkReply.Parse("Say it with me: omi. {\"action\": {\"name\": \"teach_word\", \"word\": \"omi\", \"language\": \"yo\"}}");
            Assert.AreEqual("Say it with me: omi.", trailing.Say);
            Assert.AreEqual("teach_word", trailing.Action.Name);
            Assert.AreEqual("yo", trailing.Action.Arg("language"));
        }

        [Test] public void Reply_UnknownActionsAndBrokenJson_AreNeverDone()
        {
            Assert.IsNull(TalkReply.Parse("{\"say\": \"Hm.\", \"action\": {\"name\": \"delete_everything\"}}").Action);
            Assert.AreEqual("{\"say\": \"Hm.", TalkReply.Parse("{\"say\": \"Hm.").Say, "unclosed: spoken as it came");
            Assert.AreEqual("", TalkReply.Parse(null).Say);
        }

        sealed class FakeHost : ITalkHost
        {
            public readonly List<string> Done = new List<string>();
            public string ShowItem(string id) { Done.Add("show " + id); return "lit"; }
            public string TellItemStory(string id) { Done.Add("tell " + id); return "playing"; }
            public string TeachWord(string word, string language) { Done.Add($"teach {word} {language}"); return "saved"; }
            public string SuggestStory(string storyId) { Done.Add("suggest " + storyId); return "offered"; }
        }

        [Test] public void Actions_RunOnTheHost_AndBadCallsAreToldBack()
        {
            var host = new FakeHost();
            Assert.AreEqual("lit", TalkActions.Run(host, new TalkAction { Name = "show_item", Args = new Dictionary<string, string> { ["id"] = "broom" } }));
            StringAssert.Contains("needs \"id\"", TalkActions.Run(host, new TalkAction { Name = "show_item" }));
            StringAssert.Contains("no action called", TalkActions.Run(host, new TalkAction { Name = "fly" }));
            CollectionAssert.AreEqual(new[] { "show broom" }, host.Done);
            Assert.AreEqual("hut/oil-lamp", TalkActions.ItemId("Oil Lamp"));
            Assert.AreEqual("hut/clay-pot", TalkActions.ItemId("hut/clay-pot"));
        }

        [Test] public void Tools_AreTheFourActions_WithTheHutsIds()
        {
            var tools = TalkActions.Tools(new[] { "clay-pot", "broom" }, new[] { "kings-drum" });
            CollectionAssert.AreEqual(TalkActions.Names, tools.Select(t => t.Name));
            StringAssert.Contains("clay-pot, broom", tools[0].Parameters["id"]);
            StringAssert.Contains("kings-drum", tools[3].Parameters["storyId"]);
            CollectionAssert.AreEquivalent(new[] { "word", "language" }, tools[2].Parameters.Keys);
        }

        // ---------- her persona ----------

        [Test] public void Persona_LoadsFromResources_WithEveryLineInEveryLanguage()
        {
            var p = ElderPersona.Load();
            Assert.IsNotEmpty(p.Who);
            Assert.IsNotEmpty(p.Rules);
            foreach (var kind in new[] { "greet", "offline", "timeUp", "again" })
                foreach (var lang in StoryLanguages.All)
                    Assert.IsNotEmpty(p.Line(kind, lang.Code), $"{kind} in {lang.Code}");
            foreach (var id in p.StoriesByTheFire.Concat(p.StoriesInTheVillage))
                Assert.IsNotNull(StoryLibrary.Find(id), id + " is a story");
            foreach (var id in p.StoriesByTheFire)
                Assert.IsTrue(StoryLibrary.Campfire.Any(s => s.Id == id), id + " is told at the fire");
            foreach (var lang in StoryLanguages.All)
                Assert.IsNotEmpty(p.VoicesFor(lang.Code), $"a woman's voice named for {lang.Code}: Auto picks a man's for some");
        }

        [Test] public void Persona_Voices_ALanguagesOwnFirst_ThenTheOnesForEveryLanguage()
        {
            var p = ElderPersona.Parse("{\"voices\": [{\"model\": \"hypaai/orpheus\", \"voice\": {\"yo\": \"Eniola\"}}, {\"model\": \"openai/gpt-4o-mini-tts\", \"voice\": \"sage\"}]}");
            CollectionAssert.AreEqual(new[] { "hypaai/orpheus Eniola", "openai/gpt-4o-mini-tts sage" }, p.VoicesFor("yo").Select(v => v.ToString()));
            CollectionAssert.AreEqual(new[] { "openai/gpt-4o-mini-tts sage" }, p.VoicesFor("pcm").Select(v => v.ToString()), "no voice of its own: the shared one");
            Assert.IsEmpty(ElderPersona.Parse("{}").VoicesFor("yo"), "none named: Hypa's Auto");
        }

        /// <summary>Her real persona and things (Resources), for the prompt tests.</summary>
        TalkContext Context(StoryLanguage lang, ElderPersona persona = null, ITalkHost host = null) => new TalkContext
        {
            Persona = persona ?? ElderPersona.Load(), Language = lang, Host = host ?? new FakeHost(),
            Items = HutItems.All.Select(TalkTopic.Item).ToList(),
            Fire = new[] { TalkTopic.Story(StoryLibrary.Find("kings-drum")) },
            Village = new[] { TalkTopic.Story(StoryLibrary.Find("girl-who-chose-hare")) },
        };

        /// <summary>A small made-up hut, for the engine tests (no Resources).</summary>
        static TalkContext Small(StoryLanguage lang, ITalkHost host = null) => new TalkContext
        {
            Persona = Quiet(), Language = lang, Host = host ?? new FakeHost(),
            Items = new[] { new TalkTopic("clay-pot", "The clay pot", "My mother's pot."), new TalkTopic("broom", "The broom", "Palm ribs.") },
            Fire = new[] { new TalkTopic("kings-drum", "The King's Magic Drum", "Tortoise and a drum.") },
        };

        [Test] public void Prompt_KnowsHerThings_TheStories_AndHerRules()
        {
            var ctx = Context(Yo);
            var live = ctx.Prompt(false);
            foreach (var item in ctx.Items)
            {
                StringAssert.Contains("[" + item.Id + "]", live);
                StringAssert.Contains(item.Title, live);
            }
            StringAssert.Contains("my mother's", ctx.Items.First(i => i.Id == "clay-pot").About.Replace("’", "'"), "her lines come from the item's own story");
            StringAssert.Contains("[kings-drum]", live);
            StringAssert.Contains("The Girl Who Chose Hare", live);
            StringAssert.Contains("one to three sentences", live);
            StringAssert.Contains("not a real person", live);
            StringAssert.Contains("Begin in Yoruba", live);
            StringAssert.DoesNotContain("\"say\"", live, "Live has function calling");
            StringAssert.Contains("{\"say\":", ctx.Prompt(true), "Hypa answers in JSON");

            var resumed = ctx.Prompt(false, new List<(bool, string)> { (false, "Hello"), (true, "Welcome, my child.") });
            StringAssert.Contains("Player: Hello\nYou: Welcome, my child.", resumed);
        }

        // ---------- which engine ----------

        [Test] public void Router_LiveLanguagesGoToLive_TheRestToHypa_NothingOffline()
        {
            var live = RouteContext.Set("en,fr,es,sw");
            Assert.AreEqual("Cloud · Gemini Live", TalkRouter.Decide(En, live, true, true, true).Chip());
            Assert.AreEqual("Hypa · Yorùbá", TalkRouter.Decide(Yo, live, true, true, true).Chip());
            Assert.AreEqual(Route.Hypa, TalkRouter.Decide(En, live, false, true, true).Route, "no Live here: Hypa speaks English too");
            Assert.AreEqual(Route.Offline, TalkRouter.Decide(En, live, true, false, true).Route);
            Assert.AreEqual(Route.Offline, TalkRouter.Decide(Yo, live, false, true, false).Route);
        }

        // ---------- the Hypa engine ----------

        sealed class FakeMic : ITalkMic
        {
            public readonly Queue<float[]> Frames = new Queue<float[]>();
            public bool Stopped, Echo = true;
            public string Error => null;
            public bool EchoCancelled => Echo;
            public float[] Read() => Frames.Count > 0 ? Frames.Dequeue() : null;
            public void Stop() => Stopped = true;
        }

        sealed class FakeSpeech : IStorySpeech
        {
            public readonly List<string> Played = new List<string>();
            public int Stops;
            public bool Block;
            /// <summary>Lines whose audio never comes (a slow TTS), until cancelled.</summary>
            public bool Slow;
            TaskCompletionSource<bool> _playing;

            public async Task<SpeechLine> PrepareAsync(string text, StoryLanguage language, CancellationToken ct)
            {
                if (Slow) { var never = new TaskCompletionSource<bool>(); using (ct.Register(() => never.TrySetCanceled())) await never.Task; }
                return new SpeechLine { Text = text, Seconds = 1f };
            }

            public async Task PlayAsync(SpeechLine line, Action<double> onStart, CancellationToken ct)
            {
                Played.Add(line.Text);
                onStart?.Invoke(1000);
                if (!Block) return;
                _playing = new TaskCompletionSource<bool>();
                using (ct.Register(() => _playing.TrySetResult(true))) await _playing.Task;
            }

            public void SkipCurrent() => _playing?.TrySetResult(true);
            public void StopAll() { Stops++; _playing?.TrySetResult(true); }
        }

        static ElderPersona Quiet() => ElderPersona.Parse("{\"who\": [\"An old woman.\"], \"rules\": [\"Short.\"], \"lines\": {\"timeUp\": {\"en\": \"Come back soon.\"}, \"again\": {\"en\": \"Again?\"}}}");

        SynchronizationContext _context;
        [SetUp] public void SetUp() { _context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() => SynchronizationContext.SetSynchronizationContext(_context);

        sealed class Rig
        {
            public readonly FakeMic Mic = new FakeMic();
            public readonly FakeSpeech Speech = new FakeSpeech();
            public readonly FakeHost Host = new FakeHost();
            public readonly List<ChatMessage[]> Chats = new List<ChatMessage[]>();
            public readonly List<TalkTurnLog> Logs = new List<TalkTurnLog>();
            public string Heard = "What is that pot?", Reply = "It is my water pot. It keeps the water cool.";
            /// <summary>Speech to text fails this many more times, as a dropped connection does.</summary>
            public int AsrFails;
            public double Now = 10000;
            public HypaTalk Talk;

            public Rig(TalkTests t, ElderPersona persona = null, float maxSeconds = 0f, bool echoCancelled = true)
            {
                Mic.Echo = echoCancelled;
                var ctx = Small(En, Host);
                if (persona != null) ctx.Persona = persona;
                ctx.MaxSeconds = maxSeconds;
                Talk = new HypaTalk(ctx, () => Mic, Speech,
                    (wav, lang, ct) => AsrFails-- > 0
                        ? Task.FromException<string>(new HypaProxyException(ProxyApi.ErrorUpstream, "The proxy could not be reached.", 0))
                        : Task.FromResult(Heard),
                    (messages, ct) => { Chats.Add(messages); return Task.FromResult(Reply); }, null, () => Now);
                Talk.Logged += Logs.Add;
                Talk.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                Say(0.001f, 8); // the room is listened to first
            }

            public void Say(float amp, int frames)
            {
                for (var i = 0; i < frames; i++) { Mic.Frames.Enqueue(Tone(amp)); Now += 100; Talk.Tick(0.1f); }
            }

            public void Turn() { Say(0.3f, 6); Say(0.001f, 8); }
        }

        [Test] public void Hypa_ATurn_IsHeardAnswered_AndSpokenSentenceBySentence()
        {
            var rig = new Rig(this);
            Assert.AreEqual(TalkState.Listening, rig.Talk.State);
            rig.Turn();
            Assert.AreEqual(1, rig.Chats.Count);
            var m = rig.Chats[0];
            Assert.AreEqual("system", m[0].role);
            Assert.AreEqual("user", m.Last().role);
            Assert.AreEqual("What is that pot?", m.Last().content);
            CollectionAssert.AreEqual(new[] { "It is my water pot.", "It keeps the water cool." }, rig.Speech.Played);
            Assert.AreEqual(TalkState.Listening, rig.Talk.State, "listening again once she has spoken");
            Assert.AreEqual(1, rig.Logs.Count);
            Assert.AreEqual("hypa", rig.Logs[0].Engine);
        }

        [Test] public void Hypa_BargeIn_StopsHer_AndDropsTheQueuedSentences()
        {
            var rig = new Rig(this);
            rig.Speech.Block = true;
            rig.Turn();
            CollectionAssert.AreEqual(new[] { "It is my water pot." }, rig.Speech.Played, "the first sentence is playing");
            Assert.AreEqual(TalkState.Speaking, rig.Talk.State);
            rig.Say(0.3f, 3); // 300 ms over her
            Assert.GreaterOrEqual(rig.Speech.Stops, 1);
            Assert.AreEqual(TalkState.Hearing, rig.Talk.State);
            CollectionAssert.AreEqual(new[] { "It is my water pot." }, rig.Speech.Played, "the second sentence never plays");
            Assert.IsTrue(rig.Logs[0].Interrupted);

            rig.Speech.Block = false;
            rig.Heard = "Tell me more.";
            rig.Say(0.3f, 2); rig.Say(0.001f, 8);
            Assert.AreEqual(2, rig.Chats.Count, "the interruption is answered");
        }

        [Test] public void Hypa_TheReplysAction_RunsAfterSheHasSaidIt_AndAWordIsRepeatedSlowly()
        {
            var rig = new Rig(this);
            rig.Reply = "{\"say\": \"This is my pot.\", \"action\": {\"name\": \"show_item\", \"args\": {\"id\": \"clay-pot\"}}}";
            rig.Turn();
            CollectionAssert.AreEqual(new[] { "show clay-pot" }, rig.Host.Done);

            rig.Reply = "{\"say\": \"Water is omi.\", \"action\": {\"name\": \"teach_word\", \"args\": {\"word\": \"omi\", \"language\": \"yo\"}}}";
            rig.Turn();
            Assert.AreEqual("omi… omi.", rig.Speech.Played.Last());
            Assert.AreEqual("teach omi yo", rig.Host.Done.Last());
        }

        [Test] public void Hypa_ChatHistory_IsHerPersona_ThenTheLast8Turns()
        {
            var rig = new Rig(this);
            for (var i = 0; i < 6; i++) { rig.Heard = "question " + i; rig.Turn(); }
            var m = rig.Chats.Last();
            Assert.AreEqual(9, m.Length, "system + 8");
            Assert.AreEqual("system", m[0].role);
            Assert.AreEqual("question 5", m[8].content);
            var hers = m.First(x => x.role == "assistant");
            Assert.AreEqual("It is my water pot. It keeps the water cool.", (string)JObject.Parse(hers.content)["say"], "her turns as the JSON she answers in");
        }

        [Test] public void Hypa_WithNoEchoCanceller_SheIsNotInterrupted_ByHerOwnVoice()
        {
            var rig = new Rig(this, echoCancelled: false);
            rig.Speech.Block = true;
            rig.Turn();
            Assert.AreEqual(TalkState.Speaking, rig.Talk.State);
            rig.Say(0.3f, 10); // her voice from the laptop's speakers, back in its microphone
            Assert.AreEqual(0, rig.Speech.Stops);
            Assert.AreEqual(TalkState.Speaking, rig.Talk.State);
            Assert.AreEqual(1, rig.Chats.Count, "she never answers her own words");
        }

        [Test] public void Hypa_Done_AnswersAtOnce_WithoutWaitingForTheQuiet()
        {
            var rig = new Rig(this);
            rig.Say(0.3f, 4);
            Assert.AreEqual(TalkState.Hearing, rig.Talk.State);
            Assert.IsTrue(rig.Talk.CanEndTurn);
            rig.Talk.EndTurn();
            Assert.AreEqual(1, rig.Chats.Count);
            CollectionAssert.AreEqual(new[] { "It is my water pot.", "It keeps the water cool." }, rig.Speech.Played);
        }

        [Test] public void Hypa_WhileHerLinesAudioIsStillComing_ThePlayerIsHeard_AndGoesFirst()
        {
            var rig = new Rig(this, echoCancelled: false);
            rig.Speech.Slow = true;
            rig.Turn();
            Assert.AreEqual(1, rig.Chats.Count, "heard though a reply was waiting for its audio");
            Assert.AreEqual(TalkState.Thinking, rig.Talk.State, "her reply is waiting for its audio");
            rig.Turn();
            Assert.AreEqual(2, rig.Chats.Count, "her unspoken reply doesn't block the microphone");
            rig.Speech.Slow = false;
            rig.Turn();
            Assert.AreEqual(TalkState.Listening, rig.Talk.State, "and once her voice comes, the conversation carries on");
        }

        [Test] public void Hypa_OneFailedRequest_DoesNotEndTheConversation_TwoInARowDo()
        {
            var online = NetworkStatus.DeviceOnline;
            NetworkStatus.DeviceOnline = () => true;
            try
            {
                var rig = new Rig(this);
                var notices = new List<string>();
                rig.Talk.Notice += notices.Add;
                rig.AsrFails = 1;
                rig.Turn();
                Assert.AreEqual(TalkState.Listening, rig.Talk.State, "one hiccup: say it again");
                StringAssert.Contains("Say it again", notices.Last());
                rig.Turn();
                Assert.AreEqual(1, rig.Chats.Count, "and the next turn is answered");

                rig.AsrFails = 2;
                rig.Turn();
                rig.Turn();
                Assert.AreEqual(TalkState.Ended, rig.Talk.State, "failing again: she says she needs the connection");
            }
            finally { NetworkStatus.DeviceOnline = online; }
        }

        [Test] public void Hypa_HoldMutesTheMic()
        {
            var rig = new Rig(this);
            rig.Talk.Hold(true);
            Assert.AreEqual(TalkState.Held, rig.Talk.State);
            rig.Turn();
            Assert.AreEqual(0, rig.Chats.Count, "nothing is heard while an item's story plays");
            rig.Talk.Hold(false);
            Assert.AreEqual(TalkState.Listening, rig.Talk.State);
        }

        [Test] public void Hypa_LeavingDuringGoodbye_DoesNotPlayIt()
        {
            var rig = new Rig(this);
            rig.Speech.Slow = true;
            var ending = rig.Talk.EndAsync("Come back soon.");
            rig.Talk.Dispose();
            ending.GetAwaiter().GetResult();
            Assert.AreEqual(0, rig.Speech.Played.Count);
            Assert.AreEqual(TalkState.Ended, rig.Talk.State);
        }

        [Test] public void Hypa_TheCap_EndsWithHerGoodbye()
        {
            var rig = new Rig(this, null, 2f);
            rig.Say(0.001f, 25);
            Assert.AreEqual(TalkState.Ended, rig.Talk.State);
            Assert.AreEqual("Come back soon.", rig.Speech.Played.Last());
            Assert.IsTrue(rig.Mic.Stopped);
        }

        // ---------- the Live engine ----------

        sealed class FakeLive : ILiveModel
        {
            public readonly List<LiveSetup> Setups = new List<LiveSetup>();
            public readonly List<FakeSession> Sessions = new List<FakeSession>();
            public string RefuseVoice;
            /// <summary>When set, <see cref="ConnectAsync"/> waits for it, so a test can end the talk while it is still connecting.</summary>
            public Task HoldConnect;
            public async Task<ILiveSession> ConnectAsync(LiveSetup setup, CancellationToken ct)
            {
                if (HoldConnect != null) await HoldConnect;
                ct.ThrowIfCancellationRequested();
                Setups.Add(setup);
                if (setup.Voice != null && setup.Voice == RefuseVoice) throw new InvalidOperationException("no such voice");
                var s = new FakeSession();
                Sessions.Add(s);
                return s;
            }
        }

        sealed class FakeSession : ILiveSession
        {
            public readonly Queue<LiveEvent> In = new Queue<LiveEvent>();
            public readonly List<string> Texts = new List<string>();
            public readonly List<(string id, string name, string result)> Tools = new List<(string, string, string)>();
            public int AudioSamples;
            public bool Disposed;
            public bool OwnsAudio => false;
            public bool Open => !Disposed;
            public float VoiceLevel => 0f;
            public float MicLevel => 0f;
            public bool MicDenied => false;
            public void SendAudio(float[] samples, int count) => AudioSamples += count;
            public void SendText(string text) => Texts.Add(text);
            public void SendToolResponse(string id, string name, string result) => Tools.Add((id, name, result));
            public void SetHeld(bool held) { }
            public bool TryDequeue(out LiveEvent e) { if (In.Count > 0) { e = In.Dequeue(); return true; } e = null; return false; }
            public void Dispose() => Disposed = true;
        }

        [Test] public void Live_Greets_StreamsTheMic_RunsHerActions_AndTranscribesBothSides()
        {
            var live = new FakeLive();
            var host = new FakeHost();
            var mic = new FakeMic();
            var lines = new List<TalkLine>();
            var talk = new LiveTalk(Small(En, host), live, "gemini-live-x", "Gacrux", () => mic, null, null, 2.5f);
            talk.LineChanged += l => { if (!lines.Contains(l)) lines.Add(l); };
            talk.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var setup = live.Setups[0];
            Assert.AreEqual("gemini-live-x", setup.Model);
            CollectionAssert.AreEqual(TalkActions.Names, setup.Tools.Select(t => t.Name));
            var s = live.Sessions[0];
            StringAssert.Contains("Greet them", s.Texts[0]);

            mic.Frames.Enqueue(Tone(0.2f));
            talk.Tick(0.1f);
            Assert.AreEqual(Frame, s.AudioSamples);

            s.In.Enqueue(new LiveEvent { Kind = LiveEventKind.InputText, Text = "What is" });
            s.In.Enqueue(new LiveEvent { Kind = LiveEventKind.InputText, Text = " that?" });
            s.In.Enqueue(new LiveEvent { Kind = LiveEventKind.ToolCall, Id = "c1", Name = "show_item", Args = new Dictionary<string, string> { ["id"] = "broom" } });
            s.In.Enqueue(new LiveEvent { Kind = LiveEventKind.OutputText, Text = "My broom." });
            s.In.Enqueue(new LiveEvent { Kind = LiveEventKind.TurnComplete });
            talk.Tick(0.1f);
            CollectionAssert.AreEqual(new[] { "show broom" }, host.Done);
            Assert.AreEqual(("c1", "show_item", "lit"), s.Tools[0]);
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("What is that?", lines[0].Text);
            Assert.IsFalse(lines[0].Hers);
            Assert.AreEqual("My broom.", lines[1].Text);
            Assert.IsTrue(lines[1].Final);
        }

        [Test] public void Live_EndingWhileConnecting_DoesNotOpenTheMicrophone()
        {
            var gate = new TaskCompletionSource<bool>();
            var live = new FakeLive { HoldConnect = gate.Task };
            var opens = 0;
            var talk = new LiveTalk(Small(En), live, "m", "Gacrux", () => { opens++; return new FakeMic(); }, null, null, 2.5f);
            var start = talk.StartAsync(CancellationToken.None);
            talk.EndAsync().GetAwaiter().GetResult();
            gate.SetResult(true);
            start.GetAwaiter().GetResult();
            Assert.AreEqual(0, opens);
            Assert.AreEqual(1, live.Sessions.Count);
            Assert.IsTrue(live.Sessions[0].Disposed);
        }

        [Test] public void Live_ARefusedVoice_FallsBackToAnotherWoman_NotTheDefault()
        {
            var live = new FakeLive { RefuseVoice = "Gacrux" };
            var talk = new LiveTalk(Small(En), live, "m", "Gacrux", () => new FakeMic(), null, null, 2.5f);
            talk.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "Gacrux", LiveTalk.FallbackVoice }, live.Setups.Select(x => x.Voice));
            talk.Dispose();
        }

        [Test] public void Live_ADroppedSession_IsResumedWithItsHandle_ThenFreshWithTheLastTurns()
        {
            var live = new FakeLive();
            var talk = new LiveTalk(Small(En), live, "m", null, () => new FakeMic(), null, null, 2.5f);
            talk.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var first = live.Sessions[0];
            first.In.Enqueue(new LiveEvent { Kind = LiveEventKind.InputText, Text = "I am Ada." });
            first.In.Enqueue(new LiveEvent { Kind = LiveEventKind.Handle, Text = "h-7" });
            first.In.Enqueue(new LiveEvent { Kind = LiveEventKind.GoAway, Value = 5 });
            talk.Tick(0.1f);
            Assert.AreEqual(2, live.Setups.Count);
            Assert.AreEqual("h-7", live.Setups[1].ResumeHandle);
            Assert.IsTrue(first.Disposed);

            talk.Hold(true);
            talk.Hold(false);
            StringAssert.Contains("story has finished", live.Sessions[1].Texts.Last(), "the new session carries on");
            Assert.AreNotEqual(TalkState.Ended, talk.State);
            talk.Dispose();
        }
    }
}
