using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HypaFirebase.Tests
{
    public class RestRouteLogTests
    {
        static void Run(Task task) => task.GetAwaiter().GetResult();

        static RouteEvent Event(string route = "cloud", string from = null)
            => new RouteEvent { kind = "answer", route = route, model = "Gemini", language = "en", latencyMs = 1200, ok = true, fallbackFrom = from, device = "quest", at = 1700000000000 };

        static (RestRouteLog Log, StubHandler Http, FakeAuth Auth) Make(bool signedIn = true)
        {
            var http = new StubHandler();
            var auth = new FakeAuth();
            if (signedIn) Run(auth.SignInAnonymouslyAsync());
            return (new RestRouteLog(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example"), auth, http), http, auth);
        }

        [Test] public void AnEvent_IsCreatedUnderTheUsersRoutes_InTheRulesShape()
        {
            var (log, http, _) = Make();
            http.Reply("{}");

            log.Log(Event(from: "onDevice"));
            Run(log.Pending);

            var sent = http.Requests[0];
            Assert.AreEqual("POST", sent.Method);
            StringAssert.StartsWith("https://firestore.googleapis.com/v1/projects/proj/databases/(default)/documents/routes/fake-uid/events?documentId=", sent.Url);
            Assert.AreEqual("Bearer fake-id-token", sent.Headers["Authorization"]);
            Assert.AreEqual("fake-app-check-token", sent.Headers[ProxyApi.AppCheckHeader]);
            var fields = (JObject)JObject.Parse(sent.Body)["fields"];
            CollectionAssert.AreEquivalent(new[] { "kind", "route", "model", "language", "latencyMs", "ok", "fallbackFrom", "device", "at" }, ((JObject)fields).Properties().Select(p => p.Name));
            Assert.AreEqual("1200", (string)fields["latencyMs"]["integerValue"]);
            Assert.AreEqual(true, (bool)fields["ok"]["booleanValue"]);
            Assert.AreEqual("onDevice", (string)fields["fallbackFrom"]["stringValue"]);
            Assert.AreEqual(0, log.Queued);
        }

        [Test] public void EmptyFields_AreLeftOut()
        {
            var fields = RestRouteLog.Fields(new RouteEvent { kind = "translate", route = "offline", latencyMs = 0, ok = false, device = "quest", at = 1 });
            Assert.IsNull(fields["model"]);
            Assert.IsNull(fields["language"]);
            Assert.IsNull(fields["fallbackFrom"]);
        }

        [Test] public void Offline_EventsWait_AndGoOutWithTheNextOne()
        {
            var (log, http, _) = Make();
            http.Fail(new HttpRequestException("no network"));

            log.Log(Event());
            Run(log.Pending);
            Assert.AreEqual(1, log.Queued);

            http.Reply("{}").Reply("{}");
            log.Log(Event("hypa"));
            Run(log.Pending);

            Assert.AreEqual(0, log.Queued);
            Assert.AreEqual(3, http.Requests.Count);
        }

        [Test] public void ARefusedEvent_IsDropped_SoItCannotBlockTheRest()
        {
            var (log, http, _) = Make();
            http.Reply("{\"error\":{\"status\":\"PERMISSION_DENIED\"}}", HttpStatusCode.Forbidden).Reply("{}");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("refused \\(403\\)"));

            log.Log(Event());
            log.Log(Event("hypa"));
            Run(log.Pending);

            Assert.AreEqual(0, log.Queued);
            Assert.AreEqual(2, http.Requests.Count);
        }

        [Test] public void NothingIsSent_BeforeSignIn_AndTheQueueIsCapped()
        {
            var (log, http, _) = Make(signedIn: false);
            for (var i = 0; i < RestRouteLog.MaxQueued + 10; i++) log.Log(Event());
            Run(log.Pending);

            Assert.AreEqual(0, http.Requests.Count);
            Assert.AreEqual(RestRouteLog.MaxQueued, log.Queued);
        }
    }
}
