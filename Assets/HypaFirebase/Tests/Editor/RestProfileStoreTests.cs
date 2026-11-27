using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestProfileStoreTests
    {
        const string DocumentUrl = "https://firestore.googleapis.com/v1/projects/proj/databases/(default)/documents/users/fake-uid";

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static UserProfile Sample(long at = 100) => new UserProfile
        {
            username = "ade", displayName = "Ade", appearance = "{\"skin\":3}", appearanceVersion = 2, createdAt = 50, lastDevice = "quest", lastSeenAt = at,
        };

        static (RestProfileStore Store, StubHandler Http, MemoryKeyValueStore Local) Make(params System.Action<StubHandler>[] script)
        {
            var http = new StubHandler();
            foreach (var step in script) step(http);
            var auth = new FakeAuth();
            Run(auth.SignInAnonymouslyAsync());
            var local = new MemoryKeyValueStore();
            return (new RestProfileStore(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example"), auth, local, http), http, local);
        }

        [Test] public void Save_SendsTheUsersDocument_InTheRulesShape()
        {
            var (store, http, local) = Make(h => h.Reply("{}"));

            Run(store.SaveAsync(Sample()));

            var sent = http.Requests[0];
            Assert.AreEqual("PATCH", sent.Method);
            Assert.AreEqual(DocumentUrl, sent.Url);
            Assert.AreEqual("Bearer fake-id-token", sent.Headers["Authorization"]);
            var fields = (JObject)JObject.Parse(sent.Body)["fields"];
            Assert.AreEqual("ade", (string)fields["username"]["stringValue"]);
            Assert.AreEqual("2", (string)fields["appearanceVersion"]["integerValue"]);
            Assert.AreEqual("100", (string)fields["lastSeenAt"]["integerValue"]);
            Assert.IsNull(fields["language"], "empty fields are left out");
            Assert.IsNull(local.Get(RestProfileStore.DirtyKey));
        }

        [Test] public void Offline_TheSaveWaits_AndIsWhatLoadReturns()
        {
            var (store, _, local) = Make(h => h.Fail(new HttpRequestException("no network")), h => h.Fail(new HttpRequestException("still none")));

            Run(store.SaveAsync(Sample(7)));

            Assert.AreEqual("1", local.Get(RestProfileStore.DirtyKey));
            Assert.AreEqual(7, Run(store.LoadAsync()).lastSeenAt);
        }

        [Test] public void Load_ReadsTheServersProfile()
        {
            var doc = new JObject { ["fields"] = RestProfileStore.ToFields(Sample(555)) }.ToString(Formatting.None);
            var (store, http, _) = Make(h => h.Reply(doc));

            var loaded = Run(store.LoadAsync());

            Assert.AreEqual("GET", http.Requests[0].Method);
            Assert.AreEqual("ade", loaded.username);
            Assert.AreEqual("{\"skin\":3}", loaded.appearance);
            Assert.AreEqual(555, loaded.lastSeenAt);
        }

        [Test] public void Load_WithNoProfileYet_ReturnsNull()
        {
            var (store, _, _) = Make(h => h.Reply("{}", HttpStatusCode.NotFound));
            Assert.IsNull(Run(store.LoadAsync()));
        }
    }
}
