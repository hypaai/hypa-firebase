using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using UnityEngine;

namespace HypaStories
{
    public enum AskState { Idle, Interrupt, Listening, Thinking, Answering, Resuming }

    /// <summary>The story side of a question: what was being told, and how to stop and restart it.</summary>
    public interface INarration
    {
        bool CanAsk { get; }
        /// <summary>The interrupted line (it is restarted from its beginning afterwards).</summary>
        int LineIndex { get; }
        string StoryId { get; }
        /// <summary>The script up to and including the interrupted line, in the current language (the answer's context).</summary>
        string StorySoFar { get; }
        /// <summary>Questions to suggest at the interrupted line, in the current language: only about what has been told.</summary>
        string[] Suggestions { get; }
        /// <summary>Stop the line being read and pause the clock.</summary>
        void Interrupt();
        void Resume(int lineIndex);
    }

    /// <summary>A spoken line, ready to play. No clip = speech unavailable (no key, offline): it is "spoken" silently for Seconds.</summary>
    public sealed class SpeechLine
    {
        public string Text;
        public AudioClip Clip;
        public float Seconds;
    }

    /// <summary>The storyteller's voice for lines that are not the recorded narration: fillers, answers, TTS narration.</summary>
    public interface IStorySpeech
    {
        /// <summary>Starts fetching speech now, so a line queued behind another is ready when its turn comes. Never throws but for cancellation.</summary>
        Task<SpeechLine> PrepareAsync(string text, StoryLanguage language, CancellationToken ct);
        /// <summary>Resolves when the line has finished or was skipped. onStart(ms) fires when audio actually starts.</summary>
        Task PlayAsync(SpeechLine line, Action<double> onStart, CancellationToken ct);
        void SkipCurrent();
        void StopAll();
    }

    public interface IAskView
    {
        void SetState(AskState state, string text);
        void SetLevel(float level01);
        void SetQuestion(string text);
        void SetAnswer(string text);
        /// <summary>The questions to offer while listening (set when a question starts).</summary>
        void SetSuggestions(string[] questions);
    }

    /// <summary>Timing for one question, in ms from the end of the question. Targets: filler under 300, playback under 3000.</summary>
    public sealed class QaLogEntry
    {
        public string At, Lang, Question, Mode, Error;
        public int Line;
        public bool Typed, Filler2, Ok;
        public double? Filler, Transcript, FirstSentence, FirstAudio, Playback;
        public bool MeetsTargets => Filler < 300 && Playback < 3000;

        static string Ms(double? v) => v.HasValue ? Math.Round(v.Value).ToString(CultureInfo.InvariantCulture) : "-";

        public override string ToString() =>
            $"Q&A {Lang} line {Line}{(Typed ? " picked" : "")}: filler {Ms(Filler)} · asr {Ms(Transcript)} · 1st sentence {Ms(FirstSentence)}" +
            $" · 1st audio {Ms(FirstAudio)} · playback {Ms(Playback)} ms{(MeetsTargets ? " ✓" : "")}{(Filler2 ? " · 2nd filler" : "")}" +
            $"{(Error != null ? " ERR " + Error : "")}";
    }

    /// <summary>
    /// The interrupt-and-ask loop (tickets 3B + 3C), ported from the prototype's ask.js.
    /// States: idle → interrupt → listening → thinking (filler now, second filler after 2 s) → answering → resuming → idle.
    /// Empty transcript → "could you say that again?" and keep listening (three times, then back to the story);
    /// error / offline → "Let's keep going with the story" and resume. The narration restarts at the interrupted line.
    /// Every question writes a <see cref="QaLogEntry"/>.
    /// </summary>
    public sealed class AskFlow
    {
        readonly INarration _narration;
        readonly IStorySpeech _speech;
        readonly IListener _listener;
        readonly IStoryQa _qa;
        readonly IAskView _view;
        readonly Func<StoryLanguage> _language;
        readonly Action<bool> _onAsking;
        readonly Func<double> _clockMs;
        readonly Func<int, int> _pick;

