using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The old woman's persona (G10): Resources/Characters/elder/persona.json. Who she is, what she knows, how she talks, the stories
    /// she can send you to, and her few fixed lines in each language (greeting, offline, time's up, "say that again"). What her
    /// household things are comes from their own story.json files (<see cref="HutItems"/>), so the two never disagree.
    /// The non-European lines are machine-written and want a native speaker's check, like the rest of the hut's lines.
    /// </summary>
    public sealed class ElderPersona
    {
        public const string ResourcePath = "Characters/elder/persona";

        public string Id = "elder", Role = "the old woman of the hut";
        public string[] Who = new string[0], Knows = new string[0], Rules = new string[0];
        public string[] StoriesByTheFire = new string[0], StoriesInTheVillage = new string[0];
        /// <summary>Her voices, as a story's <c>voices</c>: Hypa's Auto picks a man's voice for some languages (English: OpenAI alloy).</summary>
        public VoiceOption[] Voices = new VoiceOption[0];
        readonly Dictionary<string, Dictionary<string, string>> _lines = new Dictionary<string, Dictionary<string, string>>();

        static ElderPersona _loaded;
        public static ElderPersona Load()
        {
            if (_loaded != null) return _loaded;
            var asset = Resources.Load<TextAsset>(ResourcePath);
            if (asset == null) throw new InvalidOperationException("No persona at Resources/" + ResourcePath + ".json.");
            return _loaded = Parse(asset.text);
        }

        public static ElderPersona Parse(string json)
        {
            var o = JObject.Parse(json);
            string[] Strings(string key) => (o[key] as JArray)?.Select(t => (string)t).Where(s => !string.IsNullOrEmpty(s)).ToArray() ?? new string[0];
            var p = new ElderPersona
            {
                Id = (string)o["id"] ?? "elder", Role = (string)o["role"] ?? "the old woman of the hut",
                Who = Strings("who"), Knows = Strings("knows"), Rules = Strings("rules"),
                StoriesByTheFire = Strings("storiesByTheFire"), StoriesInTheVillage = Strings("storiesInTheVillage"),
                Voices = ((o["voices"] as JArray) ?? new JArray()).OfType<JObject>().Select(StoryLibrary.Voice).ToArray(),
            };
            if (o["lines"] is JObject lines)
                foreach (var kind in lines.Properties())
                    if (kind.Value is JObject byLang)
                        p._lines[kind.Name] = byLang.Properties().ToDictionary(l => l.Name, l => (string)l.Value);
            return p;
        }

        /// <summary>Her voices for a language, best first (as <see cref="StoryInfo.VoicesFor"/>); empty = Hypa's Auto. Pure.</summary>
        public VoiceOption[] VoicesFor(string lang)
            => Voices.Where(v => !string.IsNullOrEmpty(v.For(lang))).Select(v => new VoiceOption { Model = v.Model, Voice = v.For(lang) }).ToArray();

        /// <summary>A fixed line ("greet", "offline", "timeUp", "again") in the language, else English, else "".</summary>
        public string Line(string kind, string lang)
        {
            if (!_lines.TryGetValue(kind, out var byLang)) return "";
            if (lang != null && byLang.TryGetValue(lang, out var s) && !string.IsNullOrEmpty(s)) return s;
            return byLang.TryGetValue("en", out var en) ? en ?? "" : "";
        }
    }

    /// <summary>Something she knows about: an id the actions use, a name, and what she would say about it.</summary>
    public sealed class TalkTopic
    {
        public string Id, Title, About;
        public TalkTopic(string id, string title, string about) { Id = id; Title = title; About = about; }

        /// <summary>A hut item from its story: the id without "hut/", the card's title, and her lines (English).</summary>
        public static TalkTopic Item(StoryInfo story) => new TalkTopic(
            story.Id.StartsWith("hut/", StringComparison.Ordinal) ? story.Id.Substring(4) : story.Id, OneLine(story.Title),
            string.Join(" ", story.Timeline.Captions.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t))));

        /// <summary>A story from its card: its id, title and description.</summary>
        public static TalkTopic Story(StoryInfo story) => new TalkTopic(story.Id, OneLine(story.Title), story.Desc ?? "");

        static string OneLine(string s) => (s ?? "").Replace('\n', ' ').Trim();
    }

    /// <summary>Her system prompt for either engine. Pure, unit tested.</summary>
    public static class PersonaPrompt
    {
        /// <param name="language">The language she starts in (its Hypa label, e.g. "Yoruba").</param>
        /// <param name="items">Her household things (<see cref="TalkTopic.Item"/>).</param>
        /// <param name="fire">Stories she can send the player to the fire for (<c>suggest_story</c>).</param>
        /// <param name="village">Stories the village elder tells, which she may mention.</param>
        /// <param name="jsonActions">The Hypa path: no function calling, so she answers in JSON with an optional action.</param>
        public static string Build(ElderPersona p, string language, IEnumerable<TalkTopic> items, IEnumerable<TalkTopic> fire,
            IEnumerable<TalkTopic> village, bool jsonActions, IReadOnlyList<(bool hers, string text)> history = null)
        {
            var b = new StringBuilder();
            foreach (var s in p.Who) b.Append(s).Append('\n');
            b.Append('\n');
            foreach (var s in p.Knows) b.Append(s).Append('\n');

            var itemList = items?.ToList() ?? new List<TalkTopic>();
            b.Append("\nYour household things (ids in brackets):\n");
            foreach (var t in itemList) b.Append("- ").Append(t.Title).Append(" [").Append(t.Id).Append("]: ").Append(t.About).Append('\n');

            var fireList = fire?.ToList() ?? new List<TalkTopic>();
            if (fireList.Count > 0)
            {
                b.Append("\nStories the storyteller tells at the campfire (ids in brackets):\n");
                foreach (var t in fireList) b.Append("- ").Append(t.Title).Append(" [").Append(t.Id).Append("]: ").Append(t.About).Append('\n');
            }
            var villageList = village?.ToList() ?? new List<TalkTopic>();
            if (villageList.Count > 0)
            {
                b.Append("\nStories the village elder tells outside, in the village:\n");
                foreach (var t in villageList) b.Append("- ").Append(t.Title).Append(": ").Append(t.About).Append('\n');
            }

            b.Append("\nHow you talk:\n");
            foreach (var s in p.Rules) b.Append("- ").Append(s).Append('\n');
            b.Append("- Begin in ").Append(string.IsNullOrEmpty(language) ? "English" : language).Append(".\n");

            b.Append("\nWhat you can do while you talk:\n");
            b.Append("- show_item(id): point to one of your things so its card lights up, when you talk about it.\n");
            b.Append("- tell_item_story(id): tell a thing's full story, when the player wants to hear all about it.\n");
            b.Append("- teach_word(word, language): teach the player a word in their language and save it for them; then say it slowly, twice.\n");
            if (fireList.Count > 0) b.Append("- suggest_story(storyId): offer to walk the player to the campfire for one of the storyteller's stories.\n");
            b.Append("Use them now and then, when they fit, never more than one per turn.\n");

            if (jsonActions)
            {
                b.Append("\nAnswer with one JSON object and nothing else: {\"say\": \"the words you speak\", \"action\": null}. ");
                b.Append("To do something, set action, for example {\"say\": \"This is my water pot.\", \"action\": {\"name\": \"show_item\", \"args\": {\"id\": \"")
                    .Append(itemList.Count > 0 ? itemList[0].Id : "clay-pot").Append("\"}}}. ");
                b.Append("\"say\" is in the player's language and follows every rule above.\n");
            }

            if (history != null && history.Count > 0)
            {
                b.Append("\nThe conversation so far (carry on from it; do not greet again):\n");
                foreach (var (hers, text) in history) b.Append(hers ? "You: " : "Player: ").Append(text).Append('\n');
            }
            return b.ToString().TrimEnd();
        }
    }
}
