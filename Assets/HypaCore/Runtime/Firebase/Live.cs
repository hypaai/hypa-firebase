using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// The Gemini Live API through Firebase AI Logic (G10): one open, two-way audio session with the model's own voice activity
    /// detection, interruption and transcripts. Native talks to the WebSocket itself (<c>RestLiveModel</c> in HypaFirebase); the
    /// web build goes through the JS SDK's <c>getLiveGenerativeModel</c> (the G03 bridge), where the page records and plays the
    /// audio itself (<see cref="ILiveSession.OwnsAudio"/>).
    /// </summary>
    public interface ILiveModel
    {
        /// <summary>Opens a session and waits for the server's <c>setupComplete</c>. Throws when Live is not available here.</summary>
        Task<ILiveSession> ConnectAsync(LiveSetup setup, CancellationToken ct);
    }

    /// <summary>What a session is set up with. <see cref="Tools"/>' <c>Run</c> is not used by the transport: the caller answers calls.</summary>
    public sealed class LiveSetup
    {
        /// <summary>The model name without a prefix, e.g. <c>gemini-2.5-flash-native-audio-preview-12-2025</c> (Remote Config <c>live_model</c>).</summary>
        public string Model;
        public string SystemInstruction;
        /// <summary>A prebuilt voice name (Remote Config <c>live_voice</c>), or null for the model's default.</summary>
        public string Voice;
        /// <summary>A handle from an earlier session's <see cref="LiveEventKind.Handle"/> event, to carry on where it was.</summary>
        public string ResumeHandle;
        public IReadOnlyList<CloudTool> Tools;
    }

    /// <summary>
    /// An open Live session. Events are queued off the main thread and read with <see cref="TryDequeue"/> on it (once per frame).
    /// Input audio is 16 kHz mono; her audio arrives as 24 kHz 16-bit little-endian PCM (<see cref="LiveEventKind.Audio"/>).
    /// </summary>
    public interface ILiveSession : IDisposable
    {
        /// <summary>True when the platform records and plays the audio itself (the web page): <see cref="SendAudio"/> is ignored and no <see cref="LiveEventKind.Audio"/> arrives.</summary>
        bool OwnsAudio { get; }
        bool Open { get; }
        /// <summary>How loud her voice is right now, 0..1, when <see cref="OwnsAudio"/> (lip sync). Otherwise 0: the caller plays the audio.</summary>
        float VoiceLevel { get; }
        /// <summary>How loud the microphone is, 0..1, when <see cref="OwnsAudio"/>.</summary>
        float MicLevel { get; }
        /// <summary>The page could not open the microphone (<see cref="OwnsAudio"/> only): type instead.</summary>
        bool MicDenied { get; }
        void SendAudio(float[] samples, int count);
        void SendText(string text);
        void SendToolResponse(string id, string name, string result);
        /// <summary>Stop listening and playing while something else speaks (an item's story), and start again.</summary>
        void SetHeld(bool held);
        bool TryDequeue(out LiveEvent e);
    }

    public enum LiveEventKind
    {
        /// <summary>A piece of her voice (<see cref="LiveEvent.Pcm"/>).</summary>
        Audio,
        /// <summary>She started speaking this turn (<see cref="LiveEvent.Value"/>: ms from the end of the player's speech, when the page measured it).</summary>
        FirstAudio,
        /// <summary>The player spoke over her: stop her voice now.</summary>
        Interrupted,
        TurnComplete,
        /// <summary>More of what the player said (<see cref="LiveEvent.Text"/>, a piece to append).</summary>
        InputText,
        /// <summary>More of what she said.</summary>
        OutputText,
        /// <summary>She wants to do something (<see cref="LiveEvent.Id"/>, <see cref="LiveEvent.Name"/>, <see cref="LiveEvent.Args"/>).</summary>
        ToolCall,
        ToolCancel,
        /// <summary>The server will close the session soon (<see cref="LiveEvent.Value"/>: seconds left): reconnect with the last handle.</summary>
        GoAway,
        /// <summary>A new resumption handle (<see cref="LiveEvent.Text"/>).</summary>
        Handle,
        /// <summary>The session is over (<see cref="LiveEvent.Error"/> says why, when it was not asked for).</summary>
        Closed,
    }

    public sealed class LiveEvent
    {
        public LiveEventKind Kind;
        public byte[] Pcm;
        public string Text, Id, Name, Error;
        public float Value;
        public IReadOnlyDictionary<string, string> Args;
        public string[] Ids;

        public override string ToString() => Kind + (Text != null ? " " + Text : "") + (Name != null ? " " + Name : "") + (Error != null ? " ERR " + Error : "");
    }

    public sealed class NoLiveModel : ILiveModel
    {
        public Task<ILiveSession> ConnectAsync(LiveSetup setup, CancellationToken ct)
            => throw new NotSupportedException("The Live API is not set up on this device.");
    }

    /// <summary>
    /// The Live API's wire format, as the Firebase JS SDK (13.4.0, @firebase/ai 3.1.0) sends it to Firebase AI Logic with the Gemini
    /// Developer API backend. Pure: built and read here, sent by the platform's transport. Unit tested.
    /// </summary>
    public static class LiveProtocol
    {
        public const string Host = "firebasevertexai.googleapis.com";
        public const string Path = "/ws/google.firebase.vertexai.v1beta.GenerativeService/BidiGenerateContent";
        public const int InputRate = 16000, OutputRate = 24000;
        public const string InputMime = "audio/pcm;rate=16000";

        /// <summary>The socket address. The key is the Firebase web API key (public client configuration, as the JS SDK puts it there).</summary>
        public static string Url(string apiKey) => "wss://" + Host + Path + "?key=" + Uri.EscapeDataString(apiKey ?? "");

        public static JObject Setup(string projectId, LiveSetup s)
        {
            var generation = new JObject
            {
                ["responseModalities"] = new JArray("AUDIO"),
            };
            if (!string.IsNullOrEmpty(s.Voice))
                generation["speechConfig"] = new JObject { ["voiceConfig"] = new JObject { ["prebuiltVoiceConfig"] = new JObject { ["voiceName"] = s.Voice } } };
            var setup = new JObject
            {
                ["model"] = "projects/" + projectId + "/models/" + s.Model,
                ["generationConfig"] = generation,
                // longer than the Live API's 15 minutes of audio, should a cap allow it; and transcripts of both sides
                ["contextWindowCompression"] = new JObject { ["slidingWindow"] = new JObject() },
                ["inputAudioTranscription"] = new JObject(),
                ["outputAudioTranscription"] = new JObject(),
                // {} asks for resumption handles; a handle carries on an earlier session
                ["sessionResumption"] = string.IsNullOrEmpty(s.ResumeHandle) ? new JObject() : new JObject { ["handle"] = s.ResumeHandle },
            };
            if (!string.IsNullOrEmpty(s.SystemInstruction))
                setup["systemInstruction"] = new JObject { ["role"] = "system", ["parts"] = new JArray(new JObject { ["text"] = s.SystemInstruction }) };
            if (s.Tools != null && s.Tools.Count > 0)
                setup["tools"] = new JArray(new JObject { ["functionDeclarations"] = CloudToolCalls.Declarations(s.Tools) });
            return new JObject { ["setup"] = setup };
        }

        public static JObject Audio(byte[] pcm16) => new JObject
        {
            ["realtimeInput"] = new JObject { ["audio"] = new JObject { ["mimeType"] = InputMime, ["data"] = Convert.ToBase64String(pcm16) } },
        };

        public static JObject Text(string text) => new JObject { ["realtimeInput"] = new JObject { ["text"] = text ?? "" } };

        public static JObject ToolResponse(string id, string name, string result) => new JObject
        {
            ["toolResponse"] = new JObject
            {
                ["functionResponses"] = new JArray(new JObject { ["id"] = id, ["name"] = name, ["response"] = new JObject { ["result"] = result ?? "" } }),
            },
        };

        /// <summary>Floats (-1..1) to 16-bit little-endian PCM.</summary>
        public static byte[] Pcm16(float[] samples, int offset, int count)
        {
            var bytes = new byte[count * 2];
            for (var i = 0; i < count; i++)
            {
                var v = samples[offset + i];
                var s = (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f);
                bytes[i * 2] = (byte)(s & 0xff);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            return bytes;
        }

        /// <summary>16-bit little-endian PCM to floats.</summary>
        public static float[] Floats(byte[] pcm16)
        {
            var n = (pcm16?.Length ?? 0) / 2;
            var f = new float[n];
            for (var i = 0; i < n; i++) f[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8)) / 32768f;
            return f;
        }

        /// <summary>True for the server's answer to <see cref="Setup"/>.</summary>
        public static bool IsSetupComplete(JObject message) => message != null && message["setupComplete"] != null;

        /// <summary>One server message as events, in order. Unknown messages give none.</summary>
        public static List<LiveEvent> Parse(JObject m)
        {
            var events = new List<LiveEvent>();
            if (m == null) return events;
            if (m["serverContent"] is JObject content)
            {
                if (content.Value<bool?>("interrupted") == true) events.Add(new LiveEvent { Kind = LiveEventKind.Interrupted });
                var heard = (string)content["inputTranscription"]?["text"];
                if (!string.IsNullOrEmpty(heard)) events.Add(new LiveEvent { Kind = LiveEventKind.InputText, Text = heard });
                if (content["modelTurn"]?["parts"] is JArray parts)
                    foreach (var part in parts.OfType<JObject>())
                    {
                        var inline = part["inlineData"] as JObject;
                        var mime = (string)inline?["mimeType"];
                        var data = (string)inline?["data"];
                        if (mime != null && mime.StartsWith("audio/", StringComparison.Ordinal) && !string.IsNullOrEmpty(data))
                        {
                            byte[] pcm;
                            try { pcm = Convert.FromBase64String(data); }
                            catch (FormatException) { continue; }
                            events.Add(new LiveEvent { Kind = LiveEventKind.Audio, Pcm = pcm });
                        }
                    }
                var said = (string)content["outputTranscription"]?["text"];
                if (!string.IsNullOrEmpty(said)) events.Add(new LiveEvent { Kind = LiveEventKind.OutputText, Text = said });
                if (content.Value<bool?>("turnComplete") == true) events.Add(new LiveEvent { Kind = LiveEventKind.TurnComplete });
            }
            if (m["toolCall"]?["functionCalls"] is JArray calls)
                foreach (var call in calls.OfType<JObject>())
                    events.Add(new LiveEvent { Kind = LiveEventKind.ToolCall, Id = (string)call["id"], Name = (string)call["name"], Args = Args(call["args"] as JObject) });
            if (m["toolCallCancellation"]?["ids"] is JArray ids)
                events.Add(new LiveEvent { Kind = LiveEventKind.ToolCancel, Ids = ids.Select(i => (string)i).ToArray() });
            if (m["goAway"] is JObject away)
                events.Add(new LiveEvent { Kind = LiveEventKind.GoAway, Value = Seconds((string)away["timeLeft"]) });
            if (m["sessionResumptionUpdate"] is JObject update && update.Value<bool?>("resumable") != false)
            {
                var handle = (string)update["newHandle"];
                if (!string.IsNullOrEmpty(handle)) events.Add(new LiveEvent { Kind = LiveEventKind.Handle, Text = handle });
            }
            return events;
        }

        /// <summary>A tool call's arguments as strings (objects and numbers as their JSON).</summary>
        public static IReadOnlyDictionary<string, string> Args(JObject args)
        {
            var d = new Dictionary<string, string>();
            if (args == null) return d;
            foreach (var p in args.Properties())
                d[p.Name] = p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Formatting.None);
            return d;
        }

        /// <summary>A protobuf duration ("12s", "1.5s") in seconds; 0 if unreadable.</summary>
        public static float Seconds(string duration)
        {
            if (string.IsNullOrEmpty(duration)) return 0f;
            var s = duration.TrimEnd('s');
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }
    }
}
