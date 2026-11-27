using System;
using HypaCore;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace HypaStories
{
    /// <summary>Where recorded samples come from: a Unity microphone clip, the browser microphone in WebGL, or Android's voice-call input.</summary>
    interface IMicSource
    {
        string Name { get; }
        /// <summary>Her voice from the speaker is taken out of the recording by the platform.</summary>
        bool EchoCancelled { get; }
        /// <summary>Why recording stopped working (e.g. the microphone was refused), or null.</summary>
        string Error { get; }
        /// <summary>The samples recorded since the last call, or null if none.</summary>
        float[] ReadNew();
        void End();
    }

    sealed class ClipMicSource : IMicSource
    {
        readonly string _device;
        readonly AudioClip _clip;
        int _read;
        public ClipMicSource(string device, AudioClip clip) { _device = device; _clip = clip; }
        public string Name => _device ?? "default input";
        public string Error => null;
        /// <summary>Unity's Microphone has no echo canceller, except on iOS in voice chat mode (<see cref="VoiceMode"/>).</summary>
        public bool EchoCancelled => Application.platform == RuntimePlatform.IPhonePlayer;

        public float[] ReadNew()
        {
            var pos = Microphone.GetPosition(_device);
            if (pos < 0) return null;
            var n = (pos - _read + _clip.samples) % _clip.samples;
            if (n == 0) return null;
            var buf = new float[n]; // GetData reads exactly buf.Length samples
            _clip.GetData(buf, _read); // wraps around the looping clip
            _read = pos;
            return buf;
        }

        public void End() => Microphone.End(_device);
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    sealed class WebMicSource : IMicSource
    {
        readonly float[] _buf = new float[16000];
        public string Name => "browser microphone";
        public bool EchoCancelled => true; // getUserMedia with echoCancellation (HypaMicrophone.jslib)
        public string Error => WebMicrophone.Current == WebMicrophone.State.Denied ? "Allow the microphone in your browser, then tap the mic again."
            : WebMicrophone.Current == WebMicrophone.State.Failed ? "The microphone could not start in this browser." : null;

        public float[] ReadNew()
        {
            var n = WebMicrophone.Read(_buf);
            if (n == 0) return null;
            var samples = new float[n];
            Array.Copy(_buf, samples, n);
            return samples;
        }

        public void End() => WebMicrophone.Stop();
    }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>
    /// Android's VOICE_COMMUNICATION input with the platform echo canceller (ai.hypa.voice.HypaVoiceMic, HypaVoice.androidlib), so
    /// she does not hear her own voice from the speaker. Unity's Microphone records from the plain MIC input, which has no echo canceller.
    /// </summary>
    sealed class AndroidVoiceMicSource : IMicSource
    {
        const string Class = "ai.hypa.voice.HypaVoiceMic";
        readonly AndroidJavaClass _mic;
        public string Name => "voice-call input" + (EchoCancelled ? " (echo cancelled)" : "");
        public bool EchoCancelled { get; }
        public string Error => null;

        AndroidVoiceMicSource(AndroidJavaClass mic, bool aec) { _mic = mic; EchoCancelled = aec; }

        /// <summary>The voice-call input, or null when the plug-in is missing or the input can't start (then Unity's Microphone is used).</summary>
        public static AndroidVoiceMicSource TryStart(int rate)
        {
            try
            {
                var mic = new AndroidJavaClass(Class);
                if (!mic.CallStatic<bool>("start", rate)) { mic.Dispose(); return null; }
                return new AndroidVoiceMicSource(mic, mic.CallStatic<bool>("echoCancelled"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Hypa Stories: no voice-call input (" + e.GetType().Name + "); using the plain microphone.");
                return null;
            }
        }

        public float[] ReadNew()
        {
            var samples = _mic.CallStatic<float[]>("read");
            return samples == null || samples.Length == 0 ? null : samples;
        }

        public void End()
        {
            try { _mic.CallStatic("stop"); } catch (Exception) { }
            _mic.Dispose();
        }
    }
#endif

    /// <summary>
    /// An open microphone for a conversation (G10): 16 kHz mono samples, read every frame, until <see cref="Stop"/>. In the platform's
    /// voice-call mode where there is one (Android VOICE_COMMUNICATION, iOS voice chat; the browser has echo cancellation on), so
    /// she doesn't hear herself. <see cref="Open"/> throws when there is no microphone or no permission: the caller offers text instead.
    /// </summary>
    public sealed class MicStream
    {
        public const int Rate = 16000;
        readonly IMicSource _source;

        public string Name => _source.Name;
        public string Error => _source.Error;
        /// <summary>
        /// The platform takes her voice from the speaker out of the recording: the browser, iOS voice chat, Android's voice-call
        /// input when the device has an echo canceller. Not the editor or a desktop: there she can't be talked over.
        /// </summary>
        public bool EchoCancelled => _source.EchoCancelled;

        MicStream(IMicSource source) { _source = source; }

        public static MicStream Open()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                throw new InvalidOperationException("Allow the microphone to talk with her.");
            }
            var voice = AndroidVoiceMicSource.TryStart(Rate);
            if (voice != null) return new MicStream(voice);
#endif
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                Application.RequestUserAuthorization(UserAuthorization.Microphone);
                throw new InvalidOperationException("macOS is not letting Unity use the microphone.");
            }
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            WebMicrophone.Start();
            return new MicStream(new WebMicSource());
#else
            VoiceMode.Set(true);
            if (Microphone.devices.Length == 0) throw new InvalidOperationException("No microphone.");
            var device = MicListener.Device(Microphone.devices);
            var clip = Microphone.Start(device, true, 10, Rate);
            if (clip == null) throw new InvalidOperationException("Microphone could not start.");
            return new MicStream(new ClipMicSource(device, clip));
#endif
        }

        /// <summary>The samples since the last read, or null.</summary>
        public float[] Read() => _source.ReadNew();

        public void Stop()
        {
            _source.End();
            VoiceMode.Set(false);
        }
    }

    /// <summary>
    /// The platform's voice-call audio mode while a conversation is open. iOS: the audio session's voice chat mode (echo cancellation
    /// for the built-in microphone and speaker), through HypaVoiceSession.mm. Android uses the voice-call input instead
    /// (<see cref="MicStream"/>); the browser's microphone always has echo cancellation on.
    /// </summary>
    public static class VoiceMode
    {
#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void HypaVoice_SetVoiceChat(bool on);
#endif
        static bool _on;

        public static void Set(bool on)
        {
            if (_on == on) return;
            _on = on;
#if UNITY_IOS && !UNITY_EDITOR
            try { HypaVoice_SetVoiceChat(on); }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: voice chat mode unavailable (" + e.GetType().Name + ")."); }
#endif
        }
    }
}
