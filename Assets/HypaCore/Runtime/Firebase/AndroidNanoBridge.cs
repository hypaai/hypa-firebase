#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// <see cref="INanoBridge"/> over JNI to <c>ai.hypa.nano.HypaNano</c> (Assets/HypaCore/Plugins/Android/HypaNano.androidlib).
    /// Every call is made from a worker thread, so each one attaches the thread to the Java VM first. A build without the plug-in,
    /// Quest, and a phone with no AICore all end as "unavailable": nothing here throws.
    /// </summary>
    public sealed class AndroidNanoBridge : INanoBridge
    {
        const string BridgeClass = "ai.hypa.nano.HypaNano";

        readonly int _mainThread;

        public AndroidNanoBridge(int mainThread) { _mainThread = mainThread; }

        public string Status() => OnJavaThread(() =>
        {
            using (var bridge = new AndroidJavaClass(BridgeClass)) return bridge.CallStatic<string>("status");
        }, "unavailable");

        public void Generate(string prompt, int maxTokens, float temperature, INanoTextSink sink)
        {
            var started = OnJavaThread(() =>
            {
                using (var bridge = new AndroidJavaClass(BridgeClass))
                    bridge.CallStatic("generate", prompt, maxTokens, temperature, new TextListener(sink));
                return true;
            }, false);
            if (!started) sink.Error("unavailable");
        }

        public void Download(INanoDownloadSink sink)
        {
            var started = OnJavaThread(() =>
            {
                using (var bridge = new AndroidJavaClass(BridgeClass))
                    bridge.CallStatic("download", new DownloadListener(sink));
                return true;
            }, false);
            if (!started) sink.Error("unavailable");
        }

        public void Cancel() => OnJavaThread(() =>
        {
            using (var bridge = new AndroidJavaClass(BridgeClass)) bridge.CallStatic("cancel");
            return true;
        }, false);

        T OnJavaThread<T>(Func<T> call, T whenItFails)
        {
            // Unity's main thread is already attached, and must not be detached.
            var attach = Thread.CurrentThread.ManagedThreadId != _mainThread;
            try
            {
                if (attach) AndroidJNI.AttachCurrentThread();
                return call();
            }
            catch (Exception)
            {
                return whenItFails;
            }
            finally
            {
                if (attach) AndroidJNI.DetachCurrentThread();
            }
        }

        // Java calls these by name, on its own threads.
        sealed class TextListener : AndroidJavaProxy
        {
            readonly INanoTextSink _sink;
            public TextListener(INanoTextSink sink) : base(BridgeClass + "$TextListener") { _sink = sink; }
            public void onText(string chunk) => _sink.Text(chunk);
            public void onDone() => _sink.Done();
            public void onError(string reason) => _sink.Error(reason);
        }

        sealed class DownloadListener : AndroidJavaProxy
        {
            readonly INanoDownloadSink _sink;
            public DownloadListener(INanoDownloadSink sink) : base(BridgeClass + "$DownloadListener") { _sink = sink; }
            public void onStarted(long totalBytes) => _sink.Started(totalBytes);
            public void onProgress(long downloadedBytes) => _sink.Progress(downloadedBytes);
            public void onDone() => _sink.Done();
            public void onError(string reason) => _sink.Error(reason);
        }
    }

    /// <summary>Registers the Android on-device model when the app starts, so every <see cref="HybridServices"/> gets it.</summary>
    public static class AndroidNano
    {
        public static AndroidNanoModel Shared { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            Shared = new AndroidNanoModel(new AndroidNanoBridge(Thread.CurrentThread.ManagedThreadId));
            HybridServices.OnDeviceFactory = () => Shared;
            // The router reads Available without waiting, so find out now what this device can do.
            _ = Shared.StatusAsync();
        }
    }
}
#endif
