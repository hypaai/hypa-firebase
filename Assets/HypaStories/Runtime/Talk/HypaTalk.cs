using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaStories
{
    /// <summary>An open microphone: 16 kHz mono samples since the last read. <see cref="MicStream"/> on a device; fakes in tests.</summary>
    public interface ITalkMic
    {
        /// <summary>Why it stopped working, or null.</summary>
        string Error { get; }
        /// <summary>The platform takes her voice from the speaker out of the recording (<see cref="MicStream.EchoCancelled"/>).</summary>
        bool EchoCancelled { get; }
        float[] Read();
        void Stop();
    }

    /// <summary>
    /// "Pseudo-realtime" conversation on Hypa's request/response endpoints, for the languages Gemini Live does not speak well
    /// (Yorùbá, Hausa, Igbo, Pidgin): the client finds the end of a turn (<see cref="TurnDetector"/>: 700 ms of quiet), then
    /// <c>/hypa/asr</c> → <c>/hypa/chat</c> (her persona as system, then the last 8 turns) → her reply split into sentences, the
    /// first sent to <c>/hypa/tts</c> at once and the rest fetched behind it, played in order (as <see cref="AskFlow"/> speaks
    /// answers). Barge-in: 300 ms of the player's voice while she talks stops her and drops the queued sentences. A JSON
    /// <c>action</c> in her reply runs once she has said it. Main thread only (<see cref="Tick"/>).
    /// </summary>
    public sealed class HypaTalk : IVoiceConversation
    {
        const int Rate = MicStream.Rate;
        const float PreRollSeconds = 0.4f;

        readonly TalkContext _ctx;
        readonly Func<ITalkMic> _openMic;
        readonly IStorySpeech _speech;
        readonly Func<byte[], StoryLanguage, CancellationToken, Task<string>> _transcribe;
        readonly Func<ChatMessage[], CancellationToken, Task<string>> _chat;
        readonly Func<double> _clock;
        readonly Func<float> _voiceLevel;
        readonly TalkTranscript _transcript = new TalkTranscript();
        readonly CancellationTokenSource _life = new CancellationTokenSource();
        readonly List<float> _turn = new List<float>(Rate * 8);
        readonly Queue<float[]> _preRoll = new Queue<float[]>();
        int _preRollSamples;

        public readonly TurnDetector Detector = new TurnDetector();
        ITalkMic _mic;
        CancellationTokenSource _reply;
        Task _queue = Task.CompletedTask;
        int _turns, _unclear, _speaking, _micTries, _netFails;
        float _micRetry;
        bool _held, _ended;
        double _startedAt, _turnEnd;
        string _carried = "";
        TalkLine _heard;

        public TalkState State { get; private set; } = TalkState.Off;
        public RouteDecision Route { get; }
        public float VoiceLevel => _speaking > 0 ? _voiceLevel() : 0f;
        public float MicLevel { get; private set; }
        public bool MicOff => _mic == null;
        public bool Speaking => _speaking > 0;
        /// <summary>Her voice is playing (or between two of her sentences), not just a line waiting for its audio.</summary>
        bool Audible => _speaking > 0 && State == TalkState.Speaking;

        public event Action<TalkState> StateChanged;
        public event Action<TalkLine> LineChanged { add => _transcript.Changed += value; remove => _transcript.Changed -= value; }
        public event Action<string> Notice;
        public event Action<TalkTurnLog> Logged;

        /// <param name="voiceLevel">Her voice's loudness while a line plays (<see cref="StoryVoice.Level"/>).</param>
        public HypaTalk(TalkContext ctx, Func<ITalkMic> openMic, IStorySpeech speech,
            Func<byte[], StoryLanguage, CancellationToken, Task<string>> transcribe, Func<ChatMessage[], CancellationToken, Task<string>> chat,
            Func<float> voiceLevel = null, Func<double> clock = null)
        {
            _ctx = ctx; _openMic = openMic; _speech = speech; _transcribe = transcribe; _chat = chat;
            _voiceLevel = voiceLevel ?? (() => 0.5f);
            _clock = clock ?? (() => Time.realtimeSinceStartupAsDouble * 1000.0);
            Route = TalkRouter.Hypa(ctx.Language, ctx.Language.Name + ": Hypa · speech, chat and voice, turn by turn");
        }

        /// <summary>The proxy's speech to text and chat (the defaults for a device).</summary>
        public static HypaTalk OnProxy(TalkContext ctx, IStorySpeech speech, Func<float> voiceLevel)
        {
            var proxy = HypaProxyClient.FromConfig();
            return new HypaTalk(ctx, () => new MicTalkInput(MicStream.Open()), speech,
                (wav, lang, ct) => proxy.TranscribeAsync(wav, lang.HypaLabel, ct),
                async (messages, ct) =>
                {
                    try { return (await proxy.ChatAsync(new ChatRequest { messages = messages, maxTokens = 300 }, ct))?.content ?? ""; }
                    catch (HypaProxyException e) { throw e.ToHypaException(); }
                }, voiceLevel);
        }

        void Set(TalkState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _startedAt = _clock();
            OpenMic(true);
            Set(TalkState.Listening);
            var greet = _ctx.Persona.Line("greet", _ctx.Language.Code);
            if (!string.IsNullOrEmpty(greet)) Speak(new[] { greet }, null, null);
            await Task.CompletedTask;
        }

        public void Tick(float dt)
        {
            if (_ended || State == TalkState.Off) return;
            if (_ctx.MaxSeconds > 0 && _clock() - _startedAt > _ctx.MaxSeconds * 1000.0)
            {
                _ = EndAsync(_ctx.Persona.Line("timeUp", _ctx.Language.Code));
                return;
            }
            if (_mic == null && (_micRetry -= dt) <= 0f) OpenMic(false);
            if (_mic == null || _held) { MicLevel = 0f; return; }
            if (_mic.Error != null)
            {
                Notice?.Invoke(_mic.Error);
                _mic.Stop(); _mic = null;
                _micTries = MicTries; // refused in the browser: asking again would only nag
                return;
            }
            var buf = _mic.Read();
            if (buf == null || buf.Length == 0) return;
            Feed(buf);
        }

        const int MicTries = 12;

        /// <summary>
        /// Opens the microphone. The first time, the system is still asking for permission and opening fails: it is tried again
        /// every 1.5 s for a while, so she hears the player as soon as it is allowed.
        /// </summary>
        void OpenMic(bool first)
        {
            if (_openMic == null || _micTries >= MicTries) return;
            _micTries++;
            _micRetry = 1.5f;
            try
            {
                _mic = _openMic();
                Detector.CanBargeIn = _mic.EchoCancelled; // without an echo canceller she would hear herself and answer it
                if (!first) Notice?.Invoke("");
            }
            catch (Exception e)
            {
                _mic = null;
                if (first) Notice?.Invoke(e.Message);
            }
        }

        /// <summary>One block of microphone samples (public for tests).</summary>
        public void Feed(float[] buf)
        {
            var sum = 0f;
            for (var i = 0; i < buf.Length; i++) sum += buf[i] * buf[i];
            var rms = Mathf.Sqrt(sum / buf.Length);
            MicLevel = Mathf.Min(1f, rms * 6f);
            var wasIn = Detector.InTurn;
            var signal = Detector.Update(rms, buf.Length * 1000f / Rate, Audible);
            if (wasIn || Detector.InTurn) _turn.AddRange(buf);
            else if (Detector.HerTurn && !Detector.CanBargeIn) { _preRoll.Clear(); _preRollSamples = 0; } // only her echo in it
            else KeepPreRoll(buf);

            switch (signal)
            {
                case TurnDetector.Signal.BargeIn:
                    Interrupt();
                    BeginTurn(buf);
                    break;
                case TurnDetector.Signal.Started:
                    BeginTurn(buf);
                    break;
                case TurnDetector.Signal.Dropped:
                case TurnDetector.Signal.Ended:
                    TurnOver(signal, Detector.EndSilenceMs); // the voice stopped before the quiet was long enough
                    break;
            }
        }

        void TurnOver(TurnDetector.Signal signal, float quietMs)
        {
            if (signal == TurnDetector.Signal.Dropped)
            {
                _turn.Clear();
                if (State == TalkState.Hearing) Set(Speaking ? TalkState.Speaking : TalkState.Listening);
                return;
            }
            var wav = WavEncoder.Encode16(_turn.ToArray(), _turn.Count, 1, Rate);
            _turn.Clear();
            _turnEnd = _clock() - quietMs;
            _ = Respond(wav);
        }

        public bool CanEndTurn => true;

        public void EndTurn()
        {
            if (_ended || _held) return;
            var signal = Detector.Finish();
            if (signal != TurnDetector.Signal.None) TurnOver(signal, 0f);
        }

        void KeepPreRoll(float[] buf)
        {
            _preRoll.Enqueue(buf);
            _preRollSamples += buf.Length;
            while (_preRoll.Count > 1 && _preRollSamples - _preRoll.Peek().Length >= PreRollSeconds * Rate) _preRollSamples -= _preRoll.Dequeue().Length;
        }

        void BeginTurn(float[] current)
        {
            // what came just before the voice crossed the threshold: the start of the first word
            var start = new List<float>(_preRollSamples + _turn.Count);
            foreach (var b in _preRoll) start.AddRange(b);
            start.AddRange(_turn);
            _turn.Clear(); _turn.AddRange(start);
            _preRoll.Clear(); _preRollSamples = 0;
            if (_speaking > 0 && State != TalkState.Speaking) Interrupt(); // her reply's audio isn't here yet: the player goes first
            else if (State == TalkState.Thinking) _reply?.Cancel(); // more from the player: answer it all together
            Set(TalkState.Hearing);
        }

        /// <summary>Stop her now: the line playing, the sentences queued behind it, and the reply being written.</summary>
        void Interrupt()
        {
            var log = _lastLog;
            if (log != null && Speaking) log.Interrupted = true;
            _reply?.Cancel();
            _speech.StopAll();
            _queue = Task.CompletedTask;
            _speaking = 0;
        }

        TalkTurnLog _lastLog;

        async Task Respond(byte[] wav)
        {
            _reply?.Cancel();
            var cts = _reply = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            var log = new TalkTurnLog { Engine = "hypa", Lang = _ctx.Language.Code, Turn = ++_turns };
            Set(TalkState.Thinking);
            try
            {
                var heard = (await _transcribe(wav, _ctx.Language, cts.Token))?.Trim() ?? "";
                log.Transcript = _clock() - _turnEnd;
                if (cts.IsCancellationRequested) { _carried = (_carried + " " + heard).Trim(); return; }
                heard = (_carried + " " + heard).Trim();
                _carried = "";
                if (heard.Length == 0)
                {
                    if (++_unclear >= 2) { _unclear = 0; Speak(new[] { _ctx.Persona.Line("again", _ctx.Language.Code) }, null, null); }
                    else Set(TalkState.Listening);
                    return;
                }
                _unclear = 0;
                _netFails = 0;
                log.Heard = heard;
                if (_heard != null && !_heard.Final) _transcript.Update(_heard, heard, true);
                else _transcript.Add(false, heard, true);
                _heard = null;

                var content = await _chat(Messages(), cts.Token);
                log.Reply = _clock() - _turnEnd;
                if (cts.IsCancellationRequested) return;
                var reply = TalkReply.Parse(content);
                var sentences = ChatStoryQa.Split(reply.Say).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (sentences.Count == 0 && reply.Action == null) { Set(TalkState.Listening); return; }
                if (reply.Action?.Name == TalkActions.TeachWord && !string.IsNullOrWhiteSpace(reply.Action.Arg("word")))
                {
                    var w = reply.Action.Arg("word").Trim();
                    sentences.Add(w + "… " + w + "."); // said slowly, on its own
                }
                _transcript.Add(true, reply.Say, true);
                _lastLog = log;
                Speak(sentences, reply.Action, log);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) return;
                log.Error = e.Message;
                Logged?.Invoke(log);
                if (NetworkStatus.IsNetworkError(e))
                {
                    // really offline (or failing again): she says so and the conversation ends. One failed request doesn't end it
                    if (!NetworkStatus.DeviceOnline() || ++_netFails >= 2) { _ = EndAsync(_ctx.Persona.Line("offline", _ctx.Language.Code)); return; }
                    Set(TalkState.Listening);
                    Notice?.Invoke("That didn't reach her. Say it again.");
                    return;
                }
                Set(TalkState.Listening);
                Notice?.Invoke("She couldn't hear that: " + e.Message);
            }
        }

        /// <summary>Her persona as the system message, then the last turns as user / assistant (her turns as the JSON she answers in).</summary>
        public ChatMessage[] Messages()
        {
            var list = new List<ChatMessage> { new ChatMessage { role = "system", content = _ctx.Prompt(true) } };
            foreach (var (hers, text) in _transcript.Recent(_ctx.KeepTurns))
                list.Add(new ChatMessage { role = hers ? "assistant" : "user", content = hers ? new JObject { ["say"] = text, ["action"] = null }.ToString(Newtonsoft.Json.Formatting.None) : text });
            return list.ToArray();
        }

        /// <summary>Speaks the sentences in order, each fetched as soon as it is queued; then runs the action and listens again.</summary>
        void Speak(IList<string> sentences, TalkAction action, TalkTurnLog log)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            var reply = _reply = cts;
            var first = true;
            foreach (var text in sentences.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var isFirst = first;
                first = false;
                var prepared = _speech.PrepareAsync(text, _ctx.Language, cts.Token);
                var prev = _queue;
                _speaking++;
                _queue = Run();

                async Task Run()
                {
                    try
                    {
                        try { await prev; } catch { /* an earlier line failing never blocks this one */ }
                        if (cts.IsCancellationRequested) return;
                        var line = await prepared;
                        if (cts.IsCancellationRequested) return;
                        if (_held) await WaitWhileHeld(cts.Token);
                        Set(TalkState.Speaking);
                        await _speech.PlayAsync(line, t =>
                        {
                            if (!isFirst || log == null) return;
                            log.FirstAudio = t - _turnEnd;
                            Logged?.Invoke(log);
                        }, cts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { Debug.LogWarning("Hypa Stories: she couldn't say a line: " + e.Message); }
                    finally { if (_reply == reply) _speaking = Math.Max(0, _speaking - 1); }
                }
            }
            var all = _queue;
            _ = After();

            async Task After()
            {
                try { await all; } catch { }
                if (cts.IsCancellationRequested || _ended || _reply != reply) return;
                _speaking = 0;
                if (action != null)
                {
                    var result = TalkActions.Run(_ctx.Host, action);
                    Debug.Log($"Hypa Stories: she did {action} → {result}");
                }
                if (State != TalkState.Held && !Detector.InTurn) Set(TalkState.Listening);
            }
        }

        async Task WaitWhileHeld(CancellationToken ct)
        {
            while (_held && !ct.IsCancellationRequested) await Task.Yield();
        }

        public void Hold(bool on)
        {
            if (_held == on || _ended) return;
            _held = on;
            if (on)
            {
                Interrupt();
                Detector.Reset(); _turn.Clear();
                Set(TalkState.Held);
            }
            else Set(TalkState.Listening);
        }

        public async Task EndAsync(string lastLine = null)
        {
            if (_ended) return;
            _ended = true;
            Interrupt();
            if (!string.IsNullOrEmpty(lastLine))
            {
                Set(TalkState.Speaking);
                _transcript.Add(true, lastLine, true);
                try
                {
                    var line = await _speech.PrepareAsync(lastLine, _ctx.Language, _life.Token);
                    // Leaving the hut disposes her and cancels this. The goodbye must not start after that.
                    if (!_life.IsCancellationRequested)
                    {
                        _speaking = 1;
                        await _speech.PlayAsync(line, null, _life.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: she couldn't say goodbye: " + e.Message); }
                _speaking = 0;
            }
            Dispose();
            Set(TalkState.Ended);
        }

        public void Dispose()
        {
            _ended = true;
            _life.Cancel();
            _mic?.Stop(); _mic = null;
            MicLevel = 0f;
        }
    }

    /// <summary><see cref="MicStream"/> as a conversation's microphone.</summary>
    public sealed class MicTalkInput : ITalkMic
    {
        readonly MicStream _mic;
        public MicTalkInput(MicStream mic) { _mic = mic; }
        public string Error => _mic.Error;
        public bool EchoCancelled => _mic.EchoCancelled;
        public float[] Read() => _mic.Read();
        public void Stop() => _mic.Stop();
    }
}
