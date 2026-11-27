using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaCore.Hybrid.Web
{
    /// <summary>
    /// The Live API in the browser: the JS SDK's <c>getLiveGenerativeModel</c> in firebase-bridge.js (<c>liveStart</c>). The page
    /// records the microphone (echo cancellation on) and plays her voice itself, so audio never crosses into C#; transcripts, tool
    /// calls, interruptions and levels come back as the call's chunks. No voice activity settings: the SDK's defaults.
    /// </summary>
    public sealed class WebLiveModel : ILiveModel
    {
        public async Task<ILiveSession> ConnectAsync(LiveSetup setup, CancellationToken ct)
        {
            var args = new JObject
            {
                ["model"] = setup.Model,
                ["system"] = setup.SystemInstruction,
                ["voice"] = setup.Voice,
                ["handle"] = setup.ResumeHandle,
                ["tools"] = setup.Tools == null || setup.Tools.Count == 0 ? null : CloudToolCalls.Declarations(setup.Tools),
            };
            var session = new WebLiveSession();
            await session.StartAsync(args, ct);
            return session;
        }
    }

    sealed class WebLiveSession : ILiveSession
    {
        readonly Queue<LiveEvent> _in = new Queue<LiveEvent>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        bool _open;

        public bool OwnsAudio => true;
        public bool Open => _open;
        public float VoiceLevel { get; private set; }
        public float MicLevel { get; private set; }
        public bool MicDenied { get; private set; }

        public async Task StartAsync(JObject args, CancellationToken ct)
        {
            var ready = new TaskCompletionSource<bool>();
            using (ct.Register(() => ready.TrySetCanceled()))
            {
                _ = Pump(args, ready);
                try { await ready.Task; }
                catch { _cts.Cancel(); throw; }
            }
        }

        async Task Pump(JObject args, TaskCompletionSource<bool> ready)
        {
            string error = null;
            try
            {
                await foreach (var chunk in WebBridge.StreamAsync("liveStart", args, _cts.Token))
                {
                    if (!(chunk is JObject m)) continue;
                    if ((string)m["t"] == "ready") { _open = true; ready.TrySetResult(true); continue; }
                    Handle(m);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { error = e.Message; ready.TrySetException(e); }
            ready.TrySetException(new InvalidOperationException(error ?? "The Live session ended before it started."));
            _open = false;
            VoiceLevel = 0f;
            _in.Enqueue(new LiveEvent { Kind = LiveEventKind.Closed, Error = _cts.IsCancellationRequested ? null : error ?? "closed by the server" });
        }

        void Handle(JObject m)
        {
            switch ((string)m["t"])
            {
                case "level": VoiceLevel = (float?)m["v"] ?? 0f; MicLevel = (float?)m["mic"] ?? 0f; break;
                case "mic": MicDenied = (string)m["state"] == "denied"; break;
                case "first": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.FirstAudio, Value = (float?)m["ms"] ?? 0f }); break;
                case "interrupted": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.Interrupted }); break;
                case "turn": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.TurnComplete }); break;
                case "in": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.InputText, Text = (string)m["text"] }); break;
                case "out": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.OutputText, Text = (string)m["text"] }); break;
                case "goAway": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.GoAway, Value = (float?)m["timeLeft"] ?? 0f }); break;
                case "handle": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.Handle, Text = (string)m["handle"] }); break;
                case "toolCancel": _in.Enqueue(new LiveEvent { Kind = LiveEventKind.ToolCancel, Ids = (m["ids"] as JArray)?.Select(i => (string)i).ToArray() }); break;
                case "tool":
                    foreach (var call in (m["calls"] as JArray ?? new JArray()).OfType<JObject>())
                        _in.Enqueue(new LiveEvent { Kind = LiveEventKind.ToolCall, Id = (string)call["id"], Name = (string)call["name"], Args = LiveProtocol.Args(call["args"] as JObject) });
                    break;
            }
        }

        public void SendAudio(float[] samples, int count) { } // the page records the microphone itself
        public void SendText(string text) => Fire("liveSend", new JObject { ["text"] = text });
        public void SendToolResponse(string id, string name, string result) => Fire("liveTool", new JObject { ["id"] = id, ["name"] = name, ["result"] = result });
        public void SetHeld(bool held) => Fire("liveHold", new JObject { ["on"] = held });
        public bool TryDequeue(out LiveEvent e)
        {
            if (_in.Count > 0) { e = _in.Dequeue(); return true; }
            e = null;
            return false;
        }

        void Fire(string method, JObject args)
        {
            if (!_open) return;
            _ = Run();
            async Task Run()
            {
                try { await WebBridge.CallAsync(method, args); }
                catch (Exception e) { Debug.LogWarning($"Hypa: Live {method} failed ({e.Message})."); }
            }
        }

        public void Dispose()
        {
            _open = false;
            _cts.Cancel(); // the bridge aborts liveStart, which closes the session, the microphone and the audio
        }
    }
}
