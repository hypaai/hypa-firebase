using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// <see cref="IProgressStore"/> for Quest, over the Firestore REST API (<c>users/{uid}/progress/current</c>).
    /// REST has no offline cache, so this keeps its own: a save is stored locally first and sent when the network allows,
    /// and a failed send stays queued for the next save or load. <see cref="SaveAsync"/> therefore never fails because of the network.
    /// REST has no live listeners, so <see cref="ChangedElsewhere"/> is never raised; poll <see cref="LoadAsync"/> instead.
    /// </summary>
    public sealed class RestProgressStore : IProgressStore
    {
        public const string LocalKey = "hypa.progress.current";
        public const string DirtyKey = "hypa.progress.dirty";

        readonly FirebaseRestConfig _config;
        readonly IHypaAuth _auth;
        readonly IKeyValueStore _local;
        readonly HttpClient _http;
        // One send at a time, so an older save can never land on the server after a newer one.
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        // Bumped by every save. A send clears the queue only if no save came in while it was running.
        int _version;

        public event Action<Progress> ChangedElsewhere { add { } remove { } }

        public RestProgressStore(FirebaseRestConfig config, IHypaAuth auth, IKeyValueStore local, HttpMessageHandler handler = null)
        {
            _config = config;
            _auth = auth;
            _local = local;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        bool Dirty => _local.Get(DirtyKey) == "1";

        public async Task<Progress> LoadAsync()
        {
            await TrySendAsync().ConfigureAwait(false);
            if (Dirty) return ReadLocal(); // the local copy is newer than the server's
            try
            {
                var remote = await GetRemoteAsync().ConfigureAwait(false);
                if (remote != null) { WriteLocal(remote); return remote; }
            }
            catch (Exception e) when (IsOffline(e)) { }
            return ReadLocal();
        }

        public async Task SaveAsync(Progress p)
        {
            WriteLocal(p);
            _local.Set(DirtyKey, "1");
            Interlocked.Increment(ref _version); // after the write, so a send that sees this version also sees this progress
            await TrySendAsync().ConfigureAwait(false);
        }

        async Task TrySendAsync()
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try { await SendQueuedAsync().ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }

        async Task SendQueuedAsync()
        {
            if (!Dirty || _auth.Uid == null) return;
            int version = Volatile.Read(ref _version);
            var progress = ReadLocal();
            if (progress == null) return;
            try
            {
                using (var req = await NewRequestAsync(new HttpMethod("PATCH"), DocumentUrl()).ConfigureAwait(false))
                {
                    req.Content = new StringContent(new JObject { ["fields"] = ToFields(progress) }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "Firestore refused the save.");
                    }
                }
                // A save that came in meanwhile stays queued; its own send (waiting on the gate) sends it next.
                if (version == Volatile.Read(ref _version)) _local.Remove(DirtyKey);
            }
            catch (Exception e) when (IsOffline(e))
            {
                // Stays queued. A refusal from Firestore (for example 403) is worth seeing; the status is not sensitive.
                if (e is HypaFirebaseException refused) UnityEngine.Debug.LogWarning($"Hypa: progress not sent, Firestore answered {refused.StatusCode}. It stays queued.");
            }
        }

        async Task<Progress> GetRemoteAsync()
        {
            if (_auth.Uid == null) return null;
            using (var req = await NewRequestAsync(HttpMethod.Get, DocumentUrl()).ConfigureAwait(false))
            using (var res = await _http.SendAsync(req).ConfigureAwait(false))
            {
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "Firestore refused the read.");
                var body = JObject.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
                return FromFields((JObject)body["fields"]);
            }
        }

        string DocumentUrl()
            => $"https://firestore.googleapis.com/v1/projects/{_config.ProjectId}/databases/(default)/documents/users/{_auth.Uid}/progress/current";

        async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.IdTokenAsync().ConfigureAwait(false));
            req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, await _auth.AppCheckTokenAsync().ConfigureAwait(false));
            return req;
        }

        Progress ReadLocal()
        {
            var json = _local.Get(LocalKey);
            return string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<Progress>(json);
        }

        void WriteLocal(Progress p) => _local.Set(LocalKey, JsonConvert.SerializeObject(p));

        // A network or server problem keeps the save queued. Anything else is a bug and should surface.
        static bool IsOffline(Exception e) => e is HttpRequestException || e is HypaFirebaseException || e is TaskCanceledException;

        // Firestore REST wraps every value in its type.
        static JObject ToFields(Progress p)
        {
            var fields = new JObject();
            void Text(string name, string value) { if (value != null) fields[name] = new JObject { ["stringValue"] = value }; }
            void Number(string name, long value) => fields[name] = new JObject { ["integerValue"] = value.ToString(CultureInfo.InvariantCulture) };
            Text("storyId", p.storyId);
            Text("lineId", p.lineId);
            Number("lineIndex", p.lineIndex);
            Text("language", p.language);
            Text("place", p.place);
            Number("updatedAt", p.updatedAt);
            Text("device", p.device);
            return fields;
        }

        static Progress FromFields(JObject fields)
        {
            string Text(string name) => (string)fields?[name]?["stringValue"];
            long Number(string name) => long.Parse((string)fields?[name]?["integerValue"] ?? "0", CultureInfo.InvariantCulture);
            return new Progress
            {
                storyId = Text("storyId"),
                lineId = Text("lineId"),
                lineIndex = (int)Number("lineIndex"),
                language = Text("language"),
                place = Text("place"),
                updatedAt = Number("updatedAt"),
                device = Text("device"),
            };
        }
    }
}
