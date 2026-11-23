using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// Firebase Auth over REST (Identity Toolkit and Secure Token), for Quest if the Firebase SDK does not run there.
    /// Anonymous sign-in on first launch; the refresh token is kept in the store so the same user comes back later.
    /// ID tokens are fetched on demand and kept only in memory. Nothing here logs a token.
    /// </summary>
    public sealed class RestAuthSession : IAuthSession
    {
        const string SignUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=";
        const string RefreshUrl = "https://securetoken.googleapis.com/v1/token?key=";
        static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

        readonly string _apiKey;
        readonly IRefreshTokenStore _store;
        readonly HttpClient _http;
        readonly Func<DateTime> _now;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        string _idToken;
        string _refreshToken;
        DateTime _expiresAt;

        public string UserId { get; private set; }
        public bool IsSignedIn => !string.IsNullOrEmpty(UserId);
        public event Action Changed;

        /// <param name="apiKey">The Firebase web API key. It is public client configuration, not a secret.</param>
        public RestAuthSession(string apiKey, IRefreshTokenStore store, HttpMessageHandler handler = null, Func<DateTime> now = null)
        {
            _apiKey = apiKey;
            _store = store;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _now = now ?? (() => DateTime.UtcNow);
        }

        public async Task SignInAnonymouslyAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _refreshToken = _store.Load();
                if (!string.IsNullOrEmpty(_refreshToken)) await RefreshAsync(cancellationToken).ConfigureAwait(false);
                else await SignUpAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task<string> GetIdTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsSignedIn) throw new InvalidOperationException("Not signed in.");
                if (forceRefresh || _now() + RefreshMargin >= _expiresAt) await RefreshAsync(cancellationToken).ConfigureAwait(false);
                return _idToken;
            }
            finally { _gate.Release(); }
        }

        public void SignOut()
        {
            _idToken = null;
            _refreshToken = null;
            UserId = null;
            _store.Clear();
            Changed?.Invoke();
        }

        async Task SignUpAsync(CancellationToken ct)
        {
            var content = new StringContent("{\"returnSecureToken\":true}", Encoding.UTF8, "application/json");
            var body = await PostAsync(SignUpUrl + _apiKey, content, ct).ConfigureAwait(false);
            Apply((string)body["idToken"], (string)body["refreshToken"], (string)body["expiresIn"], (string)body["localId"]);
        }

        async Task RefreshAsync(CancellationToken ct)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "refresh_token" }, { "refresh_token", _refreshToken } });
            var body = await PostAsync(RefreshUrl + _apiKey, form, ct).ConfigureAwait(false);
            Apply((string)body["id_token"], (string)body["refresh_token"], (string)body["expires_in"], (string)body["user_id"]);
        }

        void Apply(string idToken, string refreshToken, string expiresInSeconds, string userId)
        {
            bool changed = userId != UserId;
            _idToken = idToken;
            _refreshToken = refreshToken;
            _expiresAt = _now() + TimeSpan.FromSeconds(double.Parse(expiresInSeconds, CultureInfo.InvariantCulture));
            UserId = userId;
            _store.Save(refreshToken);
            if (changed) Changed?.Invoke();
        }

        async Task<JObject> PostAsync(string url, HttpContent content, CancellationToken ct)
        {
            using (var res = await _http.PostAsync(url, content, ct).ConfigureAwait(false))
            {
                var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, ErrorCode(text));
                return JObject.Parse(text);
            }
        }

        /// <summary>The code Firebase sent (for example INVALID_REFRESH_TOKEN), or a generic line. Never echoes the request.</summary>
        internal static string ErrorCode(string body)
        {
            try { return (string)JObject.Parse(body)["error"]?["message"] ?? "Firebase request failed."; }
            catch (Exception) { return "Firebase request failed."; }
        }
    }
}