        public readonly List<QaLogEntry> Log = new List<QaLogEntry>();
        public event Action<QaLogEntry> Logged;
        /// <summary>Each sentence of an answer as it arrives (index, text, last), for the Story Circle to share.</summary>
        public event Action<int, string, bool> Sentence;
        /// <summary>A question is over: answered, failed or cancelled (the state is back to idle).</summary>
        public event Action Ended;
        public AskState State { get; private set; }
        /// <summary>The questions offered for the current question (see <see cref="INarration.Suggestions"/>).</summary>
        public string[] Suggestions { get; private set; } = new string[0];
        public float SecondFillerSeconds = 2f;

        sealed class Session
        {
            public int Line;
            public string Context, Question;
            /// <summary>The answer's sentences that started playing: what the listener heard.</summary>
            public readonly List<string> Heard = new List<string>();
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public bool Cancelled;
            public int Unclear;
            public IListenSession Rec;
            public TaskCompletionSource<ListenResult> Typed;
        }

        Session _s;
        Task _queue = Task.CompletedTask;
        /// <summary>Earlier questions in this telling, oldest first, sent with the next one so follow-ups make sense.</summary>
        readonly List<QaTurn> _history = new List<QaTurn>();
        string _historyStory;
        const int MaxHistory = 3;
        string _lastFiller, _lastResume;

        /// <param name="onAsking">true when a question starts (duck the ambience, fireflies gather, storyteller turns), false when it ends.</param>
        public AskFlow(INarration narration, IStorySpeech speech, IListener listener, IStoryQa qa, IAskView view,
            Func<StoryLanguage> language, Action<bool> onAsking, Func<double> clockMs = null, Func<int, int> pick = null)
        {
            _narration = narration; _speech = speech; _listener = listener; _qa = qa; _view = view;
            _language = language; _onAsking = onAsking;
            _clockMs = clockMs ?? (() => Time.realtimeSinceStartupAsDouble * 1000.0);
            _pick = pick ?? (n => UnityEngine.Random.Range(0, n));
        }

        QaLines L => StoryLanguages.Lines(_language().Code);

        /// <summary>One line from the set, skipping any just spoken, including on the question before.</summary>
        string Pick(string[] lines, params string[] avoid)
        {
            if (lines == null || lines.Length == 0) return "";
            var i = _pick(lines.Length);
            if (i < 0 || i >= lines.Length) i = 0;
            if (lines.Length == 1) return lines[0];
            for (var n = 0; n < lines.Length; n++)
            {
                var s = lines[(i + n) % lines.Length];
                var skip = false;
                if (avoid != null)
                    for (var a = 0; a < avoid.Length; a++)
                        if (avoid[a] != null && avoid[a] == s) { skip = true; break; }
                if (!skip) return s;
            }
            return lines[i];
        }

        void Set(AskState s, string text = "")
        {
            State = s;
            _view.SetState(s, text);
        }

