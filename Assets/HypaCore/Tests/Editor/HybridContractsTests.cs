using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaCore.Tests
{
    public class HybridContractsTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
        {
            var parts = new List<string>();
            await foreach (var part in stream) parts.Add(part);
            return parts;
        }

        [Test] public void Auth_SignsInOnce_AndKeepsTheUidWhenLinking()
        {
            var auth = new FakeAuth();
            var seen = new List<string>(); auth.UidChanged += seen.Add;

            var uid = Run(auth.SignInAnonymouslyAsync());
            Assert.AreEqual(uid, Run(auth.SignInAnonymouslyAsync()));
            Run(auth.LinkGoogleAsync());
            Run(auth.LinkAppleAsync());

            Assert.AreEqual(uid, auth.Uid);
            CollectionAssert.AreEqual(new[] { uid }, seen);
            Assert.AreEqual("fake-app-check-token", Run(auth.AppCheckTokenAsync()));
        }

        [Test] public void Auth_TokensAndLinksNeedASignedInUser()
        {
            var auth = new FakeAuth();
            Assert.Throws<InvalidOperationException>(() => Run(auth.IdTokenAsync()));
            Assert.Throws<InvalidOperationException>(() => Run(auth.LinkGoogleAsync()));
        }

        [Test] public void Progress_LoadsWhatWasSaved_AndReportsChangesFromElsewhere()
        {
            var store = new FakeProgressStore();
            Assert.IsNull(Run(store.LoadAsync()));
            Run(store.SaveAsync(new Progress { storyId = "s1", lineIndex = 4 }));
            Assert.AreEqual(4, Run(store.LoadAsync()).lineIndex);

            Progress other = null; store.ChangedElsewhere += p => other = p;
            store.SimulateChangeElsewhere(new Progress { storyId = "s1", lineIndex = 9 });
            Assert.AreEqual(9, other.lineIndex);
        }

        [Test] public void Progress_SerializesWithTheFirestoreFieldNames()
        {
            var json = JObject.FromObject(new Progress { storyId = "s1", lineId = "l2", lineIndex = 2, language = "yo", place = "camp", updatedAt = 5, device = "quest" });
            CollectionAssert.AreEquivalent(
                new[] { "storyId", "lineId", "lineIndex", "language", "place", "updatedAt", "device" },
                json.Properties().Select(p => p.Name));
        }

        [Test] public void Flags_UseTheContractDefaults_UntilSet()
        {
            var flags = new FakeRemoteFlags();
            Run(flags.FetchAsync());
            Assert.AreEqual("gemini-2.5-flash-lite", flags.Get(RemoteConfigKeys.CloudModel, "x"));
            Assert.AreEqual("true", flags.Get(RemoteConfigKeys.RoutePreferOnDevice, "false"));
            Assert.AreEqual("yo,ha,ig,pcm,sw", flags.Get(RemoteConfigKeys.RouteHypaLangs, ""));
            Assert.AreEqual("fallback", flags.Get(RemoteConfigKeys.LiveModel, "fallback"), "live_model has no default yet");
            flags.Set(RemoteConfigKeys.CloudModel, "gemini-y");
            Assert.AreEqual("gemini-y", flags.Get(RemoteConfigKeys.CloudModel, "x"));
        }

        [Test] public void OnDeviceModel_IsUnavailableUntilTurnedOn()
        {
            var model = new FakeOnDeviceModel();
            Assert.IsFalse(model.Available);
            Assert.AreEqual("unavailable", Run(model.StatusAsync()));
            Assert.Throws<InvalidOperationException>(() => Run(model.GenerateAsync("hi", CancellationToken.None)));

            model.Available = true;
            Assert.AreEqual("available", Run(model.StatusAsync()));
            Assert.AreEqual(model.Reply, Run(model.GenerateAsync("hi", CancellationToken.None)));
        }

        [Test] public void NoOnDeviceModel_IsNeverAvailable_AndIsTheDefault()
        {
            var model = new NoOnDeviceModel();
            Assert.IsFalse(model.Available);
            Assert.AreEqual("unavailable", Run(model.StatusAsync()));
            Assert.Throws<InvalidOperationException>(() => Run(model.GenerateAsync("hi", CancellationToken.None)));
            Assert.Throws<InvalidOperationException>(() => model.StreamAsync("hi", CancellationToken.None));
            Assert.IsInstanceOf<NoOnDeviceModel>(new HybridServices().OnDevice);
        }

        [Test] public void Models_StreamTheSameTextTheyReturn()
        {
            var cloud = new FakeCloudModel { Reply = "one two three" };
            Assert.AreEqual("one two three ", string.Concat(Run(Collect(cloud.StreamAsync("hi", CancellationToken.None)))));
            Assert.AreEqual("one two three", Run(cloud.GenerateAsync("hi", CancellationToken.None)));
        }

        [Test] public void Stream_StopsWhenCancelled()
        {
            var cloud = new FakeCloudModel { Reply = "one two three" };
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() => Run(Collect(cloud.StreamAsync("hi", cts.Token))));
            }
        }

        [Test] public void RouteDecision_ComparesByValue()
        {
            var a = new RouteDecision(Route.OnDevice, "Gemini Nano", "short prompt", 400);
            Assert.AreEqual(a, new RouteDecision(Route.OnDevice, "Gemini Nano", "short prompt", 400));
            Assert.AreNotEqual(a, new RouteDecision(Route.CloudGemini, "Gemini", "long prompt", 1200));
        }

        [Test] public void Services_DefaultToFakes_AndCanBeOverridden()
        {
            try
            {
                HybridServices.Override(null);
                Assert.IsInstanceOf<FakeAuth>(HybridServices.Current.Auth);
                var custom = new HybridServices { Cloud = new FakeCloudModel { Reply = "custom" } };
                HybridServices.Override(custom);
                Assert.AreEqual("custom", Run(HybridServices.Current.Cloud.GenerateAsync("hi", CancellationToken.None)));
            }
            finally { HybridServices.Override(null); }
        }

        [Test] public void ProxyBodies_UseTheContractFieldNames()
        {
            var chat = JObject.FromObject(new ChatRequest { messages = new[] { new ChatMessage { role = "user", content = "hi" } }, model = "auto", maxTokens = 512 });
            Assert.AreEqual("hi", (string)chat["messages"][0]["content"]);
            Assert.AreEqual("auto", (string)chat["model"]);
            Assert.AreEqual("/hypa/chat", ProxyApi.Chat);
            Assert.AreEqual("app_check", ProxyApi.ErrorAppCheck);
            Assert.AreEqual("X-Firebase-AppCheck", ProxyApi.AppCheckHeader);
            var tts = JObject.FromObject(new TtsResponse { audioUrl = "https://example/a", mime = "audio/mpeg", sampleRate = 24000, route = new ProxyRoute { model = "m", voice = "v" } });
            Assert.AreEqual("m", (string)tts["route"]["model"]);
            Assert.AreEqual("v", (string)tts["route"]["voice"]);
            var translate = JObject.FromObject(new TranslateRequest { text = "omi", targetLang = "English", sourceLang = "Yoruba" });
            Assert.AreEqual("English", (string)translate["targetLang"]);
            Assert.AreEqual("600", RemoteConfigKeys.Default(RemoteConfigKeys.ProxyDailySecondsPerUser));
        }
    }
}
