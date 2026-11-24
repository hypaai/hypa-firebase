using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// The Hypa proxy's client (docs/contracts.md, section 6): Cloud Functions that hold the Hypa key and call Hypa for the app.
    /// Every call carries the player's Firebase ID token and App Check token; tokens are fetched per call and never logged.
    /// No Hypa key ever reaches the client. Failures throw <see cref="HypaProxyException"/> with the contract's error code.
    /// </summary>
    public sealed class HypaProxyClient
    {
        static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        readonly string _baseUrl;
        readonly IHypaAuth _auth;
        readonly HttpClient _http;

        /// <param name="baseUrl">The functions' origin, e.g. https://REGION-PROJECT.cloudfunctions.net or the Hosting rewrite origin.</param>
        public HypaProxyClient(string baseUrl, IHypaAuth auth, HttpMessageHandler handler = null)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("The Hypa proxy URL is not configured.", nameof(baseUrl));
            _baseUrl = baseUrl.TrimEnd('/');
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _http = handler != null ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) } : SharedHttp;
        }

        /// <summary>The proxy from the app's configuration (<see cref="HypaConfig.BaseUrl"/>) and the current account.</summary>
        public static HypaProxyClient FromConfig() => new HypaProxyClient(HypaConfig.Current.BaseUrl, HybridServices.Current.Auth);

        public Task<TtsResponse> TtsAsync(TtsRequest body, CancellationToken ct) => PostAsync<TtsResponse>(ProxyApi.Tts, body, ct);
        public Task<AsrResponse> AsrAsync(AsrRequest body, CancellationToken ct) => PostAsync<AsrResponse>(ProxyApi.Asr, body, ct);
        public Task<ChatResponse> ChatAsync(ChatRequest body, CancellationToken ct) => PostAsync<ChatResponse>(ProxyApi.Chat, body, ct);
        public Task<TranslateResponse> TranslateAsync(TranslateRequest body, CancellationToken ct) => PostAsync<TranslateResponse>(ProxyApi.Translate, body, ct);

        async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
        {
            string idToken, appCheck;
            try
            {
                idToken = await _auth.IdTokenAsync().ConfigureAwait(false);
                appCheck = await _auth.AppCheckTokenAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw new HypaProxyException(ProxyApi.ErrorUnauthenticated, "Not signed in: " + e.Message, 0);
            }
            using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path))
            {
                req.Headers.TryAddWithoutValidation(ProxyApi.AuthorizationHeader, "Bearer " + idToken);
                req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, appCheck);
                req.Content = new StringContent(JsonConvert.SerializeObject(body, Json), Encoding.UTF8, "application/json");
                HttpResponseMessage res;
                try { res = await _http.SendAsync(req, ct).ConfigureAwait(false); }
                catch (HttpRequestException e) { throw new HypaProxyException(ProxyApi.ErrorUpstream, "The proxy could not be reached: " + e.Message, 0); }
                using (res)
                {
                    var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw HypaProxyException.FromResponse((int)res.StatusCode, text);
                    try { return JsonConvert.DeserializeObject<T>(text); }
                    catch (JsonException) { throw new HypaProxyException(ProxyApi.ErrorUpstream, path + ": the proxy's answer was not JSON.", (int)res.StatusCode); }
                }
            }
        }
    }

    /// <summary>A proxy failure. <see cref="Code"/> is one of the <c>ProxyApi.Error*</c> codes.</summary>
    public sealed class HypaProxyException : Exception
    {
        public string Code { get; }
        public int Status { get; }
        /// <summary>The error's details, when the proxy sent them: <c>scope</c> for quota, <c>reason</c> for upstream.</summary>
        public ProxyErrorBody Details { get; }

        public HypaProxyException(string code, string message, int status, ProxyErrorBody details = null) : base(message)
        {
            Code = code; Status = status; Details = details;
        }

        /// <summary>Reads <c>{ "error": { "code", "message", ... } }</c>; anything else is an upstream failure.</summary>
        public static HypaProxyException FromResponse(int status, string body)
        {
            try
            {
                var error = JObject.Parse(body)["error"]?.ToObject<ProxyErrorBody>();
                if (!string.IsNullOrEmpty(error?.code)) return new HypaProxyException(error.code, error.message ?? error.code, status, error);
            }
            catch (JsonException) { }
            return new HypaProxyException(status == 401 ? ProxyApi.ErrorUnauthenticated : ProxyApi.ErrorUpstream, "The proxy answered " + status + ".", status);
        }

        /// <summary>Worth offering a retry: the proxy's own answer when it gave one, otherwise any upstream failure.</summary>
        public bool Retryable => Details?.retryable ?? Code == ProxyApi.ErrorUpstream;

        /// <summary>Seconds to wait before trying again (quota), or null.</summary>
        public int? RetryAfterSeconds => Details?.retryAfterSeconds;
    }
}
