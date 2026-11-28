using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaCore.Tests
{
    public class HypaProxyClientTests
    {
        sealed class Stub : HttpMessageHandler
        {
            public HttpRequestMessage Request;
            public string Body;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Reply = "{}";
            /// <summary>How many sends fail before one gets through (a stale pooled connection), and how many were made.</summary>
            public int Fail, Sends;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Sends++;
                if (Fail-- > 0) throw new HttpRequestException("An error occurred while sending the request");
                Request = request;
                Body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new HttpResponseMessage(Status) { Content = new StringContent(Reply) };
            }
        }

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

        static (HypaProxyClient client, Stub stub, FakeAuth auth) Make()
        {
            var auth = new FakeAuth();
            auth.SignInAnonymouslyAsync().GetAwaiter().GetResult();
            var stub = new Stub();
            return (new HypaProxyClient("https://proxy.example/", auth, stub), stub, auth);
        }

        [Test] public void Calls_SendTheFirebaseTokens_ToTheContractPath()
        {
            var (client, stub, _) = Make();
            stub.Reply = "{\"translation\":\"Bawo ni\",\"language\":\"yo\",\"route\":{\"provider\":\"hypa\",\"model\":\"hypa-mt\"},\"usage\":{\"requestsUsed\":3,\"requestsLimit\":400}}";
            var res = Run(client.TranslateAsync(new TranslateRequest { text = "Hello", targetLang = "yo" }, CancellationToken.None));

            Assert.AreEqual("Bawo ni", res.translation);
            Assert.AreEqual("hypa-mt", res.route.model);
            Assert.AreEqual(3, res.usage.requestsUsed);
            Assert.AreEqual("https://proxy.example/hypa/translate", stub.Request.RequestUri.ToString());
            Assert.AreEqual("Bearer fake-id-token", stub.Request.Headers.GetValues("Authorization").Single());
            Assert.AreEqual("fake-app-check-token", stub.Request.Headers.GetValues("X-Firebase-AppCheck").Single());
            Assert.IsFalse(stub.Request.Headers.Contains("x-api-key"), "no Hypa key ever leaves the app");
            var body = JObject.Parse(stub.Body);
            Assert.AreEqual("Hello", (string)body["text"]);
            Assert.AreEqual("yo", (string)body["targetLang"]);
            Assert.IsNull(body["sourceLang"]);
        }

        [Test] public void AFailedSend_IsSentOnceMore_ThenReported()
        {
            var (client, stub, _) = Make();
            stub.Fail = 1;
            stub.Reply = "{\"transcript\":\"ẹ n lẹ\"}";
            Assert.AreEqual("ẹ n lẹ", Run(client.TranscribeAsync(new byte[] { 1 }, "Yoruba", CancellationToken.None)), "the stale connection is retried");
            Assert.AreEqual(2, stub.Sends);

            stub.Fail = 2; stub.Sends = 0;
            Assert.Throws<HypaException>(() => Run(client.TranscribeAsync(new byte[] { 1 }, "Yoruba", CancellationToken.None)));
            Assert.AreEqual(2, stub.Sends, "only once more");
        }

        [Test] public void OptionalFields_AreLeftOut()
        {
            var (client, stub, _) = Make();
            stub.Reply = "{\"audioBase64\":\"AAA=\",\"mime\":\"audio/mpeg\",\"sampleRate\":24000,\"route\":{\"model\":\"m\",\"voice\":\"v\"}}";
            var res = Run(client.TtsAsync(new TtsRequest { text = "Omi", language = "yo" }, CancellationToken.None));
            Assert.AreEqual("audio/mpeg", res.mime);
            Assert.AreEqual("v", res.route.voice);
            var body = JObject.Parse(stub.Body);
            CollectionAssert.AreEquivalent(new[] { "text", "language" }, body.Properties().Select(p => p.Name));
            Assert.AreEqual("https://proxy.example/hypa/tts", stub.Request.RequestUri.ToString());
        }

        [Test] public void ContractErrors_BecomeTypedExceptions()
        {
            var (client, stub, _) = Make();
            stub.Status = (HttpStatusCode)429;
            stub.Reply = "{\"error\":{\"code\":\"quota\",\"message\":\"Daily limit reached.\",\"scope\":\"user_seconds\",\"retryAfterSeconds\":3600}}";
            var e = Assert.Throws<HypaProxyException>(() => Run(client.ChatAsync(new ChatRequest { messages = new[] { new ChatMessage { role = "user", content = "hi" } } }, CancellationToken.None)));
            Assert.AreEqual(ProxyApi.ErrorQuota, e.Code);
            Assert.AreEqual(429, e.Status);
            Assert.AreEqual("user_seconds", e.Details.scope);
            Assert.AreEqual(3600, e.RetryAfterSeconds);
            Assert.IsFalse(e.Retryable);

            stub.Status = (HttpStatusCode)502;
            stub.Reply = "{\"error\":{\"code\":\"upstream\",\"message\":\"Hypa could not run it.\",\"reason\":\"tool_error\",\"retryable\":false}}";
            e = Assert.Throws<HypaProxyException>(() => Run(client.TranslateAsync(new TranslateRequest { text = "a", targetLang = "yo" }, CancellationToken.None)));
            Assert.AreEqual("tool_error", e.Details.reason);
            Assert.IsFalse(e.Retryable, "the proxy said a retry will not help");

            stub.Status = HttpStatusCode.BadGateway; stub.Reply = "<html>bad gateway</html>";
            e = Assert.Throws<HypaProxyException>(() => Run(client.AsrAsync(new AsrRequest { audio = "AAA=", sourceLang = "yo" }, CancellationToken.None)));
            Assert.AreEqual(ProxyApi.ErrorUpstream, e.Code);
            Assert.IsTrue(e.Retryable);
        }

        [Test] public void ASignedOutPlayer_IsRefusedBeforeAnyRequest()
        {
            var stub = new Stub();
            var client = new HypaProxyClient("https://proxy.example", new FakeAuth(), stub);
            var e = Assert.Throws<HypaProxyException>(() => Run(client.TranslateAsync(new TranslateRequest { text = "a", targetLang = "yo" }, CancellationToken.None)));
            Assert.AreEqual(ProxyApi.ErrorUnauthenticated, e.Code);
            Assert.IsNull(stub.Request);
        }

        [Test] public void AMissingProxyUrl_FailsLoudly()
            => Assert.Throws<System.ArgumentException>(() => new HypaProxyClient("", new FakeAuth()));

        [Test] public void Speech_GoesThroughHypaTts_AndComesBackAsHypaSpeech()
        {
            var (client, stub, _) = Make();
            stub.Reply = "{\"audioBase64\":\"UklGRg==\",\"mime\":\"audio/wav\",\"sampleRate\":24000,\"route\":{\"provider\":\"hypaai\",\"model\":\"tts-1\",\"voice\":\"Ade\"}}";

            var speech = Run(client.SpeechAsync("Ẹ káàárọ̀", "Yoruba", "Ade", "", CancellationToken.None));

            Assert.AreEqual("https://proxy.example/hypa/tts", stub.Request.RequestUri.ToString());
            var body = JObject.Parse(stub.Body);
            Assert.AreEqual("Yoruba", (string)body["language"]);
            Assert.AreEqual("Ade", (string)body["voice"]);
            Assert.IsNull(body["model"], "an empty model is left out");
            Assert.IsNull(body["inline"], "only the browser asks for inline audio");
            Assert.AreEqual("UklGRg==", speech.AudioBase64);
            Assert.AreEqual(24000, speech.SampleRate);
            Assert.AreEqual("tts-1", speech.Model);
            Assert.AreEqual("Ade", speech.Voice);
        }

        [Test] public void Transcribe_SendsTheWavToHypaAsr_AndReturnsTheTranscript()
        {
            var (client, stub, _) = Make();
            stub.Reply = "{\"transcript\":\" Ta ni Dodo? \",\"language\":\"yo\",\"audioSeconds\":2.1}";
            var wav = new byte[] { 82, 73, 70, 70, 1, 2, 3 };

            var text = Run(client.TranscribeAsync(wav, "Yoruba", CancellationToken.None));

            Assert.AreEqual("Ta ni Dodo?", text);
            Assert.AreEqual("https://proxy.example/hypa/asr", stub.Request.RequestUri.ToString());
            var body = JObject.Parse(stub.Body);
            Assert.AreEqual(System.Convert.ToBase64String(wav), (string)body["audio"]);
            Assert.AreEqual("Yoruba", (string)body["sourceLang"]);
        }

        [Test] public void Transcribe_ATooLongRecording_IsABadRequest()
        {
            var (client, stub, _) = Make();
            stub.Status = (HttpStatusCode)413;
            stub.Reply = "{\"error\":{\"code\":\"too_large\",\"message\":\"The recording is too long (max 30 seconds).\"}}";
            var e = Assert.Throws<HypaException>(() => Run(client.TranscribeAsync(new byte[] { 1 }, "English", CancellationToken.None)));
            Assert.AreEqual(HypaErrorKind.BadRequest, e.Kind);
        }

        [Test] public void ProxyErrors_BecomeTheAppsErrorKinds()
        {
            Assert.AreEqual(HypaErrorKind.Unauthorized, new HypaProxyException(ProxyApi.ErrorAppCheck, "x", 401).ToHypaException().Kind);
            Assert.AreEqual(HypaErrorKind.RateLimited, new HypaProxyException(ProxyApi.ErrorQuota, "x", 429).ToHypaException().Kind);
            Assert.AreEqual(HypaErrorKind.BadRequest, new HypaProxyException(ProxyApi.ErrorTooLarge, "x", 413).ToHypaException().Kind);
            Assert.AreEqual(HypaErrorKind.Offline, new HypaProxyException(ProxyApi.ErrorUpstream, "x", 0).ToHypaException().Kind);
            Assert.AreEqual(HypaErrorKind.Server, new HypaProxyException(ProxyApi.ErrorUpstream, "x", 502).ToHypaException().Kind);
            Assert.AreEqual(HypaErrorKind.ToolError, new HypaProxyException(ProxyApi.ErrorUpstream, "x", 502,
                new ProxyErrorBody { code = "upstream", reason = "tool_error" }).ToHypaException().Kind, "Hypa refused, e.g. a voice its model lacks");

            var (client, stub, _) = Make();
            stub.Status = HttpStatusCode.TooManyRequests;
            stub.Reply = "{\"error\":{\"code\":\"quota\",\"message\":\"Daily limit reached\"}}";
            var e = Assert.Throws<HypaException>(() => Run(client.SpeechAsync("Hi", "English", null, null, CancellationToken.None)));
            Assert.AreEqual(HypaErrorKind.RateLimited, e.Kind);
        }
    }
}
