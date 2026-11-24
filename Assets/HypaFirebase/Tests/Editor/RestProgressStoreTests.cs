using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestProgressStoreTests
    {
        const string DocumentUrl = "https://firestore.googleapis.com/v1/projects/proj/databases/(default)/documents/users/fake-uid/progress/current";

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static Progress Sample(int line = 3, long at = 100)
            => new Progress { storyId = "s1", lineId = "l" + line, lineIndex = line, language = "yo", place = "camp", updatedAt = at, device = "quest" };

        static (RestProgressStore Store, StubHandler Http, MemoryKeyValueStore Local) Make(params System.Action<StubHandler>[] script)
        {
            var http = new StubHandler();
            foreach (var step in script) step(http);
            var auth = new FakeAuth();
            Run(auth.SignInAnonymouslyAsync());
            var local = new MemoryKeyValueStore();
            return (new RestProgressStore(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example"), auth, local, http), http, local);
        }

        [Test] public void Save_StoresLocally_AndSendsTheFirestoreDocument()
        {
            var (store, http, local) = Make(h => h.Reply("{}"));

            Run(store.SaveAsync(Sample()));

            var sent = http.Requests[0];
            Assert.AreEqual("PATCH", sent.Method);
            Assert.AreEqual(DocumentUrl, sent.Url);
            Assert.AreEqual("Bearer fake-id-token", sent.Headers["Authorization"]);
            Assert.AreEqual("fake-app-check-token", sent.Headers["X-Firebase-AppCheck"]);
            var fields = JObject.Parse(sent.Body)["fields"];
            Assert.AreEqual("s1", (string)fields["storyId"]["stringValue"]);
            Assert.AreEqual("3", (string)fields["lineIndex"]["integerValue"], "Firestore REST sends integers as strings");
            Assert.AreEqual("100", (string)fields["updatedAt"]["integerValue"]);
            Assert.IsNull(local.Get(RestProgressStore.DirtyKey), "sent, so nothing is waiting");
            Assert.IsNotNull(local.Get(RestProgressStore.LocalKey));
        }

        [Test] public void Save_OffLine_DoesNotFail_AndKeepsTheSaveQueued()
        {
            var (store, http, local) = Make(h => h.Fail(new HttpRequestException("no network")), h => h.Fail(new HttpRequestException("still no network")));

            Assert.DoesNotThrow(() => Run(store.SaveAsync(Sample(5))));

            Assert.AreEqual("1", local.Get(RestProgressStore.DirtyKey));
            Assert.AreEqual(5, Run(store.LoadAsync()).lineIndex, "still offline: the queued save is what the player sees");
        }

        [Test] public void Load_WhileASaveIsQueued_SendsItFirst_ThenReturnsIt()
        {
            // save fails; the load's retry succeeds; then the load reads the server (nothing there yet).
            var (store, http, local) = Make(h => h.Fail(new HttpRequestException("no network")), h => h.Reply("{}"), h => h.Reply("{}", HttpStatusCode.NotFound));
            Run(store.SaveAsync(Sample(7)));

            var loaded = Run(store.LoadAsync());

            Assert.AreEqual(7, loaded.lineIndex);
            Assert.AreEqual(new[] { "PATCH", "PATCH", "GET" }, http.Requests.ConvertAll(r => r.Method).ToArray(), "the retry went out before the read");
            Assert.IsNull(local.Get(RestProgressStore.DirtyKey));
        }

        [Test] public void Load_ReadsTheServersCopy_AndKeepsItLocally()
        {
            var body = "{\"fields\":{\"storyId\":{\"stringValue\":\"s9\"},\"lineIndex\":{\"integerValue\":\"12\"},\"language\":{\"stringValue\":\"ha\"},\"updatedAt\":{\"integerValue\":\"555\"}}}";
            var (store, http, local) = Make(h => h.Reply(body));

            var loaded = Run(store.LoadAsync());

            Assert.AreEqual("GET", http.Requests[0].Method);
            Assert.AreEqual("s9", loaded.storyId);
            Assert.AreEqual(12, loaded.lineIndex);
            Assert.AreEqual(555, loaded.updatedAt);
            Assert.IsNotNull(local.Get(RestProgressStore.LocalKey));
        }

        [Test] public void Load_WithNothingOnTheServer_ReturnsNull()
        {
            var (store, _, _) = Make(h => h.Reply("{}", HttpStatusCode.NotFound));
            Assert.IsNull(Run(store.LoadAsync()));
        }

        [Test] public void Load_OffLine_FallsBackToTheLocalCopy()
        {
            var (store, _, local) = Make(h => h.Fail(new HttpRequestException("no network")));
            local.Set(RestProgressStore.LocalKey, Newtonsoft.Json.JsonConvert.SerializeObject(Sample(4)));

            Assert.AreEqual(4, Run(store.LoadAsync()).lineIndex);
        }

        /// <summary>Holds the first request until released, and records each request's body when its reply goes out.</summary>
        sealed class HoldFirstHandler : HttpMessageHandler
        {
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();
            public readonly List<string> Landed = new List<string>();
            int _count;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                if (Interlocked.Increment(ref _count) == 1) await Release.Task;
                lock (Landed) Landed.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        }

        [Test] public void Save_WhileAnOlderSaveIsSending_TheNewerOneLandsLast_AndNothingStaysQueued()
        {
            var http = new HoldFirstHandler();
            var auth = new FakeAuth();
            Run(auth.SignInAnonymouslyAsync());
            var local = new MemoryKeyValueStore();
            var store = new RestProgressStore(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example"), auth, local, http);

            var older = Task.Run(() => store.SaveAsync(Sample(1, 100)));
            SpinWait.SpinUntil(() => local.Get(RestProgressStore.DirtyKey) != null && !older.IsCompleted, 2000);
            Thread.Sleep(50); // let the older save reach the network and wait there
            var newer = Task.Run(() => store.SaveAsync(Sample(2, 200)));
            Thread.Sleep(50);
            http.Release.SetResult(true);
            Run(Task.WhenAll(older, newer));

            var last = JObject.Parse(http.Landed[http.Landed.Count - 1])["fields"];
            Assert.AreEqual("2", (string)last["lineIndex"]["integerValue"], "the server ends with the newer save");
            Assert.IsNull(local.Get(RestProgressStore.DirtyKey));
        }

        [Test] public void Save_RefusedByFirestore_StaysQueued()
        {
            var (store, _, local) = Make(h => h.Reply("{\"error\":{\"message\":\"PERMISSION_DENIED\"}}", HttpStatusCode.Forbidden));
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("Firestore answered 403"));

            Assert.DoesNotThrow(() => Run(store.SaveAsync(Sample())));

            Assert.AreEqual("1", local.Get(RestProgressStore.DirtyKey));
        }
    }
}
