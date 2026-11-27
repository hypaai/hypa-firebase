using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

namespace HypaCore
{
    /// <summary>
    /// The only code in the app that touches the network. Uses HttpClient so requests and response parsing run off
    /// the Unity main thread (continuations use ConfigureAwait(false)); callers awaiting from the main thread resume there.
    /// </summary>
    public static class HypaHttp
    {
        // No client-wide timeout: each request gets its own (see Send), because Hypa's speech can take far longer than a
        // translation, and running out of time is not the same as being offline.
        static readonly HttpClient Client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        static readonly Lazy<HttpClient> ProxyClient = new Lazy<HttpClient>(() =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan AudioDownloadTimeout = TimeSpan.FromSeconds(60);

        // An incomplete proxy task must resume on Unity's main thread in WebGL.
#if UNITY_WEBGL && !UNITY_EDITOR
        internal const bool CaptureProxyContext = true;
#else
        internal const bool CaptureProxyContext = false;
#endif

        /// <summary>POST JSON to the Hypa API. Calls the proxy without a Hypa API key. Returns the raw body (JSON or SSE).</summary>
        public static Task<string> PostAsync(HypaConfig cfg, string path, string json, CancellationToken ct, TimeSpan? timeout = null)
            => PostAsync(cfg, path, json, ct, timeout ?? DefaultTimeout, null, null);

        /// <summary>Send a G01 request with Firebase credentials. The proxy owns the Hypa key.</summary>
        public static async Task<string> PostProxyAsync(HypaConfig cfg, string path, string json, CancellationToken ct)
        {
            if (!cfg.HasProxy)
                throw new HypaException(HypaErrorKind.MissingConfiguration, "The Hypa proxy is not configured.");

            var auth = HybridServices.Current.Auth;
            if (auth is FakeAuth)
                throw new HypaException(HypaErrorKind.MissingConfiguration, "Firebase sign-in is not configured for the Hypa proxy.");

            string idToken;
            string appCheck;
            try
            {
                if (string.IsNullOrEmpty(auth.Uid)) await auth.SignInAnonymouslyAsync();
                idToken = await auth.IdTokenAsync();
                appCheck = await auth.AppCheckTokenAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                throw new HypaException(HypaErrorKind.Unauthorized, "Firebase credentials are unavailable.", inner: e);
            }
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(idToken))
                throw new HypaException(HypaErrorKind.Unauthorized, "Sign in to call the Hypa proxy.");

#if UNITY_WEBGL && !UNITY_EDITOR
            using (var req = new UnityWebRequest(cfg.BaseUrl + path, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 60;
                req.redirectLimit = 0;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader(ProxyApi.AuthorizationHeader, "Bearer " + idToken);
                if (!string.IsNullOrEmpty(appCheck)) req.SetRequestHeader(ProxyApi.AppCheckHeader, appCheck);
                var op = req.SendWebRequest();
                if (!op.isDone)
                {
                    var done = new TaskCompletionSource<bool>();
                    op.completed += _ => done.TrySetResult(true);
                    using (ct.Register(() => { req.Abort(); done.TrySetCanceled(ct); }))
                        await done.Task;
                }
                ct.ThrowIfCancellationRequested();
                var body = req.downloadHandler.text;
                if (req.result == UnityWebRequest.Result.ConnectionError)
                    throw new HypaException(HypaErrorKind.Offline, req.error);
                if (req.result != UnityWebRequest.Result.Success)
                    throw HypaException.FromHttp((int)req.responseCode, body);
                return body;
            }
#else
            return await PostAsync(cfg, path, json, ct, TimeSpan.FromSeconds(60), idToken, appCheck).ConfigureAwait(false);
#endif
        }

        static async Task<string> PostAsync(HypaConfig cfg, string path, string json, CancellationToken ct, TimeSpan timeout, string idToken, string appCheck)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl + path))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                if (!cfg.HasProxy) throw new HypaException(HypaErrorKind.MissingConfiguration, "The Hypa proxy is not configured.");
                if (!string.IsNullOrEmpty(idToken)) req.Headers.TryAddWithoutValidation(ProxyApi.AuthorizationHeader, "Bearer " + idToken);
                if (!string.IsNullOrEmpty(appCheck)) req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, appCheck);
                var client = string.IsNullOrEmpty(idToken) ? Client : ProxyClient.Value;
                using (var res = await Send(client, req, ct, timeout).ConfigureAwait(false))
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw HypaException.FromHttp((int)res.StatusCode, body);
                    return body;
                }
            }
        }

        /// <summary>GET bytes from a signed URL (TTS audio). Never sends the API key to it.</summary>
        public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
        {
            // the link's host only: its query string signs it
            var trace = HypaTrace.Enabled ? HypaTrace.Begin("audio download", Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "") : null;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var res = await Send(Client, req, ct, AudioDownloadTimeout).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode)
                        throw new HypaException(HypaErrorKind.BadRequest, $"Audio link returned HTTP {(int)res.StatusCode} (links expire after about an hour).", (int)res.StatusCode);
                    var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    HypaTrace.End(trace, "ok", $"{bytes.Length / 1024} KB");
                    return bytes;
                }
            }
            catch (OperationCanceledException) { HypaTrace.End(trace, "cancelled"); throw; }
            catch (Exception e) { HypaTrace.End(trace, e is HypaException h ? h.Kind.ToString() : e.GetType().Name, e.Message); throw; }
        }

        static async Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage req, CancellationToken ct, TimeSpan timeout)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(timeout);
                try { return await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException e) { throw new HypaException(HypaErrorKind.Timeout, $"Request timed out after {timeout.TotalSeconds:0} s.", 0, null, e); }
                catch (HttpRequestException e) { throw new HypaException(HypaErrorKind.Offline, e.Message, 0, null, e); }
            }
        }

        /// <summary>
        /// Hypa answers either with a JSON body or with server-sent events ("event: message\ndata: {...}\n\n").
        /// Returns each JSON payload in order. "[DONE]" markers are skipped. Pure, unit tested.
        /// </summary>
        public static List<string> JsonPayloads(string body)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(body)) return list;
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("[")) { list.Add(trimmed); return list; }

            var data = new StringBuilder();
            void Flush()
            {
                if (data.Length == 0) return;
                var s = data.ToString();
                if (s != "[DONE]") list.Add(s);
                data.Clear();
            }
            foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Length == 0) { Flush(); continue; }
                if (!raw.StartsWith("data:")) continue;
                var v = raw.Substring(5);
                if (v.StartsWith(" ")) v = v.Substring(1);
                if (data.Length > 0) data.Append('\n');
                data.Append(v);
            }
            Flush();
            return list;
        }
    }
}
