using System;
using System.Collections.Generic;
using System.Linq;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaStories
{
    /// <summary>
    /// What the old woman can do while she talks (G10): the hut answers each with a short result for the model. Called on the main thread.
    /// Item ids are the hut's (<c>clay-pot</c>, or <c>hut/clay-pot</c>); story ids are <see cref="StoryLibrary"/>'s.
    /// </summary>
    public interface ITalkHost
    {
        /// <summary>The item's card lights up and she gestures to it.</summary>
        string ShowItem(string id);
        /// <summary>Plays the item's existing story (the conversation waits meanwhile).</summary>
        string TellItemStory(string id);
        /// <summary>Saves the word to the player's words.</summary>
        string TeachWord(string word, string language);
        /// <summary>Offers to walk the player to the fire for that story.</summary>
        string SuggestStory(string storyId);
    }

    public sealed class TalkAction
    {
        public string Name;
        public IReadOnlyDictionary<string, string> Args = new Dictionary<string, string>();
        public string Arg(string key) => Args != null && Args.TryGetValue(key, out var v) ? v : null;
        public override string ToString() => Name + "(" + string.Join(", ", (Args ?? new Dictionary<string, string>()).Select(a => a.Key + "=" + a.Value)) + ")";
    }

    /// <summary>The actions as tools (Gemini Live function calling) and as the Hypa path's JSON <c>action</c>. Pure, unit tested.</summary>
    public static class TalkActions
    {
        public const string ShowItem = "show_item", TellItemStory = "tell_item_story", TeachWord = "teach_word", SuggestStory = "suggest_story";
        public static readonly string[] Names = { ShowItem, TellItemStory, TeachWord, SuggestStory };

        /// <summary>The tool declarations. <c>Run</c> is not set: the conversation answers calls through <see cref="Run"/>.</summary>
        public static IReadOnlyList<CloudTool> Tools(IEnumerable<string> itemIds, IEnumerable<string> storyIds)
        {
            var items = string.Join(", ", itemIds);
            var stories = string.Join(", ", storyIds);
            return new[]
            {
                new CloudTool
                {
                    Name = ShowItem, Description = "Point to one of your household things so its card lights up. Use it when you talk about a thing.",
                    Parameters = new Dictionary<string, string> { ["id"] = "The thing: one of " + items + "." },
                },
                new CloudTool
                {
                    Name = TellItemStory, Description = "Tell the full story of one of your household things, from its card. Use it when the player wants to hear about a thing at length. Say nothing more until it has finished.",
                    Parameters = new Dictionary<string, string> { ["id"] = "The thing: one of " + items + "." },
                },
                new CloudTool
                {
                    Name = TeachWord, Description = "Teach the player a word and save it to their words. After calling it, say the word slowly, twice, and what it means.",
                    Parameters = new Dictionary<string, string>
                    {
                        ["word"] = "The word, as it is written in its language.",
                        ["language"] = "The word's language code: en, fr, es, ha, ig, pcm, sw or yo.",
                    },
                },
                new CloudTool
                {
                    Name = SuggestStory, Description = "Offer to walk the player to the campfire to hear one of the storyteller's stories.",
                    Parameters = new Dictionary<string, string> { ["storyId"] = "The story: one of " + stories + "." },
                },
            };
        }

        /// <summary>Does the action and returns its result for the model. Unknown actions and missing arguments are told back, never thrown.</summary>
        public static string Run(ITalkHost host, TalkAction a)
        {
            if (a == null || string.IsNullOrEmpty(a.Name)) return "No action.";
            try
            {
                switch (a.Name)
                {
                    case ShowItem: return Need(a, "id") ?? host.ShowItem(a.Arg("id"));
                    case TellItemStory: return Need(a, "id") ?? host.TellItemStory(a.Arg("id"));
                    case TeachWord: return Need(a, "word") ?? host.TeachWord(a.Arg("word"), a.Arg("language"));
                    case SuggestStory: return Need(a, "storyId") ?? host.SuggestStory(a.Arg("storyId"));
                    default: return "There is no action called " + a.Name + ".";
                }
            }
            catch (Exception e) { return "That did not work: " + e.Message; }
        }

        static string Need(TalkAction a, string key) => string.IsNullOrWhiteSpace(a.Arg(key)) ? $"{a.Name} needs \"{key}\"." : null;

        /// <summary>"clay-pot" or "hut/clay-pot" as the hut's id ("hut/clay-pot").</summary>
        public static string ItemId(string id)
        {
            id = id?.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
            if (string.IsNullOrEmpty(id)) return id;
            return id.StartsWith("hut/", StringComparison.Ordinal) ? id : "hut/" + id;
        }
    }

    /// <summary>
    /// Her reply on the Hypa path: the chat model is asked for <c>{"say": "...", "action": {"name": ..., "args": {...}} | null}</c>.
    /// Read leniently: plain text is all speech; a JSON object (whole, fenced, or after the words) gives the action. Pure, unit tested.
    /// </summary>
    public sealed class TalkReply
    {
        public string Say = "";
        public TalkAction Action;

        public static TalkReply Parse(string content)
        {
            var text = (content ?? "").Trim();
            var reply = new TalkReply();
            if (text.Length == 0) return reply;
            text = StripFence(text);
            var at = text.IndexOf('{');
            if (at < 0) { reply.Say = text; return reply; }
            var json = Balanced(text, at);
            JObject o = null;
            if (json != null)
            {
                try { o = JObject.Parse(json); }
                catch (JsonException) { }
            }
            if (o == null) { reply.Say = text; return reply; }
            var outside = (text.Substring(0, at) + " " + text.Substring(at + json.Length)).Trim();
            var said = (string)(o["say"] ?? o["text"] ?? o["reply"] ?? o["content"]);
            reply.Say = Clean(string.IsNullOrWhiteSpace(said) ? outside : said);
            reply.Action = ActionFrom(o["action"] ?? (o["name"] != null ? o : null));
            return reply;
        }

        static TalkAction ActionFrom(JToken t)
        {
            if (t is JValue v && v.Type == JTokenType.String) return Known((string)v) ? new TalkAction { Name = (string)v } : null;
            if (!(t is JObject a)) return null;
            var name = (string)(a["name"] ?? a["type"] ?? a["action"]);
            if (!Known(name)) return null;
            var args = a["args"] as JObject ?? a["arguments"] as JObject;
            if (args == null) // flat: {"name": "show_item", "id": "clay-pot"}
            {
                args = new JObject();
                foreach (var p in a.Properties()) if (p.Name != "name" && p.Name != "type" && p.Name != "action") args[p.Name] = p.Value;
            }
            return new TalkAction { Name = name, Args = LiveProtocol.Args(args) };
        }

        static bool Known(string name) => name != null && TalkActions.Names.Contains(name);

        static string StripFence(string s)
        {
            if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
            var nl = s.IndexOf('\n');
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            return nl > 0 && end > nl ? s.Substring(nl + 1, end - nl - 1).Trim() : s;
        }

        /// <summary>The JSON object starting at <paramref name="start"/>, braces balanced outside strings; null if it never closes.</summary>
        static string Balanced(string s, int start)
        {
            var depth = 0;
            var quoted = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (quoted)
                {
                    if (c == '\\') i++;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return s.Substring(start, i - start + 1);
            }
            return null;
        }

        static string Clean(string s) => (s ?? "").Replace("```", "").Trim();
    }
}
