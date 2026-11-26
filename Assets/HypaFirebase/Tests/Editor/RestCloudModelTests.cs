using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestCloudModelTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static string Answer(params JObject[] parts)
            => new JObject { ["candidates"] = new JArray { new JObject { ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray(parts) } } } }.ToString(Formatting.None);

        static JObject TextPart(string text) => new JObject { ["text"] = text };

        static (RestCloudModel Model, StubHandler Http) Make(string model = null)
        {
            var http = new StubHandler();
            var auth = new FakeAuth();
            Run(auth.SignInAnonymouslyAsync());
            return (new RestCloudModel(new FirebaseRestConfig("KEY", "proj", "app", "debug", "https://db.example"), auth, () => model, http), http);
        }

        static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
        {
            var parts = new List<string>();
            await foreach (var part in stream) parts.Add(part);
            return parts;
        }

        [Test] public void Generate_CallsFirebaseAiLogic_LikeTheSdk()
        {
            var (model, http) = Make("gemini-x");
            http.Reply(Answer(TextPart("Hello "), TextPart("there.")));

            Assert.AreEqual("Hello there.", Run(model.GenerateAsync("Hi", CancellationToken.None)));

            var sent = http.Requests.Single();
            Assert.AreEqual("https://firebasevertexai.googleapis.com/v1beta/projects/proj/models/gemini-x:generateContent", sent.Url);
            Assert.AreEqual("KEY", sent.Headers["x-goog-api-key"]);
            Assert.AreEqual("fake-app-check-token", sent.Headers[ProxyApi.AppCheckHeader]);
            Assert.AreEqual("Firebase fake-id-token", sent.Headers["Authorization"]);
            Assert.AreEqual("Hi", (string)JObject.Parse(sent.Body)["contents"][0]["parts"][0]["text"]);
            Assert.IsNull(JObject.Parse(sent.Body)["tools"]);
        }

        [Test] public void WithoutAModelName_TheContractDefaultIsUsed()
        {
            var (model, _) = Make();
            StringAssert.Contains("/models/" + RemoteConfigKeys.Default(RemoteConfigKeys.CloudModel) + ":", model.Url(false));
        }

        [Test] public void ToolCalls_AreRun_AndTheirResultsSentBack()
        {
            var (model, http) = Make("gemini-x");
            var call = new JObject { ["functionCall"] = new JObject { ["name"] = "translate", ["args"] = new JObject { ["text"] = "ìyá", ["to"] = "en" } } };
            http.Reply(Answer(call)).Reply(Answer(TextPart("Ìyá means mother.")));
            var seen = new List<IReadOnlyDictionary<string, string>>();
            var tool = new CloudTool
            {
                Name = "translate", Description = "Translate.", Parameters = new Dictionary<string, string> { ["text"] = "Text", ["to"] = "Language" },
                Run = (args, ct) => { seen.Add(args); return Task.FromResult("mother"); },
            };

            Assert.AreEqual("Ìyá means mother.", Run(model.GenerateAsync("What does ìyá mean?", new[] { tool }, CancellationToken.None)));

            Assert.AreEqual("ìyá", seen.Single()["text"]);
            var first = JObject.Parse(http.Requests[0].Body);
            Assert.AreEqual("translate", (string)first["tools"][0]["functionDeclarations"][0]["name"]);
            var second = (JArray)JObject.Parse(http.Requests[1].Body)["contents"];
            CollectionAssert.AreEqual(new[] { "user", "model", "function" }, second.Select(c => (string)c["role"]));
            Assert.AreEqual("mother", (string)second[2]["parts"][0]["functionResponse"]["response"]["result"]);
        }

        [Test] public void Stream_YieldsEachChunk_AndSkipsThoughts()
        {
            var (model, http) = Make("gemini-x");
            var thought = new JObject { ["text"] = "thinking", ["thought"] = true };
            http.Reply("data: " + Answer(thought) + "\n\ndata: " + Answer(TextPart("Once ")) + "\n\ndata: " + Answer(TextPart("upon a time.")) + "\n\n");

            CollectionAssert.AreEqual(new[] { "Once ", "upon a time." }, Run(Collect(model.StreamAsync("Tell me", CancellationToken.None))));
            StringAssert.EndsWith(":streamGenerateContent?alt=sse", http.Requests.Single().Url);
        }

        [Test] public void ARefusal_Throws_SoTheRouterFallsBack()
        {
            var (model, http) = Make("gemini-x");
            http.Reply("{\"error\":{\"message\":\"PERMISSION_DENIED\"}}", HttpStatusCode.Forbidden);

            var e = Assert.Throws<HypaFirebaseException>(() => Run(model.GenerateAsync("Hi", CancellationToken.None)));
            Assert.AreEqual(403, e.StatusCode);
        }

        [Test] public void AnEmptyAnswer_Throws()
        {
            var (model, http) = Make("gemini-x");
            http.Reply(Answer());
            Assert.Throws<HypaFirebaseException>(() => Run(model.GenerateAsync("Hi", CancellationToken.None)));
        }

        [Test] public void TheLocalConfig_NeedsKeyProjectAndApp()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hypa-config-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                Assert.IsNull(FirebaseStartup.LoadConfig(dir));
                var file = System.IO.Path.Combine(dir, QuestRestSpike.ConfigFile);
                System.IO.File.WriteAllText(file, "{\"apiKey\":\"k\",\"projectId\":\"p\"}");
                Assert.IsNull(FirebaseStartup.LoadConfig(dir));
                System.IO.File.WriteAllText(file, "{\"apiKey\":\"k\",\"projectId\":\"p\",\"appId\":\"a\",\"appCheckDebugToken\":\"t\"}");
                Assert.AreEqual("p", FirebaseStartup.LoadConfig(dir).ProjectId);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }
    }
}
