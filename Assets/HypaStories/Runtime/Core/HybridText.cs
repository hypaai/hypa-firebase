using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HypaCore.Hybrid;

namespace HypaStories
{
    public sealed class StoryPrompt
    {
        public string System, User;
        public string Text => System + "\n\n" + User;

        public static StoryPrompt Build(QaRequest req, string title, int maxTokens = int.MaxValue, bool englishIfUnsure = false)
        {
            var lang = req.Language?.HypaLabel ?? "English";
            var name = string.IsNullOrEmpty(title) ? "this story" : $"“{title.Replace('\n', ' ')}”";
            var system =
                $"You are the storyteller of {name}, telling it aloud to a listener in virtual reality. " +
                "The listener has paused the story to ask you a question.\n" +
                "Use only what the story has told so far. Never reveal or hint at what happens next: if the answer comes later in " +
                "the story, warmly tell them they will find out soon.\n" +
                "If the question is not about the story, answer briefly and kindly, then lead back to the story. " +
                "Stay in your storyteller's voice, suitable for all ages. No lists, headings, markdown, emoji or stage directions.";
            var answer = $"Answer in {lang}, in 2 or 3 short sentences that sound natural spoken aloud. No spoilers.";
            if (englishIfUnsure && lang != "English")
                answer += $" If you cannot answer well in {lang}, answer in English and begin with “(In English)”.";

            string User(string story) => $"The story so far:\n{story}\n\nThe listener asks: {req.Question}\n\n{answer}";

            var full = req.StorySoFar ?? "";
            var prompt = new StoryPrompt { System = system, User = User(full) };
            if (HybridRouter.Tokens(prompt.Text) <= maxTokens) return prompt;
            var fixedTokens = HybridRouter.Tokens(new StoryPrompt { System = system, User = User("") }.Text) + 1;
            prompt.User = User(Trim(full, (maxTokens - fixedTokens) * 4));
            return prompt;
        }

        static string Trim(string story, int chars)
        {
            if (chars <= 1) return "";
            if (story.Length <= chars) return story;
            var tail = story.Substring(story.Length - (chars - 1));
            var line = tail.IndexOf('\n');
            return "…" + (line >= 0 && line < tail.Length / 2 ? tail.Substring(line + 1) : tail);
        }
    }

    public sealed class SentenceStream
    {
        readonly Action<string, int, bool> _on;
        readonly Func<string, bool> _accept;
        readonly StringBuilder _raw = new StringBuilder();
        int _parts;

        public int Count { get; private set; }
        public bool Enough => _parts >= ChatStoryQa.MaxSentences;

        public SentenceStream(Action<string, int, bool> on, Func<string, bool> accept = null) { _on = on; _accept = accept; }

        public void Push(string chunk)
        {
            if (Enough || string.IsNullOrEmpty(chunk)) return;
            _raw.Append(chunk);
            var parts = ChatStoryQa.Split(_raw.ToString());
            _parts = parts.Count;
            while (Count < parts.Count - 1 && Count < ChatStoryQa.MaxSentences - 1) Emit(parts[Count], false);
        }

        public void Finish(string tail = null)
        {
            var parts = ChatStoryQa.Split(_raw.ToString());
            var rest = Count < parts.Count && Count < ChatStoryQa.MaxSentences ? parts[Count] : null;
            if (rest != null) Emit(rest, string.IsNullOrEmpty(tail));
            if (!string.IsNullOrEmpty(tail) && Count > 0) Emit(tail, true);
        }

        void Emit(string sentence, bool last)
        {
            if (Count == 0 && _accept != null && !_accept(sentence)) throw new WrongLanguageException(sentence);
            _on(sentence, Count++, last);
        }
    }

    public sealed class WrongLanguageException : Exception
    {
        public WrongLanguageException(string sentence) : base("The answer came in another language: “" + sentence + "”") { }
    }

    public static class ReplyLanguage
    {
        static readonly Dictionary<string, HashSet<string>> Common = new Dictionary<string, HashSet<string>>
        {
            ["en"] = Words("the and is was are were to of he she it they you his her not with that this will have"),
            ["fr"] = Words("le les et est était il elle des du une que qui pas vous tu dans sur ce mais avec son sa"),
            ["es"] = Words("el los las y es era del una que no en con su se pero está por para muy"),
        };

        static HashSet<string> Words(string s) => new HashSet<string>(s.Split(' '));
        static readonly Regex Word = new Regex(@"\p{L}+", RegexOptions.CultureInvariant);

        public static string Guess(string text)
        {
            var words = Word.Matches((text ?? "").ToLowerInvariant()).Cast<Match>().Select(m => m.Value).ToList();
            var scores = Common.ToDictionary(c => c.Key, c => words.Count(c.Value.Contains));
            var best = scores.OrderByDescending(s => s.Value).ToList();
            if (best[0].Value < 2 || best[0].Value < best[1].Value * 2) return null;
            return best[0].Key;
        }

        public static bool Mismatch(string text, string code)
        {
            if (!Common.ContainsKey(code ?? "")) return false;
            var guess = Guess(text);
            return guess != null && guess != code;
        }
    }

    public static class ToolQuestion
    {
        static readonly Regex Ask = new Regex(
            @"\bwhat\s+(does|do|did)\s+.+\s+mean\b|\bmeaning\s+of\b|\bhow\s+(do|would|can|could)\s+(you|i|we)\s+say\b|\btranslat" +
            @"|\bque\s+(veut\s+dire|signifie)\b|\bcomment\s+(dit-on|on\s+dit|dire)\b|\bça\s+veut\s+dire\b" +
            @"|\bqu[eé]\s+(significa|quiere\s+decir)\b|\bc[oó]mo\s+se\s+dice\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool Matches(string question) => !string.IsNullOrWhiteSpace(question) && Ask.IsMatch(question);
    }
}
