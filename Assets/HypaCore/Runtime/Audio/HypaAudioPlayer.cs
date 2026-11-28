using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace HypaCore
{
    /// <summary>
    /// Plays Hypa TTS. Queues clips, stops with a fade, and reports <see cref="Volume"/> (RMS, 0..1) every frame for
    /// lip sync. Audio links are fetched off the main thread; decoding to an AudioClip happens on the main thread.
    /// WAV is decoded in memory; MP3/OGG go through UnityWebRequestMultimedia via a temp file (audio only, never frames).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class HypaAudioPlayer : MonoBehaviour
    {
        static HypaAudioPlayer _instance;
        public static HypaAudioPlayer Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("Hypa Audio");
                DontDestroyOnLoad(go);
                return _instance = go.AddComponent<HypaAudioPlayer>();
            }
        }

        readonly Queue<AudioClip> _queue = new Queue<AudioClip>();
        readonly float[] _buf = new float[256];
        AudioSource _src;
        Coroutine _fade;

        /// <summary>Current output loudness (RMS of the last ~256 samples), 0..1. Drive mouth shapes from this.</summary>
        public float Volume { get; private set; }
        public bool IsPlaying => _src != null && _src.isPlaying;
        public int Queued => _queue.Count;
        public event Action<AudioClip> ClipStarted;

        void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            _src = GetComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
        }

        void Update()
        {
            if (_fade == null && !_src.isPlaying && _queue.Count > 0)
            {
                _src.clip = _queue.Dequeue();
                _src.volume = 1f;
                _src.Play();
                ClipStarted?.Invoke(_src.clip);
            }
            if (_src.isPlaying)
            {
                _src.GetOutputData(_buf, 0);
                var sum = 0f;
                for (var i = 0; i < _buf.Length; i++) sum += _buf[i] * _buf[i];
                Volume = Mathf.Sqrt(sum / _buf.Length);
            }
            else Volume = 0f;
        }

        /// <summary>Fetch, decode and add to the end of the queue.</summary>
        public async Task EnqueueAsync(HypaSpeech speech, CancellationToken ct)
        {
            var clip = await LoadClipAsync(speech, ct);
            if (clip != null) _queue.Enqueue(clip);
        }

        /// <summary>Stop what's playing (with a short fade), clear the queue, then play this.</summary>
        public async Task PlayNowAsync(HypaSpeech speech, CancellationToken ct)
        {
            var clip = await LoadClipAsync(speech, ct);
            Stop(0.08f);
            if (clip != null) _queue.Enqueue(clip);
        }

        public void Enqueue(AudioClip clip) { if (clip != null) _queue.Enqueue(clip); }

        /// <summary>Clear the queue and fade the current clip out over <paramref name="fadeSeconds"/>.</summary>
        public void Stop(float fadeSeconds = 0.25f)
        {
            _queue.Clear();
            if (!_src.isPlaying) return;
            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeOut(fadeSeconds));
        }

        IEnumerator FadeOut(float seconds)
        {
            var start = _src.volume;
            for (var t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                _src.volume = Mathf.Lerp(start, 0f, t / seconds);
                yield return null;
            }
            _src.Stop();
            _src.volume = 1f;
            _fade = null;
        }

        // ---------- Loading ----------

        /// <summary>
        /// The speech as a clip. Unpacking the base64 and the WAV runs off the main thread; only the clip itself is made on it,
        /// one per frame, so a batch of lines arriving together (a story's opening line, the next lines and its fillers) never
        /// stalls a frame. Must be awaited from the main thread.
        /// </summary>
        public static async Task<AudioClip> LoadClipAsync(HypaSpeech s, CancellationToken ct)
        {
            if (s == null) return null;
            byte[] bytes = s.AudioBytes;
            var base64 = s.AudioBase64;
            if (bytes == null && !string.IsNullOrEmpty(base64)) bytes = await OffMain(() => Convert.FromBase64String(base64), ct);
            if (bytes == null && !string.IsNullOrEmpty(s.AudioUrl)) bytes = await HypaHttp.GetBytesAsync(s.AudioUrl, ct); // off main thread
            if (bytes == null || bytes.Length == 0) return null;

            (float[] samples, int ch, int rate) pcm = await OffMain(() => WavDecoder.TryDecode(bytes, out var samples, out var ch, out var rate) ? (samples, ch, rate) : ((float[])null, 0, 0), ct);
            if (pcm.samples != null)
            {
                await OneClipPerFrame(ct);
                var clip = AudioClip.Create("hypa-tts", pcm.samples.Length / pcm.ch, pcm.ch, pcm.rate, false);
                clip.SetData(pcm.samples, 0);
                return clip;
            }
            return await DecodeCompressedAsync(bytes, s.Mime, ct);
        }

        /// <summary>Runs <paramref name="work"/> on a worker thread; inline on the web, which has none.</summary>
        static Task<T> OffMain<T>(Func<T> work, CancellationToken ct) =>
            Application.platform == RuntimePlatform.WebGLPlayer ? Task.FromResult(work()) : Task.Run(work, ct);

        static int _clipFrame = -1;

        /// <summary>Waits until no clip has been made this frame (in play; the editor's tests run with no frames).</summary>
        static async Task OneClipPerFrame(CancellationToken ct)
        {
            if (!Application.isPlaying) return;
            while (Time.frameCount == _clipFrame)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            _clipFrame = Time.frameCount;
        }

        static async Task<AudioClip> DecodeCompressedAsync(byte[] bytes, string mime, CancellationToken ct)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return await DecodeWebGlAsync(bytes, ct);
