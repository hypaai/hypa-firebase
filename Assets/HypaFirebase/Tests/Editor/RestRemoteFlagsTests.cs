using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestRemoteFlagsTests
    {
        const string Created = "{\"fid\":\"fid-1\",\"refreshToken\":\"refresh-1\",\"authToken\":{\"token\":\"fis-token\",\"expiresIn\":\"604800s\"}}";
        const string Fetched = "{\"entries\":{\"route_max_on_device_tokens\":\"3000\",\"route_hypa_langs\":\"yo,ha\"},\"state\":\"UPDATE\"}";

        static void Run(Task task) => task.GetAwaiter().GetResult();

        static RestRemoteFlags Make(StubHandler http, MemoryKeyValueStore local, Func<DateTime> now = null)
            => new RestRemoteFlags(new FirebaseRestConfig("KEY", "proj", "1:123:android:abc", "debug", "https://db.example"), local, http, now);

        [Test] public void FirstFetch_RegistersTheInstallation_ThenFetchesTheValues()
        {
            var http = new StubHandler().Reply(Created).Reply(Fetched);
            var local = new MemoryKeyValueStore();
            var flags = Make(http, local);

            Run(flags.FetchAsync());

            Assert.AreEqual("https://firebaseinstallations.googleapis.com/v1/projects/proj/installations", http.Requests[0].Url);
            Assert.AreEqual("KEY", http.Requests[0].Headers["x-goog-api-key"]);
            var install = JObject.Parse(http.Requests[0].Body);
            Assert.AreEqual("FIS_v2", (string)install["authVersion"]);
            Assert.AreEqual("1:123:android:abc", (string)install["appId"]);
            StringAssert.IsMatch("^[cdef][A-Za-z0-9_-]{21}$", (string)install["fid"]);

            Assert.AreEqual("https://firebaseremoteconfig.googleapis.com/v1/projects/proj/namespaces/firebase:fetch?key=KEY", http.Requests[1].Url);
            var fetch = JObject.Parse(http.Requests[1].Body);
            Assert.AreEqual("fid-1", (string)fetch["app_instance_id"]);
            Assert.AreEqual("fis-token", (string)fetch["app_instance_id_token"]);

            Assert.AreEqual("3000", flags.Get(RemoteConfigKeys.RouteMaxOnDeviceTokens, null));
            Assert.AreEqual("yo,ha", flags.Get(RemoteConfigKeys.RouteHypaLangs, null));
            Assert.AreEqual(RemoteConfigKeys.Default(RemoteConfigKeys.CloudModel), flags.Get(RemoteConfigKeys.CloudModel, null), "not in the template: the contract default");
            Assert.AreEqual("fid-1", local.Get(RestRemoteFlags.FidKey));
        }

        [Test] public void TheNextLaunch_ReusesTheInstallation_AndStartsWithTheLastValues()
        {
            var local = new MemoryKeyValueStore();
            Run(Make(new StubHandler().Reply(Created).Reply(Fetched), local).FetchAsync());

            var http = new StubHandler().Reply("{\"token\":\"fis-token-2\",\"expiresIn\":\"604800s\"}").Reply("{\"state\":\"NO_CHANGE\"}");
            var again = Make(http, local);
            Assert.AreEqual("3000", again.Get(RemoteConfigKeys.RouteMaxOnDeviceTokens, null), "before any fetch");

            Run(again.FetchAsync());

            Assert.AreEqual("https://firebaseinstallations.googleapis.com/v1/projects/proj/installations/fid-1/authTokens:generate", http.Requests[0].Url);
            Assert.AreEqual("FIS_v2 refresh-1", http.Requests[0].Headers["Authorization"]);
            Assert.AreEqual("3000", again.Get(RemoteConfigKeys.RouteMaxOnDeviceTokens, null), "NO_CHANGE keeps the values");
        }

        [Test] public void AnUnknownInstallation_IsRegisteredAgain()
        {
            var local = new MemoryKeyValueStore();
            local.Set(RestRemoteFlags.FidKey, "old"); local.Set(RestRemoteFlags.RefreshKey, "old-refresh");
            var http = new StubHandler().Reply("{\"error\":{\"message\":\"NOT_FOUND\"}}", HttpStatusCode.NotFound).Reply(Created).Reply(Fetched);

            Run(Make(http, local).FetchAsync());

            Assert.AreEqual("fid-1", local.Get(RestRemoteFlags.FidKey));
            Assert.AreEqual(3, http.Requests.Count);
        }

        [Test] public void NoTemplate_MeansTheDefaults()
        {
            var flags = Make(new StubHandler().Reply(Created).Reply("{\"state\":\"NO_TEMPLATE\"}"), new MemoryKeyValueStore());
            Run(flags.FetchAsync());
            Assert.AreEqual(RemoteConfigKeys.Default(RemoteConfigKeys.RouteHypaLangs), flags.Get(RemoteConfigKeys.RouteHypaLangs, null));
        }

        [Test] public void Offline_TheFetchQuietlyKeepsWhatItHad()
        {
            var local = new MemoryKeyValueStore();
            Run(Make(new StubHandler().Reply(Created).Reply(Fetched), local).FetchAsync());
            var flags = Make(new StubHandler().Fail(new HttpRequestException("offline")), local);

            Run(flags.FetchAsync());

            Assert.AreEqual("3000", flags.Get(RemoteConfigKeys.RouteMaxOnDeviceTokens, null));
        }

        [Test] public void AFidLooksLikeTheSdks()
        {
            var fids = Enumerable.Range(0, 50).Select(_ => RestRemoteFlags.NewFid()).ToList();
            Assert.IsTrue(fids.All(f => Regex.IsMatch(f, "^[cdef][A-Za-z0-9_-]{21}$")));
            Assert.AreEqual(50, fids.Distinct().Count());
        }
    }
}
