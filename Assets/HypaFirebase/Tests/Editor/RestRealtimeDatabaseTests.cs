using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestRealtimeDatabaseTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static (RestRealtimeDatabase Db, StubHandler Http) Make()
        {
            var http = new StubHandler();
            var auth = new FakeAuth();
            Run(auth.SignInAnonymouslyAsync());
            return (new RestRealtimeDatabase(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example/"), auth, http), http);
        }

        [Test] public void Get_CallsThePathWithTheTokenAndAppCheck()
        {
            var (db, http) = Make();
            http.Reply("{\"host\":\"alice\"}");

            var json = Run(db.GetAsync("/rooms/ABCDE/", CancellationToken.None));

            Assert.AreEqual("{\"host\":\"alice\"}", json);
            Assert.AreEqual("GET", http.Requests[0].Method);
            Assert.AreEqual("https://db.example/rooms/ABCDE.json?auth=fake-id-token", http.Requests[0].Url);
            Assert.AreEqual("fake-app-check-token", http.Requests[0].Headers["X-Firebase-AppCheck"]);
        }

        [Test] public void Set_Update_Delete_UsePutPatchAndDelete()
        {
            var (db, http) = Make();
            http.Reply("{}").Reply("{}").Reply("null");

            Run(db.SetAsync("rooms/ABCDE/players/bob", "{\"online\":true}", CancellationToken.None));
            Run(db.UpdateAsync("rooms/ABCDE/players/bob", "{\"handUp\":true}", CancellationToken.None));
            Run(db.DeleteAsync("rooms/ABCDE/players/bob", CancellationToken.None));

            CollectionAssert.AreEqual(new[] { "PUT", "PATCH", "DELETE" }, http.Requests.ConvertAll(r => r.Method));
            Assert.AreEqual("{\"online\":true}", http.Requests[0].Body);
            Assert.AreEqual("{\"handUp\":true}", http.Requests[1].Body);
        }

        [Test] public void Refused_ThrowsWithTheFirebaseErrorOnly()
        {
            var (db, http) = Make();
            http.Reply("{\"error\":\"Permission denied\"}", HttpStatusCode.Unauthorized);

            var e = Assert.Throws<HypaFirebaseException>(() => Run(db.GetAsync("rooms/ABCDE", CancellationToken.None)));

            Assert.AreEqual(401, e.StatusCode);
            StringAssert.DoesNotContain("fake-id-token", e.ToString());
        }

        [Test] public void SseParser_BuildsEvents_FromLines()
        {
            var parser = new RestRealtimeDatabase.SseParser();
            Assert.IsNull(parser.Feed("event: put"));
            Assert.IsNull(parser.Feed("data: {\"path\":\"/clock\",\"data\":{\"lineIndex\":2}}"));
            var put = parser.Feed("");
            Assert.AreEqual("put", put.Name);
            Assert.AreEqual("{\"path\":\"/clock\",\"data\":{\"lineIndex\":2}}", put.Data);

            Assert.IsNull(parser.Feed("event: keep-alive"));
            Assert.IsNull(parser.Feed("data: null"));
            var keepAlive = parser.Feed("");
            Assert.AreEqual("keep-alive", keepAlive.Name);

            Assert.IsNull(parser.Feed(""), "a blank line with nothing before it is not an event");
        }
    }
}
