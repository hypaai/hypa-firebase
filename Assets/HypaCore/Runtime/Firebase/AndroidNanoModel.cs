using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// The Android on-device model: Gemini Nano through the <see cref="INanoBridge"/>. It only ever answers on the device; when it
    /// cannot, the router sends the question to the cloud model. <see cref="Available"/> is read by the router without waiting, so
    /// it is the last status seen: call <see cref="StatusAsync"/> at start-up (the registration does) and after a download.
    /// On Quest, and on a phone with no AICore, the status is "unavailable" and nothing is ever generated.
    /// </summary>
    public sealed class AndroidNanoModel : IDownloadableOnDeviceModel
    {
        /// <summary>The output limit of a reply. The model's own window is about 4,000 tokens for the prompt and reply together.</summary>
        public const int DefaultMaxTokens = 512;

        readonly INanoBridge _bridge;
        volatile string _status = "unavailable";

        public AndroidNanoModel(INanoBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public bool Available => _status == "available";

        public Task<string> StatusAsync() => Task.Run(() =>
        {
            string status;
            try { status = _bridge.Status(); }
            catch (Exception) { status = null; }
            _status = Normalize(status);
            return _status;
        });

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            var text = new System.Text.StringBuilder();
            await foreach (var chunk in StreamAsync(prompt, ct).WithCancellation(ct).ConfigureAwait(false)) text.Append(chunk);
            return text.ToString();
        }

        public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!Available) throw new InvalidOperationException("The on-device model is not ready (" + _status + ").");
            ct.ThrowIfCancellationRequested();

            var pump = new TextPump();
            using (ct.Register(() => _bridge.Cancel()))
            {
                // The first call can take a moment to set the model up, and this may be Unity's main thread.
                await Task.Run(() => _bridge.Generate(prompt, DefaultMaxTokens, -1f, pump)).ConfigureAwait(false);
                while (true)
                {
                    await pump.WaitAsync(ct).ConfigureAwait(false);
                    while (pump.TryTake(out var chunk)) yield return chunk;
                    if (pump.IsFinished && pump.IsEmpty) break;
                }
            }
            if (pump.Failure != null) throw new OnDeviceModelException(pump.Failure);
        }

        public async Task DownloadAsync(IProgress<float> progress, CancellationToken ct)
        {
            var sink = new DownloadWatch(progress);
            using (ct.Register(() => sink.Abandon()))
            {
                await Task.Run(() => _bridge.Download(sink)).ConfigureAwait(false);
                await sink.Finished.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            await StatusAsync().ConfigureAwait(false);
        }

        static string Normalize(string status)
        {
            switch (status)
            {
                case "available":
                case "downloadable":
                case "downloading":
                    return status;
                default:
                    return "unavailable";
            }
        }

        /// <summary>Hands the bridge's text, which arrives on any thread, to the one that is reading it.</summary>
        sealed class TextPump : INanoTextSink
        {
            readonly ConcurrentQueue<string> _chunks = new ConcurrentQueue<string>();
            readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            volatile bool _finished;

            public string Failure { get; private set; }
            public bool IsFinished => _finished;
            public bool IsEmpty => _chunks.IsEmpty;

            public void Text(string chunk)
            {
                if (string.IsNullOrEmpty(chunk)) return;
                _chunks.Enqueue(chunk);
                _signal.Release();
            }

            public void Done() => Finish(null);

            public void Error(string reason) => Finish(string.IsNullOrEmpty(reason) ? "error" : reason);

            void Finish(string failure)
            {
                if (_finished) return;
                Failure = failure;
                _finished = true;
                _signal.Release();
            }

            public Task WaitAsync(CancellationToken ct) => _signal.WaitAsync(ct);

            public bool TryTake(out string chunk) => _chunks.TryDequeue(out chunk);
        }

        sealed class DownloadWatch : INanoDownloadSink
        {
            readonly IProgress<float> _progress;
            readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long _total;

            public DownloadWatch(IProgress<float> progress) { _progress = progress; }

            public Task Finished => _done.Task;

            public void Started(long totalBytes)
            {
                _total = totalBytes;
                _progress?.Report(0f);
            }

            public void Progress(long downloadedBytes)
            {
                if (_total > 0) _progress?.Report(Math.Min(1f, (float)downloadedBytes / _total));
            }

            public void Done()
            {
                _progress?.Report(1f);
                _done.TrySetResult(true);
            }

            public void Error(string reason) => _done.TrySetException(new OnDeviceModelException(string.IsNullOrEmpty(reason) ? "error" : reason));

            /// <summary>The caller stopped waiting. The system may still finish the download.</summary>
            public void Abandon() => _done.TrySetResult(true);
        }
    }
}
