using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaCore.Hybrid.Web;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaCore.Tests
{
    /// <summary>The C# half of the WebGL Firebase bridge (G03), against a scripted JS side.</summary>
    public class WebBridgeTests
    {
        /// <summary>Stands in for HypaFirebase.jslib: records calls and answers them from <see cref="Reply"/>.</summary>
        sealed class FakeJs
        {
            public readonly List<(int id, string method, JToken args)> Calls = new List<(int, string, JToken)>();
            public readonly List<int> Cancelled = new List<int>();
            /// <summary>Messages to send back at once for (method, args); none leaves the call open.</summary>
            public Func<string, JToken, JObject[]> Reply = (m, a) => Array.Empty<JObject>();

            public FakeJs()
            {
                WebBridge.Send = (id, method, args) =>
                {
                    var parsed = JToken.Parse(args);
                    Calls.Add((id, method, parsed));
                    foreach (var msg in Reply(method, parsed)) { msg["id"] = id; WebBridge.Receive(msg.ToString()); }
                };
                WebBridge.Cancel = Cancelled.Add;
            }

            public (int id, string method, JToken args) Last => Calls[Calls.Count - 1];
        }

        static JObject Ok(JToken value = null) => new JObject { ["kind"] = "ok", ["value"] = value };
        static JObject Chunk(JToken value) => new JObject { ["kind"] = "chunk", ["value"] = value };
        static JObject Err(string code) => new JObject { ["kind"] = "err", ["code"] = code, ["message"] = "failed" };
        static void Event(string name, JToken value) => WebBridge.Receive(new JObject { ["id"] = 0, ["kind"] = "event", ["name"] = name, ["value"] = value }.ToString());

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
        {
            var parts = new List<string>();
            await foreach (var part in stream) parts.Add(part);
            return parts;
        }

        SynchronizationContext _context;

        // Unity's EditMode context resumes awaits on the main thread, which these blocking tests hold. Without it they resume
        // on the pool. In a WebGL build the bridge is awaited, never blocked on.
        [SetUp] public void SetUp() { _context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() { WebBridge.ResetForTests(); SynchronizationContext.SetSynchronizationContext(_context); }

        [Test] public void Call_ResolvesWithTheAnswerToItsOwnId()
        {
            var js = new FakeJs();
            var first = WebBridge.CallAsync("signIn");
            var second = WebBridge.CallAsync("idToken");
            WebBridge.Receive(new JObject { ["id"] = js.Calls[1].id, ["kind"] = "ok", ["value"] = "token" }.ToString());
            WebBridge.Receive(new JObject { ["id"] = js.Calls[0].id, ["kind"] = "ok", ["value"] = "uid-1" }.ToString());

            Assert.AreEqual("uid-1", (string)Run(first));
            Assert.AreEqual("token", (string)Run(second));
            Assert.AreNotEqual(js.Calls[0].id, js.Calls[1].id);
        }

        [Test] public void Call_RaisesTheJsErrorCode()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Err("auth/network-request-failed") } };
            var e = Assert.Throws<WebBridgeException>(() => Run(WebBridge.CallAsync("signIn")));
            Assert.AreEqual("auth/network-request-failed", e.Code);
        }

        [Test] public void Call_OutsideABrowser_FailsInsteadOfHanging()
        {
            Assert.Throws<PlatformNotSupportedException>(() => Run(WebBridge.CallAsync("signIn")));
        }

        [Test] public void Call_Cancelled_TellsJs_AndIgnoresTheLateAnswer()
        {
            var js = new FakeJs();
            var cts = new CancellationTokenSource();
            var call = WebBridge.CallAsync("generate", null, cts.Token);
            cts.Cancel();

            Assert.Catch<OperationCanceledException>(() => Run(call));
            CollectionAssert.AreEqual(new[] { js.Last.id }, js.Cancelled);
            Assert.DoesNotThrow(() => WebBridge.Receive(new JObject { ["id"] = js.Last.id, ["kind"] = "ok", ["value"] = "late" }.ToString()));
        }

        [Test] public void Receive_IgnoresUnknownIdsAndUnreadableMessages()
        {
            Assert.DoesNotThrow(() => WebBridge.Receive("{\"id\":99,\"kind\":\"ok\",\"value\":1}"));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, "Hypa: unreadable message from the Firebase bridge.");
            Assert.DoesNotThrow(() => WebBridge.Receive("not json"));
        }

        [Test] public void Stream_YieldsChunksInOrder_ThenEnds()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Chunk("Once "), Chunk("upon "), Chunk("a time"), Ok() } };
            var parts = Run(Collect(new WebCloudModel(new FakeRemoteFlags()).StreamAsync("Tell me", CancellationToken.None)));
            CollectionAssert.AreEqual(new[] { "Once ", "upon ", "a time" }, parts);
            Assert.IsEmpty(js.Cancelled);
        }

        [Test] public void Stream_ErrorAfterChunks_Throws()
        {
            new FakeJs { Reply = (m, a) => new[] { Chunk("Once "), Err("unavailable") } };
            var e = Assert.Throws<WebBridgeException>(() => Run(Collect(new ChromeNanoModel().StreamAsync("Tell me", CancellationToken.None))));
            Assert.AreEqual("unavailable", e.Code);
        }

        [Test] public void Stream_StoppedEarly_TellsJsToStop()
        {
            var js = new FakeJs { Reply = (m, a) => m == "stream" ? new[] { Chunk("one "), Chunk("two ") } : new[] { Ok("available") } };
            async Task<string> FirstOnly()
            {
                await foreach (var part in new ChromeNanoModel().StreamAsync("Tell me", CancellationToken.None)) return part;
                return null;
            }
            Assert.AreEqual("one ", Run(FirstOnly()));
            CollectionAssert.AreEqual(new[] { js.Last.id }, js.Cancelled);
        }

        [Test] public void Config_IsNullUntilTheRequiredFieldsAreSet()
        {
            Assert.IsNull(WebServices.Config(null));
            Assert.IsNull(WebServices.Config(JObject.Parse("{\"firebaseWeb\":{\"apiKey\":\"\",\"projectId\":\"p\",\"appId\":\"a\"}}")));

            var config = WebServices.Config(JObject.Parse(
                "{\"firebaseWeb\":{\"apiKey\":\" k \",\"projectId\":\"p\",\"appId\":\"a\",\"databaseURL\":\"\",\"recaptchaEnterpriseSiteKey\":\"site\"}}"));
            Assert.AreEqual("k", (string)config["firebase"]["apiKey"]);
            Assert.IsNull(config["firebase"]["databaseURL"], "blank fields are left out");
            Assert.IsNull(config["firebase"]["recaptchaEnterpriseSiteKey"], "the site key is not Firebase app config");
            Assert.AreEqual("site", (string)config["recaptchaEnterpriseSiteKey"]);
        }

        [Test] public void Emulators_OnLocalhostUnlessTheUrlAsksForLive()
        {
            Assert.IsTrue(WebServices.UsesEmulators("http://localhost:5000/"));
            Assert.IsTrue(WebServices.UsesEmulators("http://127.0.0.1:5000/index.html?debug=1"));
            Assert.IsFalse(WebServices.UsesEmulators("http://localhost:5000/?firebase=live"));
            Assert.IsFalse(WebServices.UsesEmulators("https://hypa-firebase.web.app/"));
            Assert.IsFalse(WebServices.UsesEmulators(""), "the editor has no page URL");
            Assert.IsTrue(WebServices.UsesEmulators("http://192.168.1.20:5002/"), "a phone on the same Wi-Fi");
            Assert.IsTrue(WebServices.UsesEmulators("http://172.20.10.6:5002/circle/BCDFG"));
            Assert.IsTrue(WebServices.UsesEmulators("http://10.0.0.5:5002/"));
            Assert.IsTrue(WebServices.UsesEmulators("http://macbook.local:5002/"));
            Assert.IsFalse(WebServices.UsesEmulators("http://192.168.1.20:5002/?firebase=live"));
            Assert.IsFalse(WebServices.UsesEmulators("http://172.32.0.1:5002/"), "not a private address");
            Assert.IsFalse(WebServices.UsesEmulators("http://8.8.8.8/"));

            var config = WebServices.EmulatorConfig();
            Assert.AreEqual(WebServices.EmulatorProject, (string)config["firebase"]["projectId"]);
            StringAssert.StartsWith("demo-", WebServices.EmulatorProject, "a demo- project never reaches real Firebase");
            Assert.IsTrue((bool)config["emulators"]);
        }

        [Test] public void Auth_FollowsTheUidFromJs_AndRaisesOncePerChange()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Ok("uid-1") } };
            var auth = new WebAuth();
            var seen = new List<string>(); auth.UidChanged += seen.Add;

            Assert.AreEqual("uid-1", Run(auth.SignInAnonymouslyAsync()));
            Event("uid", "uid-1");
            Event("uid", null);

            Assert.IsNull(auth.Uid);
            CollectionAssert.AreEqual(new[] { "uid-1", null }, seen);
        }

        [Test] public void Auth_LinksByProviderId()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Ok() } };
            var auth = new WebAuth();
            Run(auth.LinkGoogleAsync());
            Assert.AreEqual("google.com", (string)js.Last.args["provider"]);
            Run(auth.LinkAppleAsync());
            Assert.AreEqual("apple.com", (string)js.Last.args["provider"]);
        }

        [Test] public void Progress_UsesTheFirestoreFieldNames_AndReportsChangesFromElsewhere()
        {
            var saved = new Progress { storyId = "s1", lineId = "l2", lineIndex = 2, language = "yo", place = "camp", updatedAt = 1700000000000, device = "web" };
            var js = new FakeJs { Reply = (m, a) => new[] { Ok(m == "loadProgress" ? JObject.FromObject(saved) : null) } };
            var store = new WebProgressStore();

            Run(store.SaveAsync(saved));
            Assert.AreEqual("saveProgress", js.Last.method);
            Assert.AreEqual(1700000000000, (long)js.Last.args["updatedAt"]);
            Assert.AreEqual("camp", (string)js.Last.args["place"]);

            var loaded = Run(store.LoadAsync());
            Assert.AreEqual("l2", loaded.lineId);
            Assert.AreEqual(1700000000000, loaded.updatedAt);

            Progress other = null; store.ChangedElsewhere += p => other = p;
            Event("progress", new JObject { ["storyId"] = "s1", ["lineIndex"] = 9, ["updatedAt"] = 1700000000001 });
            Assert.AreEqual(9, other.lineIndex);
        }

        [Test] public void Progress_NothingSavedYet_LoadsNull()
        {
            new FakeJs { Reply = (m, a) => new[] { Ok() } };
            Assert.IsNull(Run(new WebProgressStore().LoadAsync()));
        }

        [Test] public void Flags_FetchedValuesWin_DefaultsOtherwise_AndAFailedFetchDoesNotThrow()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Ok(new JObject { [RemoteConfigKeys.CloudModel] = "gemini-x" }) } };
            var flags = new WebRemoteFlags();
            Run(flags.FetchAsync());
            Assert.AreEqual("gemini-x", flags.Get(RemoteConfigKeys.CloudModel, "fallback"));
            Assert.AreEqual(RemoteConfigKeys.Default(RemoteConfigKeys.ProxyDailySecondsPerUser), flags.Get(RemoteConfigKeys.ProxyDailySecondsPerUser, "fallback"));
            Assert.AreEqual("fallback", flags.Get("unknown", "fallback"));

            js.Reply = (m, a) => new[] { Err("remoteconfig/fetch-throttle") };
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, "Hypa: Remote Config not fetched (remoteconfig/fetch-throttle); using defaults.");
            Run(flags.FetchAsync());
            Assert.AreEqual("gemini-x", flags.Get(RemoteConfigKeys.CloudModel, "fallback"), "the last fetched values stay");
        }

        [Test] public void Cloud_AsksForTheRemoteConfigModel()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Ok("Hello") } };
            var cloud = new WebCloudModel(new FakeRemoteFlags().Set(RemoteConfigKeys.CloudModel, "gemini-y"));
            Assert.AreEqual("Hello", Run(cloud.GenerateAsync("Hi", CancellationToken.None)));
            Assert.AreEqual("cloud", (string)js.Last.args["side"]);
            Assert.AreEqual("gemini-y", (string)js.Last.args["model"]);

            Run(new WebCloudModel(new FakeRemoteFlags()).GenerateAsync("Hi", CancellationToken.None));
            Assert.AreEqual(RemoteConfigKeys.Default(RemoteConfigKeys.CloudModel), (string)js.Last.args["model"]);
        }

        [Test] public void Nano_ReportsChromeStatus_AndUnknownOrFailedAsUnavailable()
        {
            var js = new FakeJs { Reply = (m, a) => new[] { Ok("downloadable") } };
            var nano = new ChromeNanoModel();
            Assert.AreEqual("downloadable", nano.Status);
            Assert.IsFalse(nano.Available);

            js.Reply = (m, a) => new[] { Ok("available") };
            Assert.AreEqual("available", Run(nano.StatusAsync()));
            Assert.IsTrue(nano.Available);

            js.Reply = (m, a) => new[] { Ok("something-new") };
            Assert.AreEqual("unavailable", Run(nano.StatusAsync()));

            js.Reply = (m, a) => new[] { Err("bridge") };
            Assert.AreEqual("unavailable", Run(nano.StatusAsync()));
        }

        [Test] public void Nano_AsksForTheDevice_AndDownloadReportsProgress()
        {
            var js = new FakeJs
            {
                Reply = (m, a) => m == "nanoDownload" ? new[] { Chunk(0.25), Chunk(1.0), Ok("available") }
                    : m == "nanoStatus" ? new[] { Ok("available") } : new[] { Ok("Bonjour") },
            };
            var nano = new ChromeNanoModel();
            Assert.AreEqual("Bonjour", Run(nano.GenerateAsync("Hi", CancellationToken.None)));
            Assert.AreEqual("device", (string)js.Last.args["side"]);

            var progress = new List<float>(); nano.Changed += () => progress.Add(nano.DownloadProgress);
            Run(nano.DownloadAsync());
            CollectionAssert.AreEqual(new[] { 0.25f, 1f }, progress.GetRange(0, 2));
            Assert.IsTrue(nano.Available);
        }
    }
}
