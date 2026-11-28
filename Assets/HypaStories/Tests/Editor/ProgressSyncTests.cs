using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaStories.Tests
{
    /// <summary>HYP-203: saving progress (at most every 5 s), the "Continue where you left off" offer, per-story merges, saved words, pairing codes.</summary>
    public class ProgressSyncTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        sealed class CountingStore : IProgressStore
        {
            public readonly List<Progress> Saved = new List<Progress>();
            public Progress Stored;
            public Exception FailNext;
            public event Action<Progress> ChangedElsewhere;
            public Task<Progress> LoadAsync() => Task.FromResult(Stored);
            public Task SaveAsync(Progress p)
            {
                if (FailNext != null) { var e = FailNext; FailNext = null; throw e; }
                Saved.Add(p); Stored = p;
                return Task.CompletedTask;
            }
            public void Elsewhere(Progress p) => ChangedElsewhere?.Invoke(p);
        }

        /// <summary>Holds the first save until released; records each save when it finishes.</summary>
        sealed class SlowFirstStore : IProgressStore
        {
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public readonly List<Progress> Finished = new List<Progress>();
            public volatile bool Started;
            int _count;
            public event Action<Progress> ChangedElsewhere { add { } remove { } }
            public Task<Progress> LoadAsync() => Task.FromResult<Progress>(null);
            public async Task SaveAsync(Progress p)
            {
                if (Interlocked.Increment(ref _count) == 1) { Started = true; await Release.Task; }
                lock (Finished) Finished.Add(p);
            }
        }

        [Test] public void ASlowOlderSave_FinishesBeforeTheNewerOne_SoTheNewerOneWins()
        {
            long now = 0;
            var store = new SlowFirstStore();
            var sync = new ProgressSync(store, "android", () => now);

            // Off the main thread: a test that blocks Unity's main thread must not wait on work that resumes there.
            sync.Record(At(1));
            var tick = Task.Run(() => sync.TickAsync());   // waits inside the store
            SpinWait.SpinUntil(() => store.Started, 2000);
            now = 1000; sync.Record(At(2));
            var flush = Task.Run(() => sync.FlushAsync()); // must wait for the first save
            Thread.Sleep(50);
            store.Release.SetResult(true);
            Run(Task.WhenAll(tick, flush));

            Assert.AreEqual(new[] { 1, 2 }, store.Finished.Select(p => p.lineIndex).ToArray());
            Assert.AreEqual(2, sync.LastSaved.lineIndex);
        }

        [Test] public void AfterTwoSavesOverlap_LaterSavesStillGoOut()
        {
            long now = 0;
            var store = new SlowFirstStore();
            var sync = new ProgressSync(store, "web", () => now);

            sync.Record(At(1));
            var tick = Task.Run(() => sync.TickAsync());
            SpinWait.SpinUntil(() => store.Started, 2000);
            now = 1000; sync.Record(At(2));
            var flush = Task.Run(() => sync.FlushAsync());
            Thread.Sleep(50);
            store.Release.SetResult(true);
            Run(Task.WhenAll(tick, flush));

            now = 10000; sync.Record(At(3));
            Run(sync.TickAsync());
            Assert.AreEqual(new[] { 1, 2, 3 }, store.Finished.Select(p => p.lineIndex).ToArray());
        }

        [Test] public void NoOffer_ForAnOlderPositionElsewhere_WhileANewerOneHereWaitsToBeSaved()
        {
            long now = 0;
            var store = new CountingStore();
            var sync = new ProgressSync(store, "quest", () => now);
            ContinueOffer offered = null;
            sync.ContinueOffered += o => offered = o;
            sync.Attach();

            sync.Record(At(1)); Run(sync.TickAsync());       // saved at 0
            now = 2000; sync.Record(At(2)); Run(sync.TickAsync()); // waiting: too soon to save

            store.Elsewhere(new Progress { storyId = "seven-hot-stones", lineIndex = 1, device = "android", updatedAt = 1500 });

            Assert.IsNull(offered, "the phone's position is older than where this device is now");
        }

        static Progress At(int line, string story = "seven-hot-stones", string place = "camp")
            => new Progress { storyId = story, lineId = "l" + line, lineIndex = line, language = "yo", place = place };

        // ---- saving ----

        [Test] public void TheFirstPosition_IsSavedAtOnce_AndStampedWithTheDeviceAndTime()
        {
            long now = 1000;
            var store = new CountingStore();
            var sync = new ProgressSync(store, "quest", () => now);

            sync.Record(At(1));
            Run(sync.TickAsync());

            Assert.AreEqual(1, store.Saved.Count);
            Assert.AreEqual("quest", store.Saved[0].device);
            Assert.AreEqual(1000, store.Saved[0].updatedAt);
            Assert.AreSame(store.Saved[0], sync.LastSaved);
        }

        [Test] public void AtMostOneSaveEveryFiveSeconds_AndTheLatestPositionWins()
        {
            long now = 0;
            var store = new CountingStore();
            var sync = new ProgressSync(store, "web", () => now);

            sync.Record(At(1)); Run(sync.TickAsync());               // saved
            now = 1000; sync.Record(At(2)); Run(sync.TickAsync());   // too soon
            now = 4000; sync.Record(At(3)); Run(sync.TickAsync());   // still too soon
            Assert.AreEqual(1, store.Saved.Count);

            now = 5000; Run(sync.TickAsync());                       // the interval is up: the latest goes
            Assert.AreEqual(2, store.Saved.Count);
            Assert.AreEqual(3, store.Saved[1].lineIndex, "line 2 was skipped; only the latest is sent");

            now = 5500; Run(sync.TickAsync());
            Assert.AreEqual(2, store.Saved.Count, "nothing new to send");
        }

        [Test] public void ClosingTheApp_SavesAtOnce_EvenInsideTheInterval()
        {
            long now = 0;
            var store = new CountingStore();
            var sync = new ProgressSync(store, "android", () => now);
            sync.Record(At(1)); Run(sync.TickAsync());
            now = 1000; sync.Record(At(9));

            Run(sync.FlushAsync());

            Assert.AreEqual(2, store.Saved.Count);
            Assert.AreEqual(9, store.Saved[1].lineIndex);
        }

        [Test] public void AFailedSave_KeepsThePosition_AndTriesAgainLater()
        {
            long now = 0;
            var store = new CountingStore { FailNext = new InvalidOperationException("no network") };
            var sync = new ProgressSync(store, "ios", () => now);
            sync.Record(At(4));

            Assert.DoesNotThrow(() => Run(sync.TickAsync()));
            Assert.AreEqual(0, store.Saved.Count);
            Assert.IsNull(sync.LastSaved);

            now = 5000; Run(sync.TickAsync());
            Assert.AreEqual(1, store.Saved.Count);
            Assert.AreEqual(4, store.Saved[0].lineIndex);
        }

        // ---- the offer ----

        [Test] public void NewerProgressFromAnotherDevice_IsOffered()
        {
            var saved = new Progress { storyId = "s", device = "android", updatedAt = 500 };
            var mine = new Progress { storyId = "s", device = "web", updatedAt = 100 };
            var offer = ProgressSync.Decide(saved, mine, "web");
            Assert.IsNotNull(offer);
            Assert.AreSame(saved, offer.Saved);
        }

        [Test] public void NoOffer_ForYourOwnSave_AnOlderOne_OrNoProgress()
        {
            Assert.IsNull(ProgressSync.Decide(new Progress { storyId = "s", device = "web", updatedAt = 900 }, null, "web"), "saved by this device");
            Assert.IsNull(ProgressSync.Decide(new Progress { storyId = "s", device = "android", updatedAt = 50 }, new Progress { updatedAt = 100 }, "web"), "older than this device's");
            Assert.IsNull(ProgressSync.Decide(new Progress { storyId = "s", device = "android", updatedAt = 100 }, new Progress { updatedAt = 100 }, "web"), "same time");
            Assert.IsNull(ProgressSync.Decide(null, null, "web"));
            Assert.IsNull(ProgressSync.Decide(new Progress { device = "android", updatedAt = 900 }, null, "web"), "no story to continue");
        }

        [Test] public void OnLaunch_TheOfferComesFromTheServersProgress()
        {
            var store = new CountingStore { Stored = new Progress { storyId = "seven-hot-stones", lineIndex = 12, device = "android", updatedAt = 900 } };
            var sync = new ProgressSync(store, "quest", () => 1000);

            var offer = Run(sync.CheckAsync());

            Assert.AreEqual(12, offer.Saved.lineIndex);
        }

        [Test] public void AfterRelaunch_AnOlderUpdateFromAnotherDevice_IsNotOffered()
        {
            var store = new CountingStore { Stored = new Progress { storyId = "s", lineIndex = 8, device = "quest", updatedAt = 5000 } };
            var sync = new ProgressSync(store, "quest", () => 6000);
            ContinueOffer offered = null;
            sync.ContinueOffered += o => offered = o;

            Assert.IsNull(Run(sync.CheckAsync()));
            Assert.AreEqual(5000, sync.LastSaved.updatedAt);

            sync.Attach();
            store.Elsewhere(new Progress { storyId = "s", lineIndex = 1, device = "android", updatedAt = 1000 });
            Assert.IsNull(offered, "older than the position loaded for this device");

            store.Elsewhere(new Progress { storyId = "s", lineIndex = 20, device = "android", updatedAt = 7000 });
            Assert.AreEqual(20, offered.Saved.lineIndex);
        }

        [Test] public void WhenProgressChangesElsewhere_TheOfferIsRaised_OnlyWhileAttached()
        {
            var store = new CountingStore();
            var sync = new ProgressSync(store, "web", () => 1000);
            var offers = new List<ContinueOffer>(); sync.ContinueOffered += offers.Add;

            sync.Attach();
            store.Elsewhere(new Progress { storyId = "s", device = "quest", updatedAt = 5 });
            store.Elsewhere(new Progress { storyId = "s", device = "web", updatedAt = 6 }); // our own device echoing back
            sync.Detach();
            store.Elsewhere(new Progress { storyId = "s", device = "quest", updatedAt = 7 });

            Assert.AreEqual(1, offers.Count);
            Assert.AreEqual(5, offers[0].Saved.updatedAt);
        }

        [Test] public void TwoBrowsers_AreTwoDevices()
        {
            var laptop = ProgressSync.DeviceId("web", "k3j9x2");
            var saved = new Progress { storyId = "s", device = ProgressSync.DeviceId("phone", "q7m2zp"), updatedAt = 500 };
            Assert.IsNotNull(ProgressSync.Decide(saved, null, laptop), "another browser's save");
            saved.device = laptop;
            Assert.IsNull(ProgressSync.Decide(saved, null, laptop), "this browser's own save");
        }

        [TestCase("android", "your phone"), TestCase("ios", "your phone"), TestCase("quest", "your Quest"), TestCase("web", "the web"), TestCase("toaster", "another device"), TestCase(null, "another device"),
         TestCase("web:k3j9x2", "the web"), TestCase("phone:q7m2zp", "your phone")]
        public void TheOfferNamesTheDevice(string device, string expected)
            => Assert.AreEqual(expected, ContinueOffer.Where(device));

        [Test] public void TheOfferReadsLikeTheTicket()
            => Assert.AreEqual("Continue Seven Hot Stones where you left off on your phone?",
                new ContinueOffer(new Progress { device = "android" }).Text("Seven Hot Stones"));

        // ---- per-story results and saved words ----

        [Test] public void StoryResults_Merge_BestScoreIsTheHigher_FinishedStaysFinished()
        {
            var phone = new StoryResult { storyId = "s", finished = true, bestGameScore = 40, gamePlays = 2, lastLineId = "l9", updatedAt = 100 };
            var quest = new StoryResult { storyId = "s", finished = false, bestGameScore = 70, gamePlays = 1, lastLineId = "l3", updatedAt = 200 };

            var merged = StoryResult.Merge(phone, quest);

            Assert.IsTrue(merged.finished);
            Assert.AreEqual(70, merged.bestGameScore);
            Assert.AreEqual(2, merged.gamePlays);
            Assert.AreEqual("l3", merged.lastLineId, "from the later update");
            Assert.AreEqual(200, merged.updatedAt);
            Assert.AreEqual(merged.bestGameScore, StoryResult.Merge(quest, phone).bestGameScore, "order does not matter");
            Assert.AreSame(phone, StoryResult.Merge(phone, null));
            Assert.AreSame(quest, StoryResult.Merge(null, quest));
        }

        [Test] public void RecordingAStory_ReadsMergesAndSaves_SoALowerScoreNeverReplacesAHigherOne()
        {
            long now = 100;
            var store = new FakeUserDataStore();
            var sync = new UserDataSync(store, () => now);

            Run(sync.RecordStoryAsync("s", finished: false, gameScore: 80, lastLineId: "l5", playedGame: true));
            now = 200;
            var after = Run(sync.RecordStoryAsync("s", finished: true, gameScore: 30, lastLineId: "l9", playedGame: true));

            Assert.AreEqual(80, after.bestGameScore);
            Assert.IsTrue(after.finished);
            Assert.AreEqual(2, after.gamePlays);
            Assert.AreEqual("l9", after.lastLineId);
            Assert.AreEqual(80, Run(store.GetStoryAsync("s")).bestGameScore);
        }

        [Test] public void SavedWords_SameWordSameLanguage_IsOneWord_AndWordsFromTwoDevicesAreAllKept()
        {
            long now = 10;
            var store = new FakeUserDataStore();
            var sync = new UserDataSync(store, () => now);

            Run(sync.SaveWordAsync("Omi", "water", "yo", "s"));
            now = 20; Run(sync.SaveWordAsync("  omi ", "water", "yo", "s"));
            Run(sync.SaveWordAsync("omi", "mother", "ha", "s"));

            var words = Run(sync.WordsAsync());
            Assert.AreEqual(2, words.Count, "yo and ha are different words");

            now = 5;
            Run(sync.SaveWordAsync("omi", "agua", "yo", "other"));
            var kept = Run(sync.WordsAsync()).First(w => w.language == "yo");
            Assert.AreEqual("water", kept.translation, "an older save does not replace the later one");
            Assert.AreEqual(20, kept.savedAt);

            var merged = SavedWord.Merge(new[] { new SavedWord { text = "ìyá", language = "yo", savedAt = 1 } }, new[] { new SavedWord { text = "Ìyá", language = "yo", savedAt = 9 }, new SavedWord { text = "baba", language = "yo", savedAt = 5 } });
            Assert.AreEqual(new[] { "baba", "Ìyá" }, merged.Select(w => w.text).ToArray(), "one ìyá (the later save), plus baba, in the order saved");
        }

        [Test] public void AWordId_IsSafeAsADocumentId()
        {
            var id = SavedWord.IdFor("ìyá/ọmọ", "yo");
            Assert.IsFalse(id.Contains("/"));
            Assert.AreEqual(id, SavedWord.IdFor(" ÌYÁ/ỌMỌ ", "YO"));
        }

        // ---- the pairing code ----

        [Test] public void APairingCode_IsSixConsonants_AndNeverSpellsAWord()
        {
            var rng = new Random(7);
            for (var i = 0; i < 200; i++)
            {
                var code = PairingCode.Generate(rng.Next);
                Assert.AreEqual(6, code.Length);
                Assert.IsTrue(PairingCode.IsValid(code), code);
                Assert.IsFalse(code.Any(c => "AEIOUY".IndexOf(c) >= 0), code);
            }
        }

        [Test] public void WhatAPersonTypes_IsTidied_BeforeItIsChecked()
        {
            Assert.AreEqual("BCDFGH", PairingCode.Normalize(" bcd-fgh "));
            Assert.IsTrue(PairingCode.IsValid("bcd fgh"));
            Assert.IsFalse(PairingCode.IsValid("BCDFG"), "too short");
            Assert.IsFalse(PairingCode.IsValid("BCDFGHJ"), "too long");
            Assert.IsFalse(PairingCode.IsValid("BCDFGA"), "a vowel");
            Assert.IsFalse(PairingCode.IsValid(null));
        }
    }
}
