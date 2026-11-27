using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HypaFirebase.Rest;

namespace HypaFirebase.Tests
{
    sealed class SentRequest
    {
        public string Method, Url, Body;
        public Dictionary<string, string> Headers;
    }

    /// <summary>A fake network: replies from a queue (or throws), and records what was sent.</summary>
    sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<SentRequest> Requests = new List<SentRequest>();
        readonly Queue<Func<HttpResponseMessage>> _replies = new Queue<Func<HttpResponseMessage>>();

        public StubHandler Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _replies.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });
            return this;
        }

        public StubHandler Fail(Exception e) { _replies.Enqueue(() => throw e); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new SentRequest
            {
                Method = request.Method.Method,
                Url = request.RequestUri.ToString(),
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(),
                Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            });
            return _replies.Dequeue()();
        }
    }

    sealed class MemoryKeyValueStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public string Get(string key) { lock (Values) return Values.TryGetValue(key, out var v) ? v : null; }
        public void Set(string key, string value) { lock (Values) Values[key] = value; }
        public void Remove(string key) { lock (Values) Values.Remove(key); }
    }
}
