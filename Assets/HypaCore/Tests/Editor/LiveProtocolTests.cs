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
    /// <summary>HYP-205 (G10): the Live API's wire format through Firebase AI Logic, and the web session over the bridge.</summary>
    public class LiveProtocolTests
    {
        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        /// <summary>Chunks wake the session's reader on the thread pool here (no Unity context): wait a little for it.</summary>
        static void Until(System.Func<bool> done)
        {
            var until = System.DateTime.UtcNow.AddSeconds(2);
            while (!done() && System.DateTime.UtcNow < until) Thread.Sleep(5);
        }

        static LiveSetup Setup(string handle = null) => new LiveSetup
        {
            Model = "gemini-live-x", SystemInstruction = "You are an old woman.", Voice = "Gacrux", ResumeHandle = handle,
            Tools = new[] { new CloudTool { Name = "show_item", Description = "Point to a thing.", Parameters = new Dictionary<string, string> { ["id"] = "The thing" } } },
        };

        [Test] public void Url_IsFirebaseAiLogicsBidiSocket_WithTheKey()
        {
            Assert.AreEqual("wss://firebasevertexai.googleapis.com/ws/google.firebase.vertexai.v1beta.GenerativeService/BidiGenerateContent?key=AIza%2Fx",
                LiveProtocol.Url("AIza/x"));
        }

        [Test] public void Setup_NamesTheModelInTheProject_AsksForAudioTranscriptsAndHandles()
        {
            var s = (JObject)LiveProtocol.Setup("my-project", Setup())["setup"];
            Assert.AreEqual("projects/my-project/models/gemini-live-x", (string)s["model"]);
            CollectionAssert.AreEqual(new[] { "AUDIO" }, s["generationConfig"]["responseModalities"].Values<string>());
            Assert.AreEqual("Gacrux", (string)s["generationConfig"]["speechConfig"]["voiceConfig"]["prebuiltVoiceConfig"]["voiceName"]);
            Assert.IsNotNull(s["inputAudioTranscription"]);
            Assert.IsNotNull(s["outputAudioTranscription"]);
            Assert.AreEqual(0, ((JObject)s["sessionResumption"]).Count, "{} asks for handles");
            Assert.AreEqual("You are an old woman.", (string)s["systemInstruction"]["parts"][0]["text"]);
            Assert.AreEqual("show_item", (string)s["tools"][0]["functionDeclarations"][0]["name"]);
        }

        [Test] public void Setup_WithAHandle_ResumesThatSession_AndWithoutAVoice_LeavesItOut()
        {
            var setup = Setup("h-1");
            setup.Voice = null;
            var s = (JObject)LiveProtocol.Setup("p", setup)["setup"];
            Assert.AreEqual("h-1", (string)s["sessionResumption"]["handle"]);
            Assert.IsNull(s["generationConfig"]["speechConfig"]);
        }

        [Test] public void Audio_Is16kPcmBase64_InRealtimeInput()
        {
            var pcm = LiveProtocol.Pcm16(new[] { 0f, 1f, -1f, 0.5f }, 0, 4);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0xff, 0x7f, 0x01, 0x80, 0x00, 0x40 }, pcm);
            var m = LiveProtocol.Audio(pcm);
            Assert.AreEqual("audio/pcm;rate=16000", (string)m["realtimeInput"]["audio"]["mimeType"]);
            CollectionAssert.AreEqual(pcm, System.Convert.FromBase64String((string)m["realtimeInput"]["audio"]["data"]));
            var back = LiveProtocol.Floats(pcm);
            Assert.AreEqual(0.5f, back[3], 0.001f);
        }

        [Test] public void ToolResponse_CarriesTheCallsIdAndResult()
        {
            var r = LiveProtocol.ToolResponse("c1", "show_item", "lit");
            var f = r["toolResponse"]["functionResponses"][0];
            Assert.AreEqual("c1", (string)f["id"]);
            Assert.AreEqual("show_item", (string)f["name"]);
            Assert.AreEqual("lit", (string)f["response"]["result"]);
        }

        [Test] public void Parse_ServerContent_InOrder_InterruptedHeardAudioSaidDone()
        {
            var audio = System.Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            var m = JObject.Parse("{\"serverContent\":{\"interrupted\":true,\"inputTranscription\":{\"text\":\"hello\"}," +
                "\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm;rate=24000\",\"data\":\"" + audio + "\"}}]}," +
                "\"outputTranscription\":{\"text\":\" my child\"},\"turnComplete\":true}}");
            var e = LiveProtocol.Parse(m);
            CollectionAssert.AreEqual(new[] { LiveEventKind.Interrupted, LiveEventKind.InputText, LiveEventKind.Audio, LiveEventKind.OutputText, LiveEventKind.TurnComplete },
                e.Select(x => x.Kind));
            Assert.AreEqual("hello", e[1].Text);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, e[2].Pcm);
            Assert.AreEqual(" my child", e[3].Text);
        }

        [Test] public void Parse_ToolCallsGoAwayAndHandles()
        {
            var calls = LiveProtocol.Parse(JObject.Parse("{\"toolCall\":{\"functionCalls\":[{\"id\":\"a\",\"name\":\"teach_word\",\"args\":{\"word\":\"omi\",\"n\":2}}]}}"));
            Assert.AreEqual(LiveEventKind.ToolCall, calls[0].Kind);
            Assert.AreEqual("teach_word", calls[0].Name);
            Assert.AreEqual("omi", calls[0].Args["word"]);
            Assert.AreEqual("2", calls[0].Args["n"]);

            var away = LiveProtocol.Parse(JObject.Parse("{\"goAway\":{\"timeLeft\":\"12.5s\"}}"));
            Assert.AreEqual(12.5f, away[0].Value, 0.001f);

            Assert.AreEqual("h2", LiveProtocol.Parse(JObject.Parse("{\"sessionResumptionUpdate\":{\"newHandle\":\"h2\",\"resumable\":true}}"))[0].Text);
            Assert.AreEqual(0, LiveProtocol.Parse(JObject.Parse("{\"sessionResumptionUpdate\":{\"newHandle\":\"h3\",\"resumable\":false}}")).Count);
            Assert.AreEqual(0, LiveProtocol.Parse(JObject.Parse("{\"usageMetadata\":{}}")).Count, "unknown messages give nothing");
            Assert.IsTrue(LiveProtocol.IsSetupComplete(JObject.Parse("{\"setupComplete\":{}}")));
        }

        [Test] public void Services_StartWithNoLiveModel()
        {
            var live = new HybridServices().Live;
            Assert.IsInstanceOf<NoLiveModel>(live);
            Assert.Throws<System.NotSupportedException>(() => Run(live.ConnectAsync(Setup(), CancellationToken.None)));
        }

        // ---------- the browser: firebase-bridge.js's liveStart, through WebBridge ----------

        sealed class FakeBridge
        {
            public readonly List<(int id, string method, JToken args)> Calls = new List<(int, string, JToken)>();
            public int LiveId;
            public bool Cancelled;

            public FakeBridge()
            {
                WebBridge.Send = (id, method, args) =>
                {
                    Calls.Add((id, method, JToken.Parse(args)));
                    if (method == "liveStart") LiveId = id;
                    else WebBridge.Receive(new JObject { ["id"] = id, ["kind"] = "ok", ["value"] = null }.ToString());
                };
                WebBridge.Cancel = id => Cancelled |= id == LiveId;
            }

            public void Chunk(JObject value) => WebBridge.Receive(new JObject { ["id"] = LiveId, ["kind"] = "chunk", ["value"] = value }.ToString());
        }

        SynchronizationContext _context;
        [SetUp] public void SetUp() { _context = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null); }
        [TearDown] public void TearDown() { WebBridge.ResetForTests(); SynchronizationContext.SetSynchronizationContext(_context); }

        [Test] public void Web_StartsWithTheSetup_OwnsTheAudio_AndTurnsChunksIntoEvents()
        {
            var bridge = new FakeBridge();
            var connecting = new WebLiveModel().ConnectAsync(Setup(), CancellationToken.None);
            var start = bridge.Calls.Single(c => c.method == "liveStart").args;
            Assert.AreEqual("gemini-live-x", (string)start["model"]);
            Assert.AreEqual("show_item", (string)start["tools"][0]["name"]);
            Assert.IsFalse(connecting.IsCompleted, "waits for the page to say the session is open");

            bridge.Chunk(new JObject { ["t"] = "ready" });
            var session = Run(connecting);
            Assert.IsTrue(session.OwnsAudio);
            Assert.IsTrue(session.Open);

            bridge.Chunk(new JObject { ["t"] = "level", ["v"] = 0.4, ["mic"] = 0.2 });
            bridge.Chunk(new JObject { ["t"] = "in", ["text"] = "hi" });
            bridge.Chunk(new JObject { ["t"] = "tool", ["calls"] = new JArray(new JObject { ["id"] = "c", ["name"] = "show_item", ["args"] = new JObject { ["id"] = "broom" } }) });
            bridge.Chunk(new JObject { ["t"] = "mic", ["state"] = "denied" });
            Until(() => session.MicDenied);
            Assert.AreEqual(0.4f, session.VoiceLevel, 0.001f);
            Assert.IsTrue(session.MicDenied);
            var events = new List<LiveEvent>();
            while (session.TryDequeue(out var e)) events.Add(e);
            CollectionAssert.AreEqual(new[] { LiveEventKind.InputText, LiveEventKind.ToolCall }, events.Select(e => e.Kind));
            Assert.AreEqual("broom", events[1].Args["id"]);

            session.SendToolResponse("c", "show_item", "lit");
            var tool = bridge.Calls.Single(c => c.method == "liveTool").args;
            Assert.AreEqual("c", (string)tool["id"]);
            Assert.AreEqual("lit", (string)tool["result"]);

            session.Dispose();
            Assert.IsTrue(bridge.Cancelled, "ending aborts liveStart, which closes the page's session");
        }

        [Test] public void Web_AFailedStart_Throws()
        {
            var bridge = new FakeBridge();
            var connecting = new WebLiveModel().ConnectAsync(Setup(), CancellationToken.None);
            WebBridge.Receive(new JObject { ["id"] = bridge.LiveId, ["kind"] = "err", ["code"] = "fetch-error", ["message"] = "no socket" }.ToString());
            Assert.Throws<WebBridgeException>(() => Run(connecting));
        }
    }
}
