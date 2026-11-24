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

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
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
    }
}
