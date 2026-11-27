using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;

namespace HypaStories
{
    public enum TalkState
    {
        Off,
        Connecting,
        /// <summary>The mic is open and she is waiting for you (the pulsing ring).</summary>
        Listening,
        /// <summary>You are speaking.</summary>
        Hearing,
        Thinking,
        Speaking,
        /// <summary>Something else is speaking (an item's story): the conversation waits.</summary>
        Held,
        Ended,
    }

    /// <summary>One transcript bubble. The same line is raised again as it grows (Live sends its words a few at a time).</summary>
    public sealed class TalkLine
    {
        public int Id;
        public bool Hers;
        public string Text = "";
        public bool Final;
    }

    /// <summary>
    /// A voice conversation with one character (G10): open the mic, talk, interrupt, end. Two engines behind it, picked by language
    /// (<see cref="TalkRouter"/>): <see cref="LiveTalk"/> (Gemini Live through Firebase AI Logic) and <see cref="HypaTalk"/>
    /// (Hypa's speech, chat and voice through the proxy, turn by turn). Driven from the main thread by <see cref="Tick"/>.
    /// </summary>
    public interface IVoiceConversation : IDisposable
    {
        TalkState State { get; }
        /// <summary>The route chip: "Cloud · Gemini Live", "Hypa · Yorùbá".</summary>
        RouteDecision Route { get; }
        /// <summary>Her loudness now, 0..1 (lip sync).</summary>
        float VoiceLevel { get; }
        /// <summary>The player's loudness now, 0..1 (the listening ring).</summary>
        float MicLevel { get; }
        /// <summary>No microphone (refused, missing, still being allowed): she can't hear the player.</summary>
        bool MicOff { get; }

        event Action<TalkState> StateChanged;
        event Action<TalkLine> LineChanged;
        /// <summary>A short message for the panel ("Allow the microphone…").</summary>
        event Action<string> Notice;
        event Action<TalkTurnLog> Logged;

        /// <summary>Connects (Live) and greets. Throws when the engine cannot start, so the caller can try the other one.</summary>
        Task StartAsync(CancellationToken ct);
        void Tick(float dt);
        /// <summary>Whether <see cref="EndTurn"/> does anything here (not in the browser's Live session, which hears the turn itself).</summary>
        bool CanEndTurn { get; }
        /// <summary>The player is done speaking (the panel's Done): answer now, without waiting for the quiet.</summary>
        void EndTurn();
        /// <summary>Pause listening and her voice while something else speaks, then carry on.</summary>
        void Hold(bool on);
        /// <summary>Ends the conversation, with a last line if given ("the sun is going down").</summary>
        Task EndAsync(string lastLine = null);
    }

    /// <summary>Timing for one of her replies, in ms from the end of the player's speech. Targets: Live under 1000, Hypa under 4000.</summary>
    public sealed class TalkTurnLog
    {
        public string Engine, Lang, Heard, Error;
        public int Turn;
        public bool Interrupted;
        public double? Transcript, Reply, FirstAudio;

        public double TargetMs => Engine == "live" ? 1000 : 4000;
        public bool MeetsTarget => FirstAudio.HasValue && FirstAudio.Value > 0 && FirstAudio.Value < TargetMs;

        static string Ms(double? v) => v.HasValue ? Math.Round(v.Value).ToString(CultureInfo.InvariantCulture) : "-";

        public override string ToString() =>
            $"Talk {Engine} {Lang} turn {Turn}: asr {Ms(Transcript)} · reply {Ms(Reply)} · 1st audio {Ms(FirstAudio)} ms" +
            $"{(MeetsTarget ? " ✓" : "")}{(Interrupted ? " · interrupted" : "")}{(Error != null ? " ERR " + Error : "")}";
    }

    /// <summary>Which engine talks in a language. Pure, unit tested.</summary>
    public static class TalkRouter
    {
        public const string LiveName = "Gemini Live";

        /// <param name="liveReady">The platform has a Live model (Firebase started and configured).</param>
        /// <param name="hasProxy">The Hypa proxy is configured (speech, chat and voice).</param>
        public static RouteDecision Decide(StoryLanguage lang, ISet<string> liveLangs, bool liveReady, bool online, bool hasProxy)
        {
            if (!online) return new RouteDecision(Route.Offline, null, "Offline: she needs the connection to talk", 0);
            if (liveReady && liveLangs != null && liveLangs.Contains(lang.Code))
                return new RouteDecision(Route.CloudGemini, LiveName, lang.Name + " goes to the Live API", 0);
            if (hasProxy) return Hypa(lang, liveLangs != null && liveLangs.Contains(lang.Code) ? "Live is not set up here" : lang.Name + ": Hypa speaks it best");
            return new RouteDecision(Route.Offline, null, "Neither the Live API nor the Hypa proxy is configured", 0);
        }

        public static RouteDecision Hypa(StoryLanguage lang, string reason) => new RouteDecision(Route.Hypa, lang.Name, reason, 0);
    }

    /// <summary>What both engines share: the persona's prompt pieces, the language, her actions and the conversation so far.</summary>
    public sealed class TalkContext
    {
        public ElderPersona Persona;
        public StoryLanguage Language;
        public ITalkHost Host;
        public IReadOnlyList<TalkTopic> Items = new TalkTopic[0], Fire = new TalkTopic[0], Village = new TalkTopic[0];
        /// <summary>The cap on one conversation (Remote Config <c>talk_max_seconds</c>); 0 = none.</summary>
        public float MaxSeconds;
        /// <summary>Turns to keep for the Hypa chat and for a Live reconnect (the ticket: the last 8).</summary>
        public int KeepTurns = 8;

        public IReadOnlyList<CloudTool> Tools => TalkActions.Tools(ItemIds, FireIds);
        IEnumerable<string> ItemIds { get { foreach (var t in Items) yield return t.Id; } }
        IEnumerable<string> FireIds { get { foreach (var t in Fire) yield return t.Id; } }

        public string Prompt(bool jsonActions, IReadOnlyList<(bool hers, string text)> history = null)
            => PersonaPrompt.Build(Persona, Language.HypaLabel, Items, Fire, Village, jsonActions, history);
    }

    /// <summary>The transcript both engines keep: bubbles in order, the last few as chat history.</summary>
    public sealed class TalkTranscript
    {
        readonly List<TalkLine> _lines = new List<TalkLine>();
        int _next;
        public event Action<TalkLine> Changed;

        public IReadOnlyList<TalkLine> Lines => _lines;

        public TalkLine Add(bool hers, string text, bool final)
        {
            var line = new TalkLine { Id = ++_next, Hers = hers, Text = text ?? "", Final = final };
            _lines.Add(line);
            Changed?.Invoke(line);
            return line;
        }

        public void Update(TalkLine line, string text, bool final)
        {
            if (line == null) return;
            line.Text = text ?? "";
            line.Final = final;
            Changed?.Invoke(line);
        }

        /// <summary>The last <paramref name="turns"/> non-empty lines, oldest first.</summary>
        public List<(bool hers, string text)> Recent(int turns)
        {
            var r = new List<(bool, string)>();
            for (var i = _lines.Count - 1; i >= 0 && r.Count < turns; i--)
                if (!string.IsNullOrWhiteSpace(_lines[i].Text)) r.Insert(0, (_lines[i].Hers, _lines[i].Text.Trim()));
            return r;
        }
    }
}
