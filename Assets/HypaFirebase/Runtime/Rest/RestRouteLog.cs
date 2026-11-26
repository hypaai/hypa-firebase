using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    public sealed class RestRouteLog : IRouteLog
    {
        public const int MaxQueued = 50;

        readonly FirebaseRestConfig _config;
        readonly IHypaAuth _auth;
        readonly HttpClient _http;
        readonly Queue<RouteEvent> _queue = new Queue<RouteEvent>();
        bool _sending;
        Task _flush = Task.CompletedTask;

        public RestRouteLog(FirebaseRestConfig config, IHypaAuth auth, HttpMessageHandler handler = null)
        {
            _config = config;
            _auth = auth;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public int Queued { get { lock (_queue) return _queue.Count; } }
        public Task Pending { get { lock (_queue) return _flush; } }

        public void Log(RouteEvent e)
        {
            if (e == null) return;
            lock (_queue)
            {
                _queue.Enqueue(e);
                while (_queue.Count > MaxQueued) _queue.Dequeue();
            }
            Flush();
        }

        public Task Flush()
        {
            lock (_queue)
            {
                if (_sending) return _flush;
                _sending = true;
                return _flush = SendQueuedAsync();
            }
        }

        async Task SendQueuedAsync()
        {
            try
            {
                while (true)
                {
                    RouteEvent next;
                    lock (_queue)
                    {
                        if (_queue.Count == 0 || _auth.Uid == null) return;
                        next = _queue.Peek();
                    }
                    try { await SendAsync(next).ConfigureAwait(false); }
                    catch (Exception e) when (Retry(e)) { return; }
                    catch (HypaFirebaseException e) { UnityEngine.Debug.LogWarning($"Hypa: a route event was refused ({e.StatusCode}) and dropped."); }
                    lock (_queue)
                    {
                        if (_queue.Count > 0 && ReferenceEquals(_queue.Peek(), next)) _queue.Dequeue();
                    }
                }
            }
            finally
            {
                lock (_queue) _sending = false;
            }
        }

        static bool Retry(Exception e)
            => e is HttpRequestException || e is TaskCanceledException
               || (e is HypaFirebaseException f && (f.StatusCode >= 500 || f.StatusCode == 429));

        async Task SendAsync(RouteEvent e)
        {
            var url = $"https://firestore.googleapis.com/v1/projects/{_config.ProjectId}/databases/(default)/documents/routes/{_auth.Uid}/events?documentId={Guid.NewGuid():N}";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.IdTokenAsync().ConfigureAwait(false));
                req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, await _auth.AppCheckTokenAsync().ConfigureAwait(false));
                req.Content = new StringContent(new JObject { ["fields"] = Fields(e) }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "Firestore refused the route event.");
                }
            }
        }

        public static JObject Fields(RouteEvent e)
        {
            var fields = new JObject();
            void Text(string name, string value) { if (value != null) fields[name] = new JObject { ["stringValue"] = value }; }
            void Number(string name, long value) => fields[name] = new JObject { ["integerValue"] = value.ToString(CultureInfo.InvariantCulture) };
            Text("kind", e.kind);
            Text("route", e.route);
            Text("model", e.model);
            Text("language", e.language);
            Number("latencyMs", e.latencyMs);
            fields["ok"] = new JObject { ["booleanValue"] = e.ok };
            Text("fallbackFrom", e.fallbackFrom);
            Text("device", e.device);
            Number("at", e.at);
            return fields;
        }
    }
}
