using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// Her Live voice on Android, iOS, Quest and the editor: 24 kHz pieces played as they arrive through one streaming clip on her
    /// mouth, so there is no gap between pieces. <see cref="Clear"/> drops what has not been heard (she was interrupted).
    /// Not used in the browser, where the page plays the Live audio itself (streaming clips don't run in WebGL).
    /// </summary>
    public sealed class LivePlayback : MonoBehaviour
    {
        const int Rate = HypaCore.Hybrid.LiveProtocol.OutputRate;
        readonly object _lock = new object();
        float[] _ring = new float[Rate * 120];
        int _head, _count;
        float _level;
        AudioSource _source;

        /// <summary>Loudness of what is playing now, 0..1 (lip sync).</summary>
        public float Level { get; private set; }
        /// <summary>Seconds of her voice still to play.</summary>
        public float Buffered { get { lock (_lock) return _count / (float)Rate; } }
        public bool Playing => Buffered > 0.02f;

        public static LivePlayback On(Transform mouth)
        {
            var go = new GameObject("Live Voice");
            go.transform.SetParent(mouth, false);
            var p = go.AddComponent<LivePlayback>();
            var s = p._source = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0.6f; // from her, but always clear (as StoryVoice)
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 2f; s.maxDistance = 30f;
            s.loop = true;
            s.clip = AudioClip.Create("Live voice", Rate, 1, Rate, true, p.Fill);
            return p;
        }

        public void Enqueue(float[] samples)
        {
            if (samples == null || samples.Length == 0) return;
            lock (_lock)
            {
                if (_ring.Length < _count + samples.Length)
                {
                    var bigger = new float[Mathf.NextPowerOfTwo(_count + samples.Length)];
                    for (var i = 0; i < _count; i++) bigger[i] = _ring[(_head + i) % _ring.Length];
                    _ring = bigger; _head = 0;
                }
                for (var i = 0; i < samples.Length; i++) _ring[(_head + _count + i) % _ring.Length] = samples[i];
                _count += samples.Length;
            }
            if (!_source.isPlaying) _source.Play();
        }

        public void Clear()
        {
            lock (_lock) { _head = 0; _count = 0; }
        }

        /// <summary>The clip asks for more (audio thread): her voice, then silence.</summary>
        void Fill(float[] data)
        {
            var sum = 0f;
            lock (_lock)
            {
                for (var i = 0; i < data.Length; i++)
                {
                    if (_count > 0)
                    {
                        data[i] = _ring[_head];
                        _head = (_head + 1) % _ring.Length;
                        _count--;
                    }
                    else data[i] = 0f;
                    sum += data[i] * data[i];
                }
            }
            _level = data.Length > 0 ? Mathf.Sqrt(sum / data.Length) : 0f;
        }

        void Update() => Level = Mathf.Min(1f, _level * 5f);

        void OnDestroy()
        {
            if (_source != null && _source.clip != null) Destroy(_source.clip);
        }
    }
}
