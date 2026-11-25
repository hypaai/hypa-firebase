using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HypaStories.Tests
{
    public class HybridStoryQaTests
    {
        sealed class Proxy : HttpMessageHandler
        {
            public readonly List<(string path, JObject body)> Calls = new List<(string, JObject)>();
            public Func<string, JObject, (HttpStatusCode, string)> Answer = (p, b) => (HttpStatusCode.OK, "{}");
            public bool Unreachable;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = JObject.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Calls.Add((request.RequestUri.AbsolutePath, body));
                if (Unreachable) throw new HttpRequestException("No such host is known.");
                var (status, reply) = Answer(request.RequestUri.AbsolutePath, body);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(reply) });
            }
        }

        sealed class SilentCloud : ICloudModel
        {
            public Task<string> GenerateAsync(string prompt, CancellationToken ct) => Never<string>(ct);
            public Task<string> GenerateAsync(string prompt, IReadOnlyList<CloudTool> tools, CancellationToken ct) => Never<string>(ct);
            public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
            {
                await Never<string>(ct);
                yield break;
            }

            static Task<T> Never<T>(CancellationToken ct)
            {
                var tcs = new TaskCompletionSource<T>();
                ct.Register(() => tcs.TrySetCanceled());
                return tcs.Task;
            }
        }

        SynchronizationContext _ctx;
        HybridServices _svc;
        FakeOnDeviceModel _device;
        FakeCloudModel _cloud;
        FakeRouteLog _log;
        Proxy _proxy;
        bool _online, _marked, _share;
        AiMode _mode;
        double _now;

        [SetUp] public void SetUp()
        {
            _ctx = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _device = new FakeOnDeviceModel { Available = true, Reply = "Dodo is a creature the whole town fears. Nobody dares to challenge it." };
            _cloud = new FakeCloudModel { Reply = "Auta is a young boy. He is staying with an old woman." };
            _log = new FakeRouteLog();
            _svc = new HybridServices { OnDevice = _device, Cloud = _cloud, RouteLog = _log };
            _svc.Auth.SignInAnonymouslyAsync().GetAwaiter().GetResult();
            _proxy = new Proxy();
            _online = true; _marked = false; _share = true; _mode = AiMode.Hybrid; _now = 0;
        }

        [TearDown] public void TearDown() => SynchronizationContext.SetSynchronizationContext(_ctx);

        HybridStoryQa Qa() => new HybridStoryQa(() => _svc, () => new HypaProxyClient("https://proxy.test", _svc.Auth, _proxy),
            () => _online, () => _marked = true, () => _mode, () => _share, () => _now += 100, id => "Seven Hot Stones");

        static QaRequest Ask(string lang, string question = "Who is Dodo?", string story = "Long ago, in a town in Hausaland.\nDodo came at night.")
            => new QaRequest { Question = question, StoryId = "seven-stones", StorySoFar = story, Language = StoryLanguages.Find(lang), LineIndex = 10 };

        static (List<(string text, int i, bool last)> said, RouteDecision route) Run(HybridStoryQa qa, QaRequest req)
        {
            var said = new List<(string, int, bool)>();
            RouteDecision route = null;
            qa.Routed += d => route = d;
            qa.AnswerAsync(req, (t, i, last) => said.Add((t, i, last)), CancellationToken.None).GetAwaiter().GetResult();
            return (said, route);
        }

        static string Reply(string text) => new JObject { ["content"] = text, ["route"] = new JObject { ["model"] = "hypa-chat" } }.ToString();

        [Test] public void English_IsAnsweredOnTheDevice_SentenceBySentence()
        {
            var (said, route) = Run(Qa(), Ask("en"));
            CollectionAssert.AreEqual(new[] { "Dodo is a creature the whole town fears.", "Nobody dares to challenge it." }, said.Select(s => s.text));
            CollectionAssert.AreEqual(new[] { false, true }, said.Select(s => s.last));
            Assert.AreEqual(Route.OnDevice, route.Route);
            StringAssert.StartsWith("On device · Gemini Nano · ", route.Chip());
            Assert.Greater(route.LatencyMs, 0);
            StringAssert.Contains("Dodo came at night.", _device.LastPrompt);
            StringAssert.Contains("The listener asks: Who is Dodo?", _device.LastPrompt);
            StringAssert.Contains("Answer in English, in 2 or 3 short sentences", _device.LastPrompt);
            Assert.AreEqual(0, _proxy.Calls.Count);
            Assert.AreEqual("onDevice", _log.Events.Single().route);
            Assert.IsTrue(_log.Events[0].ok);
        }

        [Test] public void Yoruba_IsAnsweredByHypa_ThroughTheProxy()
        {
            _proxy.Answer = (p, b) => (HttpStatusCode.OK, Reply("Dodo jẹ́ ẹ̀dá tí gbogbo ìlú ń bẹ̀rù. Kò sí ẹni tó lè dojú kọ ọ́."));
            var (said, route) = Run(Qa(), Ask("yo"));
            Assert.AreEqual(2, said.Count);
            Assert.AreEqual(Route.Hypa, route.Route);
            StringAssert.StartsWith("Hypa · Yorùbá · ", route.Chip());
            var (path, body) = _proxy.Calls.Single();
            Assert.AreEqual("/hypa/chat", path);
            Assert.AreEqual("system", (string)body["messages"][0]["role"]);
            StringAssert.Contains("Answer in Yoruba", (string)body["messages"][1]["content"]);
        }

        [Test] public void WhenHypaFails_CloudGeminiAnswers_InTheLanguageIfItCan()
        {
            _proxy.Answer = (p, b) => ((HttpStatusCode)502, "{\"error\":{\"code\":\"upstream\",\"message\":\"Hypa had a problem\"}}");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Hypa · Yorùbá couldn't answer"));
            var (said, route) = Run(Qa(), Ask("yo"));
            Assert.AreEqual(Route.CloudGemini, route.Route);
            Assert.AreEqual("Auta is a young boy.", said[0].text);
            StringAssert.Contains("answer in English and begin with “(In English)”", _cloud.LastPrompt);
            Assert.IsFalse(_marked, "Hypa refusing is not the network being down");
            Assert.AreEqual("hypa", _log.Events.Single().fallbackFrom);
        }

        [Test] public void AnOnDeviceAnswerInTheWrongLanguage_IsAskedAgainInTheCloud()
        {
            _device.Reply = "Dodo est une créature que toute la ville craint. Personne ne la défie.";
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("couldn't answer \\(The answer came in another language"));
            var (said, route) = Run(Qa(), Ask("en"));
            Assert.AreEqual(Route.CloudGemini, route.Route);
            Assert.AreEqual("Auta is a young boy.", said[0].text, "nothing in French was spoken");
            Assert.AreEqual("The device answered in the wrong language", route.Reason);
        }

        [Test] public void Offline_TheDeviceAnswers_AndSaysItIsOffline()
        {
            _online = false;
            var (said, route) = Run(Qa(), Ask("en"));
            Assert.AreEqual("Offline · on device", route.Chip().Substring(0, "Offline · on device".Length));
            Assert.AreEqual(StoryLanguages.Lines("en").BackOnline, said.Last().text);
            Assert.IsTrue(said.Last().last);
            Assert.AreEqual(3, said.Count);
            Assert.AreEqual(0, _proxy.Calls.Count);
        }

        [Test] public void Offline_WithoutAModel_TheSavedAnswersSpeak_SpoilerSafe()
        {
            _online = false; _device.Available = false;
            var (said, route) = Run(Qa(), Ask("en", "What happens to Dodo, is it dead?"));
            Assert.AreEqual(HybridRouter.SavedAnswers, route.Model);
            Assert.AreEqual("Hmm, I cannot tell you that just yet.", said[0].text, "line 10 is before Dodo's end: no spoiler");
            Assert.AreEqual(StoryLanguages.Lines("en").BackOnline, said.Last().text);
        }

        [Test] public void ADeadNetwork_MarksItDown_AndTheAnswerStillComes()
        {
            _proxy.Unreachable = true; _device.Available = true;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("couldn't answer"));
            var (said, route) = Run(Qa(), Ask("ha"));
            Assert.IsTrue(_marked);
            Assert.AreEqual("Offline · on device", route.Chip().Substring(0, "Offline · on device".Length));
            Assert.AreEqual(StoryLanguages.Lines("ha").BackOnline, said.Last().text);
        }

        [Test] public void AWordQuestion_GoesToTheCloud_WhichCallsHypasTranslate()
        {
            _proxy.Answer = (p, b) => (HttpStatusCode.OK, new JObject { ["translation"] = "mother" }.ToString());
            _cloud.CallTool = "define";
            _cloud.CallArgs = new Dictionary<string, string> { ["word"] = "ìyá", ["language"] = "Yoruba" };
            _cloud.ReplyAfterTool = r => "Ìyá means mother. The old woman is like a mother to Auta.";
            var (said, route) = Run(Qa(), Ask("en", "What does ìyá mean?"));
            Assert.AreEqual(Route.CloudGemini, route.Route);
            StringAssert.Contains("only the cloud can call Hypa's tools", route.Reason);
            Assert.AreEqual("Ìyá means mother.", said[0].text);
            var (path, body) = _proxy.Calls.Single();
            Assert.AreEqual("/hypa/translate", path);
            Assert.AreEqual("ìyá", (string)body["text"]);
            Assert.AreEqual("English", (string)body["targetLang"]);
            StringAssert.Contains("“ìyá” (Yoruba) means “mother”", _cloud.ToolResults.Single());
        }

        [Test] public void AFailedTool_TellsTheModel_AndTheAnswerGoesOn()
        {
            _proxy.Answer = (p, b) => ((HttpStatusCode)429, "{\"error\":{\"code\":\"quota\",\"message\":\"Daily limit reached.\"}}");
            _cloud.CallTool = "translate";
            _cloud.ReplyAfterTool = r => "I can't look that up now. Let's keep listening.";
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("a Hypa tool failed"));
            var (said, _) = Run(Qa(), Ask("en", "How do you say fire in Hausa?"));
            Assert.AreEqual("Hypa couldn't look that up just now.", _cloud.ToolResults.Single());
            Assert.AreEqual(2, said.Count);
        }

        [Test] public void AModelStillDownloading_TheCloudAnswers_AndThePlayerIsTold()
        {
            _device.Status = "downloading";
            var qa = Qa();
            var notices = new List<string>();
            qa.Notice += notices.Add;
            var (_, route) = Run(qa, Ask("es"));
            Assert.AreEqual(Route.CloudGemini, route.Route);
            Assert.AreEqual(HybridRouter.Downloading, route.Reason);
            CollectionAssert.AreEqual(new[] { StoryLanguages.Lines("es").Preparing }, notices);
        }

        [Test] public void AReadyModel_TellsThePlayerNothing()
        {
            var qa = Qa();
            var notices = new List<string>();
            qa.Notice += notices.Add;
            Run(qa, Ask("en"));
            Assert.IsEmpty(notices);
        }

        [Test] public void EveryLanguage_HasItsLines()
        {
            foreach (var lang in StoryLanguages.All)
            {
                var l = StoryLanguages.Lines(lang.Code);
                Assert.IsNotEmpty(l.Preparing, lang.Code);
                Assert.IsNotEmpty(l.BackOnline, lang.Code);
            }
        }

        [Test] public void Settings_AreFollowed()
        {
            _mode = AiMode.CloudOnly;
            Assert.AreEqual(Route.CloudGemini, Run(Qa(), Ask("en")).route.Route);
            _mode = AiMode.OnDeviceOnly; _device.Available = false;
            var (said, route) = Run(Qa(), Ask("en"));
            Assert.AreEqual(HybridRouter.SavedAnswers, route.Model);
            Assert.AreNotEqual(StoryLanguages.Lines("en").BackOnline, said.Last().text, "online: no 'back online' line");
        }

        [Test] public void NothingWithinTheDeadline_CountsAsOffline()
        {
            _svc.Cloud = new SilentCloud(); _device.Available = false;
            var qa = Qa(); qa.DeadlineMs = 50;
            Assert.Throws<TimeoutException>(() => Run(qa, Ask("en")));
            Assert.IsFalse(_log.Events.Single().ok);
        }

        [Test] public void OptingOut_LogsNothing()
        {
            _share = false;
            Run(Qa(), Ask("en"));
            Assert.AreEqual(0, _log.Events.Count);
        }

        [Test] public void TheStoryIsTrimmedFromItsStart_ToFitTheDevice()
        {
            var story = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"Line {i} of the story, told by the fire."));
            var req = Ask("en", "Who is Dodo?", story);
            var full = StoryPrompt.Build(req, "Seven Hot Stones");
            var fitted = StoryPrompt.Build(req, "Seven Hot Stones", 1000);
            Assert.Greater(HybridRouter.Tokens(full.Text), 1000);
            Assert.LessOrEqual(HybridRouter.Tokens(fitted.Text), 1000);
            StringAssert.Contains("Line 400 of the story", fitted.Text, "the interrupted line is kept");
            StringAssert.DoesNotContain("Line 1 of the story", fitted.Text);
            StringAssert.Contains("The listener asks: Who is Dodo?", fitted.Text);

            _svc.Flags = new FakeRemoteFlags().Set(RemoteConfigKeys.RouteMaxOnDeviceTokens, "1000");
            var (_, route) = Run(Qa(), req);
            Assert.AreEqual(Route.CloudGemini, route.Route, "too long for the device: the cloud gets the whole story");
            StringAssert.Contains("Line 1 of the story", _cloud.LastPrompt);
        }

        [Test] public void Sentences_StreamAsSoonAsTheNextOneBegins()
        {
            var said = new List<(string, int, bool)>();
            var s = new SentenceStream((t, i, last) => said.Add((t, i, last)));
            s.Push("Auta heats seven ");
            Assert.AreEqual(0, said.Count);
            s.Push("stones. He has a ");
            CollectionAssert.AreEqual(new[] { ("Auta heats seven stones.", 0, false) }, said);
            s.Push("plan. ");
            s.Finish();
            CollectionAssert.AreEqual(new[] { ("Auta heats seven stones.", 0, false), ("He has a plan.", 1, true) }, said);

            said.Clear();
            s = new SentenceStream((t, i, last) => said.Add((t, i, last)));
            s.Push("One. Two. Three. Four. Five. Six.");
            Assert.IsTrue(s.Enough);
            s.Finish("Tail.");
            CollectionAssert.AreEqual(new[] { "One.", "Two.", "Three.", "Four.", "Tail." }, said.Select(x => x.Item1));
            Assert.IsTrue(said.Last().Item3);
        }

        [Test] public void ReplyLanguage_OnlyRefusesAClearMismatch()
        {
            Assert.AreEqual("fr", ReplyLanguage.Guess("Dodo est une créature que toute la ville craint."));
            Assert.AreEqual("es", ReplyLanguage.Guess("Dodo es una criatura que todo el pueblo teme y no se va."));
            Assert.AreEqual("en", ReplyLanguage.Guess("Dodo is a creature and the town fears it."));
            Assert.IsNull(ReplyLanguage.Guess("Dodo!"));
            Assert.IsTrue(ReplyLanguage.Mismatch("Dodo est une créature que toute la ville craint.", "en"));
            Assert.IsFalse(ReplyLanguage.Mismatch("Dodo est une créature que toute la ville craint.", "fr"));
            Assert.IsFalse(ReplyLanguage.Mismatch("Dodo is scary.", "es"), "too short to tell");
            Assert.IsFalse(ReplyLanguage.Mismatch("Dodo is a creature and the town fears it.", "yo"), "only the three on-device languages are checked");
        }

        [Test] public void WordQuestions_AreRecognised()
        {
            foreach (var q in new[] { "What does ìyá mean?", "what is the meaning of sannu", "How do you say fire in Hausa?", "Que veut dire Eshu ?", "¿Qué significa Dodo?", "¿Cómo se dice hola en yoruba?", "Can you translate this?" })
                Assert.IsTrue(ToolQuestion.Matches(q), q);
            foreach (var q in new[] { "Who is Dodo?", "Why did Auta heat the stones?", "What happens next?", "Where is the town?" })
                Assert.IsFalse(ToolQuestion.Matches(q), q);
        }

        sealed class Inner : ITryApi
        {
            public Task<IReadOnlyList<HypaLanguage>> LanguagesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HypaLanguage>>(new HypaLanguage[0]);
            public Task<HypaTranslation> TranslateAsync(string text, string targetLang, string sourceLang, CancellationToken ct) => throw new InvalidOperationException("not routed");
            public Task<HypaDictionaryEntry> LookUpAsync(string word, string language, CancellationToken ct) => Task.FromResult<HypaDictionaryEntry>(null);
            public Task SpeakAsync(string text, string language, string voice, CancellationToken ct) => Task.CompletedTask;
            public Task<string> ExplainAsync(string a, string b, string c, string d, CancellationToken ct) => Task.FromResult("");
            public bool IsPlaying => false;
            public void StopSpeaking() { }
        }

        HybridTryApi Try() => new HybridTryApi(new Inner(), () => _svc, () => new HypaProxyClient("https://proxy.test", _svc.Auth, _proxy),
            () => _online, () => _marked = true, () => _mode, () => _share, () => _now += 50);

        static HypaTranslation Translate(HybridTryApi api, string text, string to, string from)
            => api.TranslateAsync(text, to, from, CancellationToken.None).GetAwaiter().GetResult();

        [Test] public void TryHypa_EnglishToFrench_OnTheDevice_YorubaThroughHypa()
        {
            _device.Reply = "“Bonjour”";
            var api = Try();
            Assert.AreEqual("Bonjour", Translate(api, "Hello", "French", "English").Text);
            Assert.AreEqual(Route.OnDevice, api.LastRoute.Route);
            StringAssert.Contains("into French", _device.LastPrompt);

            _proxy.Answer = (p, b) => (HttpStatusCode.OK, new JObject { ["translation"] = "Ẹ n lẹ", ["language"] = "English", ["route"] = new JObject { ["model"] = "hypa-mt" } }.ToString());
            var r = Translate(api, "Hello", "Yoruba", null);
            Assert.AreEqual("Ẹ n lẹ", r.Text);
            Assert.AreEqual("English", r.DetectedLanguage);
            StringAssert.StartsWith("Hypa · Yorùbá · ", api.LastRoute.Chip());
            var body = _proxy.Calls.Single().body;
            Assert.AreEqual("Yoruba", (string)body["targetLang"]);
            Assert.IsNull(body["sourceLang"], "auto-detect");
            CollectionAssert.AreEqual(new[] { "onDevice", "hypa" }, _log.Events.Select(e => e.route));
            Assert.IsTrue(_log.Events.All(e => e.kind == "translate"));
        }

        [Test] public void TryHypa_Offline_OnlyTheDevicePairsWork()
        {
            _online = false; _device.Reply = "Hola";
            var api = Try();
            Assert.AreEqual("Hola", Translate(api, "Hello", "Spanish", "English").Text);
            var e = Assert.Throws<HypaException>(() => Translate(api, "Hello", "Hausa", "English"));
            Assert.AreEqual(HypaErrorKind.Offline, e.Kind);
            Assert.AreEqual(0, _proxy.Calls.Count);
        }

        [Test] public void TryHypa_ADeviceFailure_IsTranslatedByHypa()
        {
            _device.Status = "available"; _device.Available = false;
            _proxy.Answer = (p, b) => (HttpStatusCode.OK, new JObject { ["translation"] = "Bonjour" }.ToString());
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("the device couldn't translate"));
            var api = Try();
            Assert.AreEqual("Bonjour", Translate(api, "Hello", "French", "English").Text);
            Assert.AreEqual(Route.Hypa, api.LastRoute.Route);
            Assert.AreEqual("onDevice", _log.Events.Single().fallbackFrom);
        }

        [Test] public void TryHypaFlow_TranslatesThroughTheRouter()
        {
            _device.Reply = "Bonjour";
            var api = Try();
            var flow = new TryHypaFlow(api, null, "French");
            flow.BindStory("English", new[] { "English", "French" });
            flow.UseWordAsync("Hello").GetAwaiter().GetResult();
            Assert.AreEqual("Bonjour", flow.Output);
            Assert.AreEqual(Route.OnDevice, api.LastRoute.Route);
        }
    }
}
