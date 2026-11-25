using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using HypaCore.Hybrid.Web;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaCore.Tests
{
    public class WebCloudToolsTests
    {
        readonly List<(string method, JToken args)> _calls = new List<(string, JToken)>();
        System.Func<string, JToken, JToken> _answer = (m, a) => null;
        SynchronizationContext _context;

        [SetUp] public void SetUp()
        {
            _context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _calls.Clear();
            _answer = (m, a) => null;
            WebBridge.Send = (id, method, args) =>
            {
                var parsed = JToken.Parse(args);
                _calls.Add((method, parsed));
                WebBridge.Receive(new JObject { ["id"] = id, ["kind"] = "ok", ["value"] = _answer(method, parsed) }.ToString());
            };
            WebBridge.Cancel = id => { };
        }

        [TearDown] public void TearDown() { WebBridge.ResetForTests(); SynchronizationContext.SetSynchronizationContext(_context); }

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static CloudTool Translate(List<IReadOnlyDictionary<string, string>> seen) => new CloudTool
        {
            Name = "translate", Description = "Translate text with Hypa.",
            Parameters = new Dictionary<string, string> { ["text"] = "What to translate", ["to"] = "The language" },
            Run = (args, ct) => { seen.Add(args); return Task.FromResult("mother"); },
        };

        [Test] public void Declarations_AreStringParameters_AllRequired()
        {
            var d = (JObject)CloudToolCalls.Declarations(new[] { Translate(new List<IReadOnlyDictionary<string, string>>()) })[0];
            Assert.AreEqual("translate", (string)d["name"]);
            Assert.AreEqual("object", (string)d["parameters"]["type"]);
            Assert.AreEqual("string", (string)d["parameters"]["properties"]["text"]["type"]);
            CollectionAssert.AreEqual(new[] { "text", "to" }, d["parameters"]["required"].Values<string>());
        }

        [Test] public void AToolCall_IsRunInCSharp_AndItsResultSentBack()
        {
            var seen = new List<IReadOnlyDictionary<string, string>>();
            var turn = 0;
            _answer = (m, a) => ++turn == 1
                ? new JObject
                {
                    ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray { new JObject { ["functionCall"] = new JObject { ["name"] = "translate", ["args"] = new JObject { ["text"] = "ìyá", ["to"] = "en" } } } } },
                    ["calls"] = new JArray { new JObject { ["name"] = "translate", ["args"] = new JObject { ["text"] = "ìyá", ["to"] = "en" } } },
                    ["text"] = "",
                }
                : new JObject { ["calls"] = new JArray(), ["text"] = "Ìyá means mother." };

            var reply = Run(new WebCloudModel(new FakeRemoteFlags()).GenerateAsync("What does ìyá mean?", new[] { Translate(seen) }, CancellationToken.None));

            Assert.AreEqual("Ìyá means mother.", reply);
            Assert.AreEqual("ìyá", seen.Single()["text"]);
            Assert.AreEqual(2, _calls.Count);
            Assert.IsTrue(_calls.All(c => c.method == "generateTurn"));
            var contents = (JArray)_calls[1].args["contents"];
            CollectionAssert.AreEqual(new[] { "user", "model", "function" }, contents.Select(c => (string)c["role"]));
            Assert.AreEqual("mother", (string)contents[2]["parts"][0]["functionResponse"]["response"]["result"]);
            Assert.AreEqual("translate", (string)_calls[0].args["tools"][0]["name"]);
        }

        [Test] public void AModelThatKeepsCallingTools_IsStoppedAfterTheLastRound()
        {
            _answer = (m, a) => ((JArray)a["tools"]).Count == 0
                ? new JObject { ["calls"] = new JArray(), ["text"] = "Done." }
                : new JObject
                {
                    ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray() },
                    ["calls"] = new JArray { new JObject { ["name"] = "translate", ["args"] = new JObject() } },
                };

            var reply = Run(new WebCloudModel(new FakeRemoteFlags()).GenerateAsync("Loop", new[] { Translate(new List<IReadOnlyDictionary<string, string>>()) }, CancellationToken.None));

            Assert.AreEqual("Done.", reply);
            Assert.AreEqual(CloudToolCalls.MaxRounds + 1, _calls.Count);
        }

        [Test] public void AnUnknownTool_GetsAnAnswerSaySo()
        {
            var r = Run(CloudToolCalls.RunAsync(new CloudTool[0], new JObject { ["name"] = "define" }, CancellationToken.None));
            StringAssert.Contains("no tool called define", (string)r["functionResponse"]["response"]["result"]);
        }

        [Test] public void RouteEvents_GoToTheBridge_WithoutEmptyFields()
        {
            new WebRouteLog().Log(new RouteEvent { kind = "answer", route = "offline", latencyMs = 3, ok = true, device = "web", at = 5 });

            var (method, args) = _calls.Single();
            Assert.AreEqual("logRoute", method);
            CollectionAssert.AreEquivalent(new[] { "kind", "route", "latencyMs", "ok", "device", "at" }, ((JObject)args).Properties().Select(p => p.Name));
        }
    }
}
