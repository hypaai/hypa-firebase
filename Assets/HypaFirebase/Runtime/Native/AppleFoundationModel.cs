using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaFirebase.Native
{
    /// <summary>An error from Apple's on-device model: "unavailable", "too_long", "guardrail", "unsupported_language", "busy", "cancelled" or "failed".</summary>
    public sealed class OnDeviceModelException : Exception
    {
        public string Code { get; }
        public OnDeviceModelException(string code) : base($"On-device model: {code}") { Code = code; }
    }

    /// <summary>
    /// Apple's on-device model (Foundation Models, iOS 26 with Apple Intelligence) through Assets/Plugins/iOS/HypaFoundationModels.swift
    /// (docs/ios-on-device.md). Every answer is generated on the device, so the route chip can say so. When the model is not ready
    /// a call fails at once with "unavailable" and the router uses the cloud; nothing waits for Apple's download.
    /// </summary>
    public sealed class AppleFoundationModel : IOnDeviceModel
    {
        /// <summary>The Swift functions. Tests replace them.</summary>
        public interface INative
        {
            int Status();
            void Start(int id, string prompt, bool stream);
            void Cancel(int id);
        }

        public static INative Native = new Swift();

        public bool Available => Native.Status() == 0;

        public Task<string> StatusAsync() => Task.FromResult(Native.Status() switch
        {
            0 => "available",
            1 => "downloading",
            _ => "unavailable",
        });

        /// <summary>Why the model is unavailable, for the help card; null when it is available or downloading.</summary>
        public static string UnavailableReason => Native.Status() switch
        {
            2 => "This iPhone does not support Apple Intelligence.",
            3 => "Turn on Apple Intelligence in Settings to answer on this iPhone.",
            4 => "Update to iOS 26 to answer on this iPhone.",
            _ => null,
        };

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var (id, p) = Begin(prompt, false);
            using (ct.Register(() => Abandon(id, p)))
                return await p.Done.Task;
        }

        public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var (id, p) = Begin(prompt, true);
            try
            {
                while (true)
                {
                    if (p.Chunks.Count > 0) { yield return p.Chunks.Dequeue(); continue; }
                    if (p.Done.Task.IsCompleted) { await p.Done.Task; yield break; } // rethrows the model's error
                    p.Arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => Abandon(id, p)))
                        await Task.WhenAny(p.Arrived.Task, p.Done.Task);
                    ct.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (!p.Done.Task.IsCompleted) Abandon(id, p); // the caller stopped early: stop the model too
            }
        }

        sealed class Pending
        {
            public readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly Queue<string> Chunks = new Queue<string>();
            public TaskCompletionSource<bool> Arrived;
        }

        static readonly Dictionary<int, Pending> Requests = new Dictionary<int, Pending>();
        static int _nextId;

        static (int, Pending) Begin(string prompt, bool stream)
        {
            var id = ++_nextId;
            var p = new Pending();
            Requests[id] = p;
            try { Native.Start(id, prompt ?? "", stream); }
            catch (Exception e) { Requests.Remove(id); p.Done.TrySetException(e); }
            return (id, p);
        }

        static void Abandon(int id, Pending p)
        {
            if (!Requests.Remove(id)) return;
            p.Done.TrySetCanceled();
            p.Arrived?.TrySetResult(true);
            Native.Cancel(id);
        }

        /// <summary>A reply from Swift. kind 0: a chunk, 1: done (the answer), 2: error (a code). Replies to finished or cancelled requests are dropped.</summary>
        public static void Receive(int id, int kind, string text)
        {
            if (!Requests.TryGetValue(id, out var p)) return;
            if (kind == 0) p.Chunks.Enqueue(text);
            else
            {
                Requests.Remove(id);
                if (kind == 1) p.Done.TrySetResult(text);
                else p.Done.TrySetException(new OnDeviceModelException(string.IsNullOrEmpty(text) ? "failed" : text));
            }
            p.Arrived?.TrySetResult(true);
        }

        /// <summary>For tests: forgets every request and restores the Swift transport.</summary>
        public static void ResetForTests()
        {
            Requests.Clear();
            _nextId = 0;
            Native = new Swift();
        }

        /// <summary>iOS player: the on-device model of every service set, including the one G02's native factory builds.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
#if UNITY_IOS && !UNITY_EDITOR
            HybridServices.OnDeviceFactory = () => new AppleFoundationModel();
#endif
        }

        sealed class Swift : INative
        {
#if UNITY_IOS && !UNITY_EDITOR
            delegate void Callback(int id, int kind, IntPtr text);
            [DllImport("__Internal")] static extern void HypaFM_SetCallback(Callback cb);
            [DllImport("__Internal")] static extern int HypaFM_Status();
            [DllImport("__Internal")] static extern void HypaFM_Start(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, [MarshalAs(UnmanagedType.U1)] bool stream);
            [DllImport("__Internal")] static extern void HypaFM_Cancel(int id);

            static readonly Callback Reply = OnReply; // kept alive while Swift holds it
            static bool _ready;

            [AOT.MonoPInvokeCallback(typeof(Callback))]
            static void OnReply(int id, int kind, IntPtr text) => Receive(id, kind, Marshal.PtrToStringUTF8(text) ?? "");

            static void Ready()
            {
                if (_ready) return;
                HypaFM_SetCallback(Reply);
                _ready = true;
            }

            public int Status() => HypaFM_Status();
            public void Start(int id, string prompt, bool stream) { Ready(); HypaFM_Start(id, prompt, stream); }
            public void Cancel(int id) => HypaFM_Cancel(id);
#else
            public int Status() => 4;
            public void Start(int id, string prompt, bool stream) => throw new PlatformNotSupportedException("Apple's on-device model needs an iOS build.");
            public void Cancel(int id) { }
#endif
        }
    }
}
