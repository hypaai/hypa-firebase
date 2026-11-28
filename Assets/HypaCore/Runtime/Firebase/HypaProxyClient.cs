using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

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
        readonly bool _inBrowser;

        /// <param name="baseUrl">The functions' origin, e.g. https://REGION-PROJECT.cloudfunctions.net or the Hosting rewrite origin.</param>
        public HypaProxyClient(string baseUrl, IHypaAuth auth, HttpMessageHandler handler = null)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("The Hypa proxy URL is not configured.", nameof(baseUrl));
            _baseUrl = baseUrl.TrimEnd('/');
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _http = handler != null ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) } : SharedHttp;
            _inBrowser = handler == null && UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WebGLPlayer;
        }

        /// <summary>The proxy from the app's configuration (<see cref="HypaConfig.BaseUrl"/>) and the current account.</summary>
        public static HypaProxyClient FromConfig() => new HypaProxyClient(HypaConfig.Current.BaseUrl, HybridServices.Current.Auth);

        public Task<TtsResponse> TtsAsync(TtsRequest body, CancellationToken ct)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var inlineBody = JObject.FromObject(body, JsonSerializer.Create(Json));
            inlineBody["inline"] = true;
            return PostAsync<TtsResponse>(ProxyApi.Tts, inlineBody, ct);
#else
            return PostAsync<TtsResponse>(ProxyApi.Tts, body, ct);
#endif
        }
        public Task<AsrResponse> AsrAsync(AsrRequest body, CancellationToken ct) => PostAsync<AsrResponse>(ProxyApi.Asr, body, ct);
        public Task<ChatResponse> ChatAsync(ChatRequest body, CancellationToken ct) => PostAsync<ChatResponse>(ProxyApi.Chat, body, ct);
        public Task<TranslateResponse> TranslateAsync(TranslateRequest body, CancellationToken ct) => PostAsync<TranslateResponse>(ProxyApi.Translate, body, ct);

        public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
        {
            AsrResponse r;
            var request = new AsrRequest { audio = Convert.ToBase64String(wav ?? new byte[0]), sourceLang = string.IsNullOrEmpty(language) ? null : language };
            try { r = await AsrAsync(request, ct); }
            catch (HypaProxyException e) { throw e.ToHypaException(); }
            return r?.transcript?.Trim() ?? "";
        }

        public async Task<HypaSpeech> SpeechAsync(string text, string language, string voice, string model, CancellationToken ct)
        {
            TtsResponse r;
            var request = new TtsRequest
            {
                text = text, language = language, voice = string.IsNullOrEmpty(voice) ? null : voice, model = string.IsNullOrEmpty(model) ? null : model,
                inline = _inBrowser ? true : (bool?)null,
            };
            try { r = await TtsAsync(request, ct); }
            catch (HypaProxyException e) { throw e.ToHypaException(); }
            return new HypaSpeech
            {
                AudioUrl = r?.audioUrl, AudioBase64 = r?.audioBase64, Mime = r?.mime, SampleRate = r?.sampleRate ?? 0,
                Voice = r?.route?.voice, Provider = r?.route?.provider, Model = r?.route?.model,
            };
        }

        async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
        {
            // No ConfigureAwait(false) on the browser path: a WebGL build has no thread pool, so a continuation sent there
            // never runs and the request is never made. The tokens come from the main thread, so the awaits resume there.
            string idToken, appCheck;
            try
            {
                idToken = await _auth.IdTokenAsync();
                appCheck = await _auth.AppCheckTokenAsync();
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw new HypaProxyException(ProxyApi.ErrorUnauthenticated, "Not signed in: " + e.Message, 0);
            }
            var json = JsonConvert.SerializeObject(body, Json);
            var (status, text) = _inBrowser
                ? await BrowserPostAsync(_baseUrl + path, json, idToken, appCheck, ct)
                : await HttpPostAsync(_baseUrl + path, json, idToken, appCheck, ct).ConfigureAwait(false);
            if (status < 200 || status > 299) throw HypaProxyException.FromResponse(status, text);
            try { return JsonConvert.DeserializeObject<T>(text); }
            catch (JsonException) { throw new HypaProxyException(ProxyApi.ErrorUpstream, path + ": the proxy's answer was not JSON.", status); }
        }

        /// <summary>
        /// Sent twice at most: a request that fails while being sent (Mono's "An error occurred while sending the request") is
        /// usually a pooled connection the server closed while it sat idle, so it never reached the proxy and is sent again.
        /// </summary>
        async Task<(int, string)> HttpPostAsync(string url, string json, string idToken, string appCheck, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation(ProxyApi.AuthorizationHeader, "Bearer " + idToken);
                    req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, appCheck);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    HttpResponseMessage res;
                    try { res = await _http.SendAsync(req, ct).ConfigureAwait(false); }
                    catch (HttpRequestException) when (attempt < 2 && !ct.IsCancellationRequested) { continue; }
                    catch (HttpRequestException e) { throw new HypaProxyException(ProxyApi.ErrorUpstream, "The proxy could not be reached: " + e.Message, 0); }
                    using (res) return ((int)res.StatusCode, await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }

        static async Task<(int, string)> BrowserPostAsync(string url, string json, string idToken, string appCheck, CancellationToken ct)
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader(ProxyApi.AuthorizationHeader, "Bearer " + idToken);
                req.SetRequestHeader(ProxyApi.AppCheckHeader, appCheck);
                req.timeout = 60;
                var done = new TaskCompletionSource<bool>();
                req.SendWebRequest().completed += _ => done.TrySetResult(true);
                using (ct.Register(() => { req.Abort(); done.TrySetCanceled(); }))
                    await done.Task;
                if (req.result == UnityWebRequest.Result.ConnectionError)
                    throw new HypaProxyException(ProxyApi.ErrorUpstream, "The proxy could not be reached: " + req.error, 0);
                return ((int)req.responseCode, req.downloadHandler.text);
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

        public HypaException ToHypaException()
        {
            HypaErrorKind kind;
            switch (Code)
            {
                case ProxyApi.ErrorUnauthenticated:
                case ProxyApi.ErrorAppCheck: kind = HypaErrorKind.Unauthorized; break;
                case ProxyApi.ErrorQuota: kind = HypaErrorKind.RateLimited; break;
                case ProxyApi.ErrorBadRequest:
                case ProxyApi.ErrorTooLarge:
                case ProxyApi.ErrorNotFound:
                case ProxyApi.ErrorMethodNotAllowed: kind = HypaErrorKind.BadRequest; break;
                // tool_error: Hypa ran and refused (e.g. a voice the model doesn't have), not a passing failure
                case ProxyApi.ErrorUpstream: kind = Status == 0 ? HypaErrorKind.Offline : Details?.reason == "tool_error" ? HypaErrorKind.ToolError : HypaErrorKind.Server; break;
                default: kind = HypaErrorKind.Server; break;
            }
            return new HypaException(kind, Message, Status, Code, this);
        }
    }
}
