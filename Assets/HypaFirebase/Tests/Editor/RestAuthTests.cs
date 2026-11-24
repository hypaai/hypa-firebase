using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HypaFirebase.Rest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestAuthTests
    {
        sealed class FakeHandler : HttpMessageHandler
        {
            public readonly List<(string Url, string Body)> Requests = new List<(string, string)>();
            readonly Queue<(HttpStatusCode Status, string Body)> _responses = new Queue<(HttpStatusCode, string)>();
            public void Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => _responses.Enqueue((status, body));
            /// <summary>When set, each reply waits for this, so a test can act while a call is in flight.</summary>
            public TaskCompletionSource<bool> Hold;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add((request.RequestUri.ToString(), request.Content == null ? "" : await request.Content.ReadAsStringAsync()));
                if (Hold != null) await Hold.Task;
                var next = _responses.Dequeue();
                return new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body) };
            }
        }

        sealed class MemoryStore : IRefreshTokenStore
        {
            public string Value;
            public string Load() => Value;
            public void Save(string refreshToken) => Value = refreshToken;
            public void Clear() => Value = null;
        }

        const string SignUpReply = "{\"idToken\":\"id-1\",\"refreshToken\":\"refresh-1\",\"expiresIn\":\"3600\",\"localId\":\"user-1\"}";
        const string RefreshReply = "{\"id_token\":\"id-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":\"3600\",\"user_id\":\"user-1\"}";

        static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
        static void Run(Task task) => task.GetAwaiter().GetResult();

        [Test] public void FirstLaunch_SignsUpAnonymously_AndKeepsTheRefreshToken()
        {
            var http = new FakeHandler(); http.Reply(SignUpReply);
            var store = new MemoryStore();
            var auth = new RestAuthSession("KEY", store, http);
            int changed = 0; auth.Changed += () => changed++;

            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));

            Assert.IsTrue(auth.IsSignedIn);
            Assert.AreEqual("user-1", auth.UserId);
            Assert.AreEqual("refresh-1", store.Value);
            Assert.AreEqual(1, changed);
            Assert.AreEqual("https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=KEY", http.Requests[0].Url);
            Assert.AreEqual(true, (bool)JObject.Parse(http.Requests[0].Body)["returnSecureToken"]);
        }

        [Test] public void LaterLaunch_RestoresTheSameUser_WithoutSigningUpAgain()
        {
            var http = new FakeHandler(); http.Reply(RefreshReply);
            var store = new MemoryStore { Value = "refresh-1" };
            var auth = new RestAuthSession("KEY", store, http);

            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));

            Assert.AreEqual("user-1", auth.UserId);
            Assert.AreEqual("refresh-2", store.Value);
            Assert.AreEqual(1, http.Requests.Count);
            StringAssert.StartsWith("https://securetoken.googleapis.com/v1/token?key=KEY", http.Requests[0].Url);
            StringAssert.Contains("refresh_token=refresh-1", http.Requests[0].Body);
        }

        [Test] public void IdToken_IsCached_UntilItIsAboutToExpire_OrForced()
        {
            var http = new FakeHandler(); http.Reply(SignUpReply); http.Reply(RefreshReply); http.Reply(RefreshReply);
            var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            var auth = new RestAuthSession("KEY", new MemoryStore(), http, () => now);
            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));

            Assert.AreEqual("id-1", Run(auth.GetIdTokenAsync(false, CancellationToken.None)));
            Assert.AreEqual(1, http.Requests.Count, "still fresh, no new request");

            now = now.AddSeconds(3560); // inside the 60 second margin
            Assert.AreEqual("id-2", Run(auth.GetIdTokenAsync(false, CancellationToken.None)));
            Assert.AreEqual(2, http.Requests.Count);

            Run(auth.GetIdTokenAsync(true, CancellationToken.None));
            Assert.AreEqual(3, http.Requests.Count, "forced refresh");
        }

        [Test] public void GetIdToken_BeforeSignIn_Throws()
        {
            var auth = new RestAuthSession("KEY", new MemoryStore(), new FakeHandler());
            Assert.Throws<InvalidOperationException>(() => Run(auth.GetIdTokenAsync(false, CancellationToken.None)));
        }

        [Test] public void SignOut_ForgetsTheUser_AndTheStoredToken()
        {
            var http = new FakeHandler(); http.Reply(SignUpReply);
            var store = new MemoryStore();
            var auth = new RestAuthSession("KEY", store, http);
            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));
            int changed = 0; auth.Changed += () => changed++;

            auth.SignOut();

            Assert.IsFalse(auth.IsSignedIn);
            Assert.IsNull(store.Value);
            Assert.AreEqual(1, changed);
        }

        [Test] public void FailedCall_NamesTheFirebaseError_AndLeaksNoToken()
        {
            var http = new FakeHandler(); http.Reply("{\"error\":{\"message\":\"USER_DISABLED\"}}", HttpStatusCode.BadRequest);
            var store = new MemoryStore { Value = "secret-refresh" };
            var auth = new RestAuthSession("KEY", store, http);

            var e = Assert.Throws<HypaFirebaseException>(() => Run(auth.SignInAnonymouslyAsync(CancellationToken.None)));

            Assert.AreEqual(400, e.StatusCode);
            Assert.AreEqual("USER_DISABLED", e.Message);
            StringAssert.DoesNotContain("secret-refresh", e.ToString());
            Assert.AreEqual("secret-refresh", store.Value, "a disabled user is not replaced by a new one");
        }

        [TestCase("INVALID_REFRESH_TOKEN")]
        [TestCase("TOKEN_EXPIRED")]
        [TestCase("USER_NOT_FOUND")]
        public void DeadSavedToken_IsCleared_AndANewUserSignsUp(string error)
        {
            var http = new FakeHandler();
            http.Reply("{\"error\":{\"message\":\"" + error + "\"}}", HttpStatusCode.BadRequest);
            http.Reply(SignUpReply);
            var store = new MemoryStore { Value = "dead-refresh" };
            var auth = new RestAuthSession("KEY", store, http);

            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));

            Assert.IsTrue(auth.IsSignedIn);
            Assert.AreEqual("user-1", auth.UserId);
            Assert.AreEqual("refresh-1", store.Value);
            Assert.AreEqual(2, http.Requests.Count);
            StringAssert.StartsWith("https://identitytoolkit.googleapis.com/v1/accounts:signUp", http.Requests[1].Url);
        }

        [Test] public void SignOut_DuringARefresh_StaysSignedOut()
        {
            var http = new FakeHandler(); http.Reply(SignUpReply); http.Reply(RefreshReply);
            var store = new MemoryStore();
            var auth = new RestAuthSession("KEY", store, http);
            Run(auth.SignInAnonymouslyAsync(CancellationToken.None));

            http.Hold = new TaskCompletionSource<bool>();
            var refresh = Task.Run(() => auth.GetIdTokenAsync(true, CancellationToken.None));
            SpinWait.SpinUntil(() => http.Requests.Count == 2, 5000);

            auth.SignOut();
            http.Hold.SetResult(true);

            Assert.Throws<InvalidOperationException>(() => Run(refresh));
            Assert.IsFalse(auth.IsSignedIn);
            Assert.IsNull(store.Value, "the refresh that finished late must not save its token");
        }

        [Test] public void AppCheck_ExchangesTheDebugToken_AndCachesTheResult()
        {
            var http = new FakeHandler(); http.Reply("{\"token\":\"ac-1\",\"ttl\":\"3600s\"}"); http.Reply("{\"token\":\"ac-2\",\"ttl\":\"3600s\"}");
            var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            var appCheck = new RestAppCheck("KEY", "proj", "1:123:android:abc", "debug-xyz", http, () => now);

            Assert.AreEqual("ac-1", Run(appCheck.GetTokenAsync(false, CancellationToken.None)));
            Assert.AreEqual("ac-1", Run(appCheck.GetTokenAsync(false, CancellationToken.None)));
            Assert.AreEqual(1, http.Requests.Count);
            Assert.AreEqual("https://firebaseappcheck.googleapis.com/v1/projects/proj/apps/1:123:android:abc:exchangeDebugToken?key=KEY", http.Requests[0].Url);
            Assert.AreEqual("debug-xyz", (string)JObject.Parse(http.Requests[0].Body)["debug_token"]);

            now = now.AddSeconds(3600);
            Assert.AreEqual("ac-2", Run(appCheck.GetTokenAsync(false, CancellationToken.None)));
        }

        [Test] public void AppCheck_Refused_ThrowsWithoutTheDebugToken()
        {
            var http = new FakeHandler(); http.Reply("{\"error\":{\"message\":\"PERMISSION_DENIED\"}}", HttpStatusCode.Forbidden);
            var appCheck = new RestAppCheck("KEY", "proj", "app", "debug-xyz", http);

            var e = Assert.Throws<HypaFirebaseException>(() => Run(appCheck.GetTokenAsync(false, CancellationToken.None)));

            Assert.AreEqual(403, e.StatusCode);
            StringAssert.DoesNotContain("debug-xyz", e.ToString());
        }
    }
}
