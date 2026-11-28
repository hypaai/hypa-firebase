using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// The Gemini Live API through Firebase AI Logic over a plain WebSocket, for Android, iOS, Quest and the editor (no Firebase
    /// Unity SDK in this project). Same address and messages as the JS SDK (<see cref="LiveProtocol"/>): the Firebase web API key in
    /// the URL; the App Check and ID tokens are sent as headers too, which a browser cannot do. Nothing here logs a token or the key.
    /// </summary>
    public sealed class RestLiveModel : ILiveModel
    {
        readonly FirebaseRestConfig _config;
        readonly IHypaAuth _auth;

        public RestLiveModel(FirebaseRestConfig config, IHypaAuth auth) { _config = config; _auth = auth; }

        public async Task<ILiveSession> ConnectAsync(LiveSetup setup, CancellationToken ct)
        {
            try { return await ConnectAsync(setup, true, ct).ConfigureAwait(false); }
            catch (HypaFirebaseException)
            {
                // the browser can't send these headers, so the server never needs them: try once as the browser does
                return await ConnectAsync(setup, false, ct).ConfigureAwait(false);
            }
        }

        async Task<ILiveSession> ConnectAsync(LiveSetup setup, bool tokens, CancellationToken ct)
        {
            var ws = new ClientWebSocket();
            if (tokens)
            {
                try { ws.Options.SetRequestHeader(ProxyApi.AppCheckHeader, await _auth.AppCheckTokenAsync().ConfigureAwait(false)); }
                catch (Exception e) when (!(e is OperationCanceledException)) { } // App Check is best effort here, as in the browser
                try { ws.Options.SetRequestHeader("Authorization", "Firebase " + await _auth.IdTokenAsync().ConfigureAwait(false)); }
                catch (Exception e) when (!(e is OperationCanceledException)) { }
            }
            ws.Options.SetRequestHeader("x-goog-api-client", "gl-unity-rest fire-unity-rest");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    await ws.ConnectAsync(new Uri(LiveProtocol.Url(_config.ApiKey)), timeout.Token).ConfigureAwait(false);
                    await RestLiveSession.SendAsync(ws, LiveProtocol.Setup(_config.ProjectId, setup), timeout.Token).ConfigureAwait(false);
                    var first = await RestLiveSession.ReceiveAsync(ws, timeout.Token).ConfigureAwait(false);
                    if (!LiveProtocol.IsSetupComplete(first))
                        throw new HypaFirebaseException(0, "The Live API did not accept the session" + (ws.CloseStatusDescription != null ? ": " + ws.CloseStatusDescription : "."));
                }
                catch (Exception e)
                {
                    ws.Dispose();
                    if (e is OperationCanceledException && !ct.IsCancellationRequested) throw new HypaFirebaseException(0, "The Live API did not answer in time.");
                    if (e is WebSocketException w) throw new HypaFirebaseException(0, "The Live API could not be reached: " + w.Message);
                    throw;
                }
            }
            return new RestLiveSession(ws);
        }
    }

    /// <summary>One open socket: a send loop (one message at a time, as a ClientWebSocket needs) and a receive loop, both off the main thread.</summary>
    sealed class RestLiveSession : ILiveSession
    {
        const int ChunkSamples = LiveProtocol.InputRate / 10; // 100 ms per audio message

        readonly ClientWebSocket _ws;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<LiveEvent> _in = new ConcurrentQueue<LiveEvent>();
        readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        readonly float[] _audio = new float[ChunkSamples];
        int _audioCount, _closed;
        bool _held;

        public bool OwnsAudio => false;
        public bool Open => _closed == 0 && _ws.State == WebSocketState.Open;
        public float VoiceLevel => 0f;
        public float MicLevel => 0f;
        public bool MicDenied => false;

        public RestLiveSession(ClientWebSocket ws)
        {
            _ws = ws;
            _ = Task.Run(SendLoop);
            _ = Task.Run(ReceiveLoop);
        }

        public void SendAudio(float[] samples, int count)
        {
            if (_held || !Open) return;
            for (var i = 0; i < count; i++)
            {
                _audio[_audioCount++] = samples[i];
                if (_audioCount < ChunkSamples) continue;
                Queue(LiveProtocol.Audio(LiveProtocol.Pcm16(_audio, 0, _audioCount)));
                _audioCount = 0;
            }
        }

        public void SendText(string text) => Queue(LiveProtocol.Text(text));
        public void SendToolResponse(string id, string name, string result) => Queue(LiveProtocol.ToolResponse(id, name, result));
        public void SetHeld(bool held) { _held = held; _audioCount = 0; }
        public bool TryDequeue(out LiveEvent e) => _in.TryDequeue(out e);

        void Queue(JObject message)
        {
            if (!Open) return;
            _out.Enqueue(message.ToString(Formatting.None));
            _wake.Release();
        }

        async Task SendLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _wake.WaitAsync(_cts.Token).ConfigureAwait(false);
                    while (_out.TryDequeue(out var text))
                        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Close("send failed: " + e.GetType().Name); }
        }

        async Task ReceiveLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var message = await ReceiveAsync(_ws, _cts.Token).ConfigureAwait(false);
                    if (message == null) break;
                    foreach (var e in LiveProtocol.Parse(message)) _in.Enqueue(e);
                }
                Close(_ws.CloseStatus == WebSocketCloseStatus.NormalClosure || _cts.IsCancellationRequested ? null
                    : "closed by the server" + (string.IsNullOrEmpty(_ws.CloseStatusDescription) ? "" : ": " + _ws.CloseStatusDescription));
            }
            catch (OperationCanceledException) { Close(null); }
            catch (Exception e) { Close("connection lost: " + e.GetType().Name); }
        }

        void Close(string error)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _in.Enqueue(new LiveEvent { Kind = LiveEventKind.Closed, Error = error });
        }

        public static Task SendAsync(ClientWebSocket ws, JObject message, CancellationToken ct)
            => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message.ToString(Formatting.None))), WebSocketMessageType.Text, true, ct);

        /// <summary>The next whole message as JSON (the server sends JSON in text or binary frames), or null once the socket closes.</summary>
        public static async Task<JObject> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            using (var all = new MemoryStream())
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    all.Write(buffer, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                var text = Encoding.UTF8.GetString(all.GetBuffer(), 0, (int)all.Length);
                try { return JObject.Parse(text); }
                catch (JsonException) { return new JObject(); }
            }
        }

        public void Dispose()
        {
            Close(null);
            _cts.Cancel();
            var ws = _ws;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
                ws.Dispose();
            });
        }
    }
}