#else
            mime = mime ?? "";
            var type = mime.Contains("ogg") ? AudioType.OGGVORBIS : AudioType.MPEG;
            var path = Path.Combine(Application.temporaryCachePath, "hypa_tts_" + Guid.NewGuid().ToString("N") + (type == AudioType.MPEG ? ".mp3" : ".ogg"));
            File.WriteAllBytes(path, bytes);
            try
            {
                return await RequestClipAsync(new Uri(path).AbsoluteUri, type, ct);
            }
            finally { try { File.Delete(path); } catch { } }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int HypaAudio_Decode(byte[] data, int length);
        [DllImport("__Internal")] static extern int HypaAudio_Status(int id);
        [DllImport("__Internal")] static extern int HypaAudio_Channels(int id);
        [DllImport("__Internal")] static extern int HypaAudio_Rate(int id);
        [DllImport("__Internal")] static extern int HypaAudio_Length(int id);
        [DllImport("__Internal")] static extern int HypaAudio_CopyAndFree(int id, float[] dst, int maxLen);
        [DllImport("__Internal")] static extern void HypaAudio_Free(int id);

        static async Task<AudioClip> DecodeWebGlAsync(byte[] bytes, CancellationToken ct)
        {
            var id = HypaAudio_Decode(bytes, bytes.Length);
            try
            {
                while (HypaAudio_Status(id) == 0)
                {
                    await HypaAsync.NextFrame(ct);
                }
                if (HypaAudio_Status(id) != 1)
                    throw new HypaException(HypaErrorKind.InvalidResponse, "Could not decode the Hypa audio.");
                var ch = Math.Max(1, HypaAudio_Channels(id));
                var rate = Math.Max(8000, HypaAudio_Rate(id));
                var len = HypaAudio_Length(id);
                if (len <= 0)
                    throw new HypaException(HypaErrorKind.InvalidResponse, "Could not decode the Hypa audio.");
                var samples = new float[len];
                var copied = HypaAudio_CopyAndFree(id, samples, len);
                id = 0;
                var clip = AudioClip.Create("hypa-tts", copied / ch, ch, rate, false);
                clip.SetData(samples, 0);
                return clip;
            }
            finally
            {
                if (id != 0) HypaAudio_Free(id);
            }
        }
#endif

        static async Task<AudioClip> RequestClipAsync(string url, AudioType type, CancellationToken ct)
        {
            using (var req = UnityWebRequestMultimedia.GetAudioClip(url, type))
            {
                var op = req.SendWebRequest();
                if (!op.isDone)
                {
                    var done = new TaskCompletionSource<bool>();
                    op.completed += _ => done.TrySetResult(true);
                    using (ct.Register(() => { req.Abort(); done.TrySetCanceled(ct); }))
                        await done.Task;
                }
                ct.ThrowIfCancellationRequested();
                if (req.result != UnityWebRequest.Result.Success)
                    throw new HypaException(HypaErrorKind.InvalidResponse, "Could not decode the Hypa audio.");
                return DownloadHandlerAudioClip.GetContent(req);
            }
        }
    }
}
