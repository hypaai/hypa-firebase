using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;

namespace HypaFirebase.Rest
{
    /// <summary>One server-sent event from the Realtime Database: Name is "put", "patch", "keep-alive", "cancel" or "auth_revoked".</summary>
    public sealed class RtdbEvent
    {
        public string Name { get; }
        public string Data { get; }
        public RtdbEvent(string name, string data) { Name = name; Data = data; }
    }

    /// <summary>
    /// A small Realtime Database client over REST and server-sent events, for Quest if the SDK does not run there (G09 builds the
    /// room logic on top). REST has no <c>onDisconnect</c>: presence cannot clear itself when a player drops, so it must time
    /// out on the reading side (for example a heartbeat in <c>players/{uid}</c>). Events arrive on a thread-pool thread.
    /// The ID token goes in the <c>auth</c> query parameter (the REST API has no header for it) and is never logged.
    /// </summary>
    public sealed class RestRealtimeDatabase
    {
        readonly FirebaseRestConfig _config;
        readonly IHypaAuth _auth;
        readonly HttpClient _http;

        public RestRealtimeDatabase(FirebaseRestConfig config, IHypaAuth auth, HttpMessageHandler handler = null)
        {
            _config = config;
            _auth = auth;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = Timeout.InfiniteTimeSpan; // the stream stays open; each request below brings its own limit
        }

        /// <summary>The JSON at a path ("null" if there is nothing there).</summary>
        public async Task<string> GetAsync(string path, CancellationToken ct)
        {
            using (var req = await NewRequestAsync(HttpMethod.Get, path, ct).ConfigureAwait(false))
                return await SendAsync(req, ct).ConfigureAwait(false);
        }

        /// <summary>Replaces what is at a path.</summary>
        public Task SetAsync(string path, string json, CancellationToken ct) => WriteAsync(HttpMethod.Put, path, json, ct);

        /// <summary>Changes only the fields in the JSON.</summary>
        public Task UpdateAsync(string path, string json, CancellationToken ct) => WriteAsync(new HttpMethod("PATCH"), path, json, ct);

        public async Task DeleteAsync(string path, CancellationToken ct)
        {
            using (var req = await NewRequestAsync(HttpMethod.Delete, path, ct).ConfigureAwait(false))
                await SendAsync(req, ct).ConfigureAwait(false);
        }

        /// <summary>Events as the server sends them, until cancelled or the connection ends.</summary>
        public async IAsyncEnumerable<RtdbEvent> StreamAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            using (var req = await NewRequestAsync(HttpMethod.Get, path, ct).ConfigureAwait(false))
            {
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                using (var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "The Realtime Database refused the stream.");
                    using (var reader = new StreamReader(await res.Content.ReadAsStreamAsync().ConfigureAwait(false), Encoding.UTF8))
                    {
                        var parser = new SseParser();
                        string line;
                        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            ct.ThrowIfCancellationRequested();
                            var evt = parser.Feed(line);
                            if (evt != null) yield return evt;
                        }
                    }
                }
            }
        }

        async Task WriteAsync(HttpMethod method, string path, string json, CancellationToken ct)
        {
            using (var req = await NewRequestAsync(method, path, ct).ConfigureAwait(false))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                await SendAsync(req, ct).ConfigureAwait(false);
            }
        }

        async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using (var res = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false))
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, RestAuthSession.ErrorCode(body));
                    return body;
                }
            }
        }

        async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string path, CancellationToken ct)
        {
            var token = await _auth.IdTokenAsync().ConfigureAwait(false);
            var req = new HttpRequestMessage(method, $"{_config.DatabaseUrl}/{path.Trim('/')}.json?auth={Uri.EscapeDataString(token)}");
            req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, await _auth.AppCheckTokenAsync().ConfigureAwait(false));
            return req;
        }

        /// <summary>Turns lines of an event stream into events: "event: x", "data: y", then a blank line.</summary>
        public sealed class SseParser
        {
            string _name;
            readonly StringBuilder _data = new StringBuilder();

            /// <summary>Feed one line at a time. Returns an event when a blank line ends one, otherwise null.</summary>
            public RtdbEvent Feed(string line)
            {
                if (line.Length == 0)
                {
                    if (_name == null && _data.Length == 0) return null;
                    var evt = new RtdbEvent(_name ?? "message", _data.ToString());
                    _name = null;
                    _data.Clear();
                    return evt;
                }
                if (line.StartsWith("event:", StringComparison.Ordinal)) _name = line.Substring(6).Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (_data.Length > 0) _data.Append('\n');
                    _data.Append(line.Substring(5).TrimStart());
                }
                return null;
            }
        }
    }
}
