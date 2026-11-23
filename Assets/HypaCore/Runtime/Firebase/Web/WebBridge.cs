using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaCore.Hybrid.Web
{
    /// <summary>An error from the Firebase JS SDK side, e.g. "auth/network-request-failed", "unavailable", "not_configured".</summary>
    public sealed class WebBridgeException : Exception
    {
        public string Code { get; }
        public WebBridgeException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// C# side of the WebGL Firebase bridge (docs/web-bridge.md): C# → HypaFirebase.jslib → firebase-bridge.js promise →
    /// <c>SendMessage</c> back to this object with the request id. Every call is a task (or a stream of chunks), never a
    /// blocking wait: WebGL has one thread, so <c>.Result</c> or <c>.Wait()</c> on one of these would hang the page.
    /// </summary>
    public sealed class WebBridge : MonoBehaviour
    {
        /// <summary>The GameObject name the jslib sends to.</summary>
        public const string ObjectName = "HypaWebBridge";

        /// <summary>Sends (id, method, args JSON) to JS. The jslib in WebGL builds; tests replace it.</summary>
        public static Action<int, string, string> Send = JsCall;
        /// <summary>Tells JS that C# stopped listening to a request (cancelled or stream abandoned).</summary>
        public static Action<int> Cancel = JsCancel;
        /// <summary>Events from JS: "uid" (string or null) and "progress" (the progress document).</summary>
        public static event Action<string, JToken> Event;

        sealed class Pending
        {
            public readonly TaskCompletionSource<JToken> Done = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly Queue<JToken> Chunks = new Queue<JToken>();
            public TaskCompletionSource<bool> Arrived;
        }

        static readonly Dictionary<int, Pending> Requests = new Dictionary<int, Pending>();
        static int _nextId;

        /// <summary>Creates the receiver object. WebGL builds only; called by <see cref="WebServices"/>.</summary>
        public static void Install()
        {
            if (GameObject.Find(ObjectName) != null) return;
            var go = new GameObject(ObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<WebBridge>();
        }

        /// <summary>Calls a bridge method and returns its value (JSON null for none).</summary>
        public static async Task<JToken> CallAsync(string method, JToken args = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var (id, p) = Start(method, args);
            using (ct.Register(() => Abandon(id, p)))
                return await p.Done.Task;
        }

        /// <summary>Calls a streaming bridge method and yields its chunks until it finishes.</summary>
        public static async IAsyncEnumerable<JToken> StreamAsync(string method, JToken args, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var (id, p) = Start(method, args);
            try
            {
                while (true)
                {
                    if (p.Chunks.Count > 0) { yield return p.Chunks.Dequeue(); continue; }
                    if (p.Done.Task.IsCompleted) { await p.Done.Task; yield break; } // rethrows a JS error
                    p.Arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => Abandon(id, p)))
                        await Task.WhenAny(p.Arrived.Task, p.Done.Task);
                    ct.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                // The consumer stopped early (break, exception): let JS stop producing.
                if (!p.Done.Task.IsCompleted) Abandon(id, p);
            }
        }

        static (int, Pending) Start(string method, JToken args)
        {
            var id = ++_nextId;
            var p = new Pending();
            Requests[id] = p;
            try { Send(id, method, (args ?? JValue.CreateNull()).ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception e) { Requests.Remove(id); p.Done.TrySetException(e); }
            return (id, p);
        }

        static void Abandon(int id, Pending p)
        {
            if (!Requests.Remove(id)) return;
            p.Done.TrySetCanceled();
            p.Arrived?.TrySetResult(true);
            Cancel(id);
        }

        /// <summary>Called by the jslib through SendMessage.</summary>
        public void OnMessage(string json) => Receive(json);

        /// <summary>Handles one message from JS. Messages for finished or cancelled requests are dropped.</summary>
        public static void Receive(string json)
        {
            JObject msg;
            try { msg = JObject.Parse(json); }
            catch (Exception) { Debug.LogWarning("Hypa: unreadable message from the Firebase bridge."); return; }
            var kind = (string)msg["kind"];
            if (kind == "event") { Event?.Invoke((string)msg["name"], msg["value"]); return; }
            var id = (int?)msg["id"] ?? 0;
            if (!Requests.TryGetValue(id, out var p)) return;
            switch (kind)
            {
                case "chunk":
                    p.Chunks.Enqueue(msg["value"]);
                    p.Arrived?.TrySetResult(true);
                    break;
                case "ok":
                    Requests.Remove(id);
                    p.Done.TrySetResult(msg["value"]);
                    p.Arrived?.TrySetResult(true);
                    break;
                default:
                    Requests.Remove(id);
                    p.Done.TrySetException(new WebBridgeException((string)msg["code"] ?? "error", (string)msg["message"] ?? "Firebase bridge error."));
                    p.Arrived?.TrySetResult(true);
                    break;
            }
        }

        /// <summary>For tests: forgets every request and resets the transport.</summary>
        public static void ResetForTests()
        {
            Requests.Clear();
            _nextId = 0;
            Send = JsCall;
            Cancel = JsCancel;
            Event = null;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void HypaFirebase_Call(int id, string method, string args);
        [DllImport("__Internal")] static extern void HypaFirebase_Cancel(int id);
        static void JsCall(int id, string method, string args) => HypaFirebase_Call(id, method, args);
        static void JsCancel(int id) => HypaFirebase_Cancel(id);
#else
        static void JsCall(int id, string method, string args) => throw new PlatformNotSupportedException("The Firebase web bridge needs a WebGL build.");
        static void JsCancel(int id) { }
#endif
    }
}
