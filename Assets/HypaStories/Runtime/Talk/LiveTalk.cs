using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The conversation on the Gemini Live API through Firebase AI Logic (English, French, Spanish, Kiswahili: Remote Config
    /// <c>live_langs</c>, model <c>live_model</c>). One open session: the model hears the stream, finds the turns, can be
    /// interrupted, sends transcripts of both sides and calls her actions as functions. On native the microphone and her voice
    /// go through here (<see cref="LivePlayback"/>); in the browser the page does both (<see cref="ILiveSession.OwnsAudio"/>).
    ///
    /// While she speaks on native, the microphone sends silence until the player has been loud enough for 300 ms
    /// (<see cref="TurnDetector"/>), so her voice from the speaker doesn't interrupt her; the model then hears the player
    /// from just before they began. With no echo canceller (the editor, a desktop) it sends silence for all of her turn. A session the server closes (its time limit, a dropped connection) is reopened with its
    /// resumption handle, or failing that with the last turns in her prompt.
    /// </summary>
    public sealed class LiveTalk : IVoiceConversation
    {
        const int Rate = MicStream.Rate;
        const float PreRollSeconds = 0.4f;
        const int MaxReconnects = 3;
        /// <summary>Another woman's voice, for when <c>live_voice</c> is refused: the model's default (Puck) is a man's.</summary>
        public const string FallbackVoice = "Aoede";

        readonly TalkContext _ctx;
        readonly ILiveModel _model;
        readonly string _modelName;
        string _voice;
        readonly Func<ITalkMic> _openMic;
        readonly LivePlayback _playback;
        readonly Func<double> _clock;
        readonly TalkTranscript _transcript = new TalkTranscript();
        readonly CancellationTokenSource _life = new CancellationTokenSource();
        readonly Queue<float[]> _preRoll = new Queue<float[]>();
        int _preRollSamples;

        public readonly TurnDetector Detector = new TurnDetector();
        ILiveSession _session;
        ITalkMic _mic;
        string _handle;
        TalkLine _heard, _said;
        TalkTurnLog _log;
        int _turns, _reconnects, _micTries;
        float _micRetry;
        bool _held, _ended, _ending, _reconnecting, _turnDone, _gateOpen, _barged, _micNoticed, _spokeThisTurn;
        double _startedAt, _lastSpeech;
        float _quietFor, _hush;

        public TalkState State { get; private set; } = TalkState.Off;
        public RouteDecision Route { get; }
        public float VoiceLevel => _session == null ? 0f : _session.OwnsAudio ? _session.VoiceLevel : _playback != null ? _playback.Level : 0f;
        public float MicLevel { get; private set; }
        public bool MicOff => _session != null && (_session.OwnsAudio ? _session.MicDenied : _mic == null);

        public event Action<TalkState> StateChanged;
        public event Action<TalkLine> LineChanged { add => _transcript.Changed += value; remove => _transcript.Changed -= value; }
        public event Action<string> Notice;
        public event Action<TalkTurnLog> Logged;

        bool SheSpeaks => _session != null && (_session.OwnsAudio ? _session.VoiceLevel > 0.02f : _playback != null && _playback.Playing);

        /// <param name="playback">Her voice on native; null in the browser.</param>
        /// <param name="echoFactor">How much louder than her echo the player must be to interrupt her; 0 = by device.</param>
        public LiveTalk(TalkContext ctx, ILiveModel model, string modelName, string voice, Func<ITalkMic> openMic, LivePlayback playback,
            Func<double> clock = null, float echoFactor = 0f)
        {
            _ctx = ctx; _model = model; _modelName = modelName; _voice = voice; _openMic = openMic; _playback = playback;
            _clock = clock ?? (() => Time.realtimeSinceStartupAsDouble * 1000.0);
            Route = new RouteDecision(HypaCore.Hybrid.Route.CloudGemini, TalkRouter.LiveName, ctx.Language.Name + " on the Live API (" + modelName + ")", 0);
            // the headset's speakers sit right by its microphone: she must be talked over more firmly there
            Detector.EchoFactor = echoFactor > 0f ? echoFactor : SystemInfo.deviceModel.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 ? 4f : 2.5f;
        }

        void Set(TalkState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        LiveSetup Setup(string handle, bool withHistory) => new LiveSetup
        {
            Model = _modelName,
            SystemInstruction = _ctx.Prompt(false, withHistory ? _transcript.Recent(_ctx.KeepTurns) : null),
            Voice = _voice,
            ResumeHandle = handle,
            Tools = _ctx.Tools,
        };

        public async Task StartAsync(CancellationToken ct)
        {
            Set(TalkState.Connecting);
            _startedAt = _clock();
            try { _session = await _model.ConnectAsync(Setup(null, false), ct); }
            catch (Exception e) when (!(e is OperationCanceledException) && !string.IsNullOrEmpty(_voice) && _voice != FallbackVoice)
            {
                Debug.LogWarning($"Hypa Stories: Live refused the session with voice {_voice} ({e.Message}); trying {FallbackVoice}.");
                _voice = FallbackVoice; // and for any reconnect
                _session = await _model.ConnectAsync(Setup(null, false), ct);
            }
            if (!_session.OwnsAudio) OpenMic(true);
            Set(TalkState.Listening);
            // she speaks first, in the player's language
            _session.SendText($"(The player has just come into your hut and sat down in front of you. Greet them warmly in {_ctx.Language.HypaLabel}, in one short sentence, and ask their name.)");
            _gateOpen = true;
        }

        public void Tick(float dt)
        {
            if (_ended || _session == null) return;
            if (_ctx.MaxSeconds > 0 && _clock() - _startedAt > _ctx.MaxSeconds * 1000.0)
            {
                _ = EndAsync(_ctx.Persona.Line("timeUp", _ctx.Language.Code));
                return;
            }
            if (_session.OwnsAudio)
            {
                MicLevel = _session.MicLevel;
                if (_session.MicDenied && !_micNoticed) { _micNoticed = true; Notice?.Invoke("The microphone is off: allow it to talk with her."); }
            }
            else
            {
                if (_mic == null && (_micRetry -= dt) <= 0f) OpenMic(false);
                PumpMic();
            }
            while (_session != null && _session.TryDequeue(out var e)) Handle(e);

            // her turn is over once its words are all said and her voice has stopped
            _quietFor = SheSpeaks ? 0f : _quietFor + dt;
            if (State == TalkState.Speaking && _turnDone && _quietFor > 0.35f) Set(TalkState.Listening);
        }

        const int MicTries = 12;

        /// <summary>Opens the microphone, and again every 1.5 s for a while if the system was still asking for permission.</summary>
        void OpenMic(bool first)
        {
            if (_openMic == null || _micTries >= MicTries) return;
            _micTries++;
            _micRetry = 1.5f;
            try { _mic = _openMic(); Detector.CanBargeIn = _mic.EchoCancelled; }
            catch (Exception e)
            {
                _mic = null;
                if (first) Notice?.Invoke(e.Message);
            }
        }

        void PumpMic()
        {
            if (_mic == null) { MicLevel = 0f; return; }
            if (_mic.Error != null) { Notice?.Invoke(_mic.Error); _mic.Stop(); _mic = null; _micTries = MicTries; return; }
            var buf = _mic.Read();
            if (buf == null || buf.Length == 0) return;
            var sum = 0f;
            for (var i = 0; i < buf.Length; i++) sum += buf[i] * buf[i];
            var rms = Mathf.Sqrt(sum / buf.Length);
            MicLevel = Mathf.Min(1f, rms * 6f);
            if (_held || _reconnecting) return;
            if (_hush > 0f)
            {
                // Done was pressed: silence, so the model's own turn detection hears the end now
                _hush -= buf.Length / (float)Rate;
                _session.SendAudio(new float[buf.Length], buf.Length);
                return;
            }

            var speaks = SheSpeaks;
            var signal = Detector.Update(rms, buf.Length * 1000f / Rate, speaks);
            if (rms > Detector.Level(speaks)) _lastSpeech = _clock();
            if (!Detector.HerTurn) { _gateOpen = true; _barged = false; }
            else if (signal == TurnDetector.Signal.BargeIn)
            {
                // the player means it: let the model hear them, from just before they began
                if (!_gateOpen) foreach (var b in _preRoll) _session.SendAudio(b, b.Length);
                _gateOpen = true; _barged = true;
            }
            // she is speaking and the player is not, or there is no echo canceller (the model would hear her and stop itself): silence
            else if (!Detector.CanBargeIn || (!_barged && !Detector.InTurn)) _gateOpen = false;

            KeepPreRoll(buf);
            if (_gateOpen) _session.SendAudio(buf, buf.Length);
            else _session.SendAudio(new float[buf.Length], buf.Length); // silence keeps the stream's clock running
        }

        void KeepPreRoll(float[] buf)
        {
            _preRoll.Enqueue(buf);
            _preRollSamples += buf.Length;
            while (_preRoll.Count > 1 && _preRollSamples - _preRoll.Peek().Length >= PreRollSeconds * Rate) _preRollSamples -= _preRoll.Dequeue().Length;
        }

        void Handle(LiveEvent e)
        {
            switch (e.Kind)
            {
                case LiveEventKind.Audio:
                    if (_held) break;
                    FirstAudio(0);
                    _playback?.Enqueue(LiveProtocol.Floats(e.Pcm));
                    break;
                case LiveEventKind.FirstAudio:
                    FirstAudio(e.Value);
                    break;
                case LiveEventKind.Interrupted:
                    _playback?.Clear();
                    if (_log != null) _log.Interrupted = true;
                    if (_said != null) _transcript.Update(_said, _said.Text, true);
                    _said = null;
                    _spokeThisTurn = false;
                    Set(TalkState.Hearing);
                    break;
                case LiveEventKind.InputText:
                    if (_heard == null || _heard.Final) _heard = _transcript.Add(false, "", false);
                    _transcript.Update(_heard, Join(_heard.Text, e.Text), false);
                    if (State != TalkState.Speaking) Set(TalkState.Hearing);
                    break;
                case LiveEventKind.OutputText:
                    if (_heard != null && !_heard.Final) _transcript.Update(_heard, _heard.Text, true);
                    if (_said == null || _said.Final) _said = _transcript.Add(true, "", false);
                    _transcript.Update(_said, Join(_said.Text, e.Text), false);
                    break;
                case LiveEventKind.TurnComplete:
                    if (_heard != null && !_heard.Final) _transcript.Update(_heard, _heard.Text, true);
                    if (_said != null) _transcript.Update(_said, _said.Text, true);
                    _said = null;
                    _turnDone = true;
                    _spokeThisTurn = false;
                    if (State != TalkState.Speaking) Set(TalkState.Listening);
                    break;
                case LiveEventKind.ToolCall:
                    var result = TalkActions.Run(_ctx.Host, new TalkAction { Name = e.Name, Args = e.Args });
                    Debug.Log($"Hypa Stories: she did {e.Name} → {result}");
                    _session.SendToolResponse(e.Id, e.Name, result);
                    break;
                case LiveEventKind.Handle:
                    _handle = e.Text;
                    break;
                case LiveEventKind.GoAway:
                    if (!_ending) _ = Reconnect("the server's time limit");
                    break;
                case LiveEventKind.Closed:
                    if (!_ended && !_ending && !_reconnecting) _ = Reconnect(e.Error ?? "closed");
                    break;
            }
        }

        /// <summary>Her voice started this turn: time it from the end of the player's speech (the page measures it in the browser).</summary>
        void FirstAudio(float measuredMs)
        {
            if (_spokeThisTurn) return;
            _spokeThisTurn = true;
            _turnDone = false;
            Set(TalkState.Speaking);
            var heard = _heard?.Text;
            if (_turns == 0 && string.IsNullOrEmpty(heard)) { _turns++; return; } // her greeting
            _log = new TalkTurnLog { Engine = "live", Lang = _ctx.Language.Code, Turn = ++_turns, Heard = heard };
            _log.FirstAudio = measuredMs > 0 ? measuredMs : _lastSpeech > 0 ? _clock() - _lastSpeech : (double?)null;
            Logged?.Invoke(_log);
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return (b ?? "").TrimStart();
            if (string.IsNullOrEmpty(b)) return a;
            return a + b; // the pieces carry their own spaces
        }

        async Task Reconnect(string why)
        {
            if (_reconnecting || _ended) return;
            _reconnecting = true;
            var old = _session;
            Debug.Log($"Hypa Stories: reopening the Live session ({why}).");
            try
            {
                if (++_reconnects > MaxReconnects) throw new InvalidOperationException("the connection keeps dropping");
                ILiveSession next = null;
                if (!string.IsNullOrEmpty(_handle))
                {
                    try { next = await _model.ConnectAsync(Setup(_handle, false), _life.Token); }
                    catch (Exception e) when (!(e is OperationCanceledException)) { Debug.LogWarning("Hypa Stories: couldn't resume the Live session: " + e.Message); }
                }
                if (next == null) next = await _model.ConnectAsync(Setup(null, true), _life.Token); // the last turns in her prompt
                if (_ended) { next.Dispose(); return; }
                _session = next;
                if (_held) next.SetHeld(true);
                old?.Dispose();
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogWarning("Hypa Stories: the Live session is gone: " + e.Message);
                Notice?.Invoke("She lost the connection.");
                old?.Dispose();
                _session = null;
                _ended = true;
                Set(TalkState.Ended);
            }
            finally { _reconnecting = false; }
        }

        public bool CanEndTurn => _session != null && !_session.OwnsAudio;

        public void EndTurn()
        {
            if (_ended || _held || !CanEndTurn) return;
            Detector.Finish();
            _lastSpeech = _clock();
            _hush = 1.2f; // longer than the server's end-of-speech silence
            Set(TalkState.Thinking);
        }

        public void Hold(bool on)
        {
            if (_held == on || _ended) return;
            _held = on;
            _session?.SetHeld(on);
            _playback?.Clear();
            Detector.Reset();
            Set(on ? TalkState.Held : TalkState.Listening);
            // the only hold is an item's story: when it is over, she picks the conversation up again
            if (!on) _session?.SendText("(The story has finished. Ask the player, in one short sentence, what they thought of it.)");
        }

        public async Task EndAsync(string lastLine = null)
        {
            if (_ended || _ending) return;
            _ending = true;
            if (!string.IsNullOrEmpty(lastLine) && _session != null && _session.Open && !_held)
            {
                // in her own voice: ask her to say goodbye, and give her a few seconds to
                _turnDone = false;
                _session.SendText($"(The visit is over now. Say a warm goodbye to the player in {_ctx.Language.HypaLabel}, in one sentence, like: \"{lastLine}\")");
                var until = _clock() + 7000;
                var started = false;
                while (_clock() < until && _session != null && !_ended)
                {
                    while (_session.TryDequeue(out var e)) Handle(e);
                    if (SheSpeaks) started = true;
                    if (started && _turnDone && !SheSpeaks) break;
                    await Task.Yield();
                }
            }
            _ended = true;
            Dispose();
            Set(TalkState.Ended);
        }

        public void Dispose()
        {
            _ended = true;
            _life.Cancel();
            _session?.Dispose(); _session = null;
            _mic?.Stop(); _mic = null;
            _playback?.Clear();
            MicLevel = 0f;
        }
    }
}
