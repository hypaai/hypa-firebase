using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// App Check with the debug provider over REST, for Quest and the editor (no Play Integrity or App Attest there).
    /// Exchanges a debug token registered in the Firebase console for an App Check token, kept in memory only.
    /// Send the result as the X-Firebase-AppCheck header. Nothing here logs a token.
    /// </summary>
    public sealed class RestAppCheck
    {
        static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

        readonly string _exchangeUrl;
        readonly string _debugToken;
        readonly HttpClient _http;
        readonly Func<DateTime> _now;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        string _token;
        DateTime _expiresAt;

        /// <param name="appId">The Firebase app id (public client configuration).</param>
        /// <param name="debugToken">Registered in the console. Never log it.</param>
        public RestAppCheck(string apiKey, string projectId, string appId, string debugToken, HttpMessageHandler handler = null, Func<DateTime> now = null)
        {
            _exchangeUrl = $"https://firebaseappcheck.googleapis.com/v1/projects/{projectId}/apps/{appId}:exchangeDebugToken?key={apiKey}";
            _debugToken = debugToken;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _now = now ?? (() => DateTime.UtcNow);
        }

        public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && _token != null && _now() + RefreshMargin < _expiresAt) return _token;
                var json = new JObject { ["debug_token"] = _debugToken }.ToString(Formatting.None);
                using (var res = await _http.PostAsync(_exchangeUrl, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false))
                {
                    var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, RestAuthSession.ErrorCode(text));
                    var body = JObject.Parse(text);
                    _token = (string)body["token"];
                    // ttl is a duration string such as "3600s".
                    var ttl = ((string)body["ttl"] ?? "3600s").TrimEnd('s');
                    _expiresAt = _now() + TimeSpan.FromSeconds(double.Parse(ttl, CultureInfo.InvariantCulture));
                    return _token;
                }
            }
            finally { _gate.Release(); }
        }
    }
}