        // Lines are spoken one after another. Speech for a line is fetched as soon as it is queued, not when its turn comes.
        Task Say(Session s, string text, Action<double> onStart = null, bool answer = false, Action<double> onReady = null)
        {
            var prepared = _speech.PrepareAsync(text, _language(), s.Cts.Token);
            var prev = _queue;
            return _queue = Run();

            async Task Run()
            {
                try { await prev; } catch { /* an earlier line failing never blocks this one */ }
                if (s.Cancelled) return;
                try
                {
                    var line = await prepared;
                    onReady?.Invoke(_clockMs());
                    if (s.Cancelled) return;
                    await _speech.PlayAsync(line, t => { if (answer) { s.Heard.Add(text); _view.SetAnswer(text); } onStart?.Invoke(t); }, s.Cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't speak a line: " + e.Message); }
            }
        }

        /// <summary>
        /// True when a raised hand can start a question: with nothing being asked (the story playing), or while the storyteller
        /// thinks, answers or is getting back to the story, which a new question cuts into.
        /// </summary>
        public bool CanBegin => State == AskState.Idle ? _narration.CanAsk
            : State == AskState.Thinking || State == AskState.Answering || State == AskState.Resuming;

        /// <summary>
        /// Raise a hand / Ask. With the story playing it stops the story and listens. While the storyteller thinks or answers,
        /// that answer stops and a new question starts, at the same place in the story. No-op otherwise.
        /// </summary>
        public async Task BeginAsync()
        {
            if (!CanBegin) return;
            if (State != AskState.Idle) { await AskAgainAsync(); return; }
            var s = _s = new Session { Line = _narration.LineIndex, Context = _narration.StorySoFar };
            _view.SetSuggestions(Suggestions = _narration.Suggestions ?? new string[0]);
            Set(AskState.Interrupt, L.Listening);
            _onAsking?.Invoke(true);
            _view.SetQuestion(""); _view.SetAnswer("");
            _narration.Interrupt();
            await Loop(s);
        }

        /// <summary>A question cutting into the answer: drop that answer and listen again, keeping the story's place and context.</summary>
        async Task AskAgainAsync()
        {
            var old = _s;
            if (old == null) return;
            Remember(old);
            old.Cancelled = true;
            old.Cts.Cancel();
            old.Rec?.Cancel();
            old.Typed?.TrySetResult(new ListenResult());
            _speech.StopAll();
            _queue = Task.CompletedTask;
            var s = _s = new Session { Line = old.Line, Context = old.Context };
            _view.SetQuestion(""); _view.SetAnswer("");
            Set(AskState.Interrupt, L.Listening);
            await Loop(s);
        }

        async Task Loop(Session s)
        {
            while (!s.Cancelled)
            {
                Set(AskState.Listening, L.Listening);
                var typed = s.Typed = new TaskCompletionSource<ListenResult>();
                ListenResult r;
                try
                {
                    s.Rec = _listener.Listen(_language(), _view.SetLevel);
                    r = await await Task.WhenAny(s.Rec.Result, typed.Task);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    // No microphone or permission, or speech-to-text unavailable: a suggested question still works.
                    if (s.Cancelled) return;
                    _view.SetState(State, UnavailableMessage(e));
                    r = await typed.Task;
                }
                if (s.Cancelled) return;
                s.Typed = null;
                if (string.IsNullOrWhiteSpace(r.Transcript))
                {
                    if (++s.Unclear > 2) { await End(s, null, true); return; }
                    await Say(s, L.Again);
                    continue;
                }
                await Answer(s, r);
                return;
            }
        }

        static string UnavailableMessage(Exception e)
        {
            if (e is HypaException h && h.Kind == HypaErrorKind.MissingConfiguration) return "Speech is not configured: pick a question";
            if (e is HypaException) return "Couldn't hear that: pick a question";
            return "Microphone unavailable: pick a question";
        }

        async Task Answer(Session s, ListenResult r)
        {
            var lang = _language();
            var entry = new QaLogEntry
            {
                At = DateTime.UtcNow.ToString("o"), Lang = lang.Code, Line = s.Line, Typed = r.Typed, Question = r.Transcript, Mode = _qa.Mode,
                Transcript = r.Typed ? 0 : r.TTranscript - r.TEnd,
            };
            s.Question = r.Transcript;
            _view.SetQuestion($"{L.You}: “{r.Transcript}”");
            Set(AskState.Thinking, L.Thinking);
            var answered = false;
            // The filler starts at once, before any network work (its speech is cached when the story starts).
            var filler = Pick(L.Filler, _lastFiller);
            _lastFiller = filler;
            _ = Say(s, filler, t => entry.Filler = t - r.TEnd);
            var timer = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
            _ = SecondFiller();

            async Task SecondFiller() // still nothing after ~2 s → another "let me think"
            {
                try { await HypaAsync.Delay((int)(SecondFillerSeconds * 1000f), timer.Token); }
                catch (OperationCanceledException) { return; }
                if (answered || s.Cancelled) return;
                entry.Filler2 = true;
                var again = Pick(L.Filler, filler);
                _lastFiller = again;
                _ = Say(s, again);
            }

            try
            {
                var req = new QaRequest { Question = r.Transcript, StoryId = _narration.StoryId, StorySoFar = s.Context, Language = lang, LineIndex = s.Line,
                    History = HistoryAt(s.Line) };
                await _qa.AnswerAsync(req, (text, i, last) =>
                {
                    if (s.Cancelled) return;
                    if (i == 0)
                    {
                        entry.FirstSentence = _clockMs() - r.TEnd;
                        answered = true; timer.Cancel();
                        Set(AskState.Answering);
                    }
                    Sentence?.Invoke(i, text, last);
                    _ = Say(s, text, i == 0 ? t => entry.Playback = t - r.TEnd : (Action<double>)null, true,
                        i == 0 ? t => entry.FirstAudio = t - r.TEnd : (Action<double>)null);
                }, s.Cts.Token);
                timer.Cancel();
                if (s.Cancelled) return;
                if (!answered) { await Say(s, L.Again); await Loop(s); return; } // the service produced no answer
                await _queue;
                entry.Ok = true;
                await End(s, entry, false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                timer.Cancel();
                if (s.Cancelled) return;
                entry.Error = e.Message;
                await End(s, entry, true);
            }
        }

        // Back to the story: "Now, where was I…" then restart the interrupted line (or the offline line if something failed).
        async Task End(Session s, QaLogEntry entry, bool failed)
        {
            if (s.Cancelled) return;
            Set(AskState.Resuming);
            var back = L.Offline;
            if (!failed) { back = Pick(L.Resume, _lastResume); _lastResume = back; }
            await Say(s, back);
            if (s.Cancelled) return;
            Remember(s);
            Cleanup();
            _narration.Resume(s.Line);
            Ended?.Invoke();
            if (entry == null) return;
            Log.Add(entry);
            Logged?.Invoke(entry);
        }

        /// <summary>
        /// The questions asked so far in this telling, oldest first. A new story, or going back before a question's line (a restart),
        /// forgets them.
        /// </summary>
        List<QaTurn> HistoryAt(int line)
        {
            if (_historyStory != _narration.StoryId) { _history.Clear(); _historyStory = _narration.StoryId; }
            _history.RemoveAll(t => t.Line > line);
            return new List<QaTurn>(_history);
        }

        /// <summary>Keeps a finished, cut-off or cancelled question with what was heard of its answer. Unanswered ones are dropped.</summary>
        void Remember(Session s)
        {
            if (s.Question != null && s.Heard.Count > 0)
            {
                HistoryAt(s.Line);
                _history.Add(new QaTurn { Question = s.Question, Answer = string.Join(" ", s.Heard), Line = s.Line });
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            s.Question = null;
        }

        void Cleanup()
        {
            _speech.StopAll();
            _view.SetAnswer(""); _view.SetQuestion("");
            _onAsking?.Invoke(false);
            _s = null;
            Set(AskState.Idle);
        }

        /// <summary>Abort everything (leaving the story). Does not resume the narration.</summary>
        public void Cancel()
        {
            var s = _s;
            if (State == AskState.Idle || s == null) return;
            Remember(s);
            s.Cancelled = true;
            s.Cts.Cancel();
            s.Rec?.Cancel();
            s.Typed?.TrySetResult(new ListenResult());
            Cleanup();
            _queue = Task.CompletedTask;
            Ended?.Invoke();
        }

        /// <summary>
        /// "Stop talking" while the storyteller is mid-answer: skips the current line. Not while thinking: the corner button
        /// turns from Done to Skip as the question ends, and a Done pinch landing a moment late must not cut the "let me think".
        /// </summary>
        public void Skip()
        {
            if (State != AskState.Answering && State != AskState.Resuming) return;
            _speech.SkipCurrent();
        }

        /// <summary>End the recording now (release of a held Ask) and transcribe it.</summary>
        public void StopListening() => _s?.Rec?.Stop();

        /// <summary>A picked (or, in the editor, typed) question instead of speech.</summary>
        public void SubmitText(string question)
        {
            var s = _s;
            question = question?.Trim();
            if (string.IsNullOrEmpty(question) || State == AskState.Idle || s?.Typed == null) return;
            var now = _clockMs();
            s.Rec?.Cancel(); // first, so the recording can't win the race with an empty transcript
            s.Typed.TrySetResult(new ListenResult { Transcript = question, TEnd = now, TTranscript = now, Spoke = true, Typed = true });
        }
    }
}
