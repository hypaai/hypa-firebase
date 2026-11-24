using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaStories
{
    /// <summary>Asks the Firebase proxy once per question and speaks its answer in the listener's language.</summary>
    public sealed class ChatStoryQa : IStoryQa
    {
        /// <summary>Reasoning models think before answering, and max_tokens covers that too.</summary>
        const int MaxTokens = 1200, MaxSentences = 4;
        // The proxy has a 20-second budget. Leave room for sign-in, quota checks, and the response.
        public int DeadlineMs = 25000;

        readonly HypaRestClient _rest = new HypaRestClient();

        public string Mode => "chat";

        /// <summary>A story can pin one model. Other stories use the proxy's fallback order.</summary>
        public static string RequestedModel(StoryInfo story)
        {
            var models = story?.Models;
            return models?.Length == 1 && !string.IsNullOrWhiteSpace(models[0]) ? models[0] : "auto";
        }

        public async Task AnswerAsync(QaRequest req, Action<string, int, bool> onSentence, CancellationToken ct)
        {
            var story = StoryLibrary.Find(req.StoryId);
            var messages = Messages(req, story?.Title);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(DeadlineMs);
                try
                {
                    var reply = await _rest.ChatWithRouteAsync(RequestedModel(story), messages,
                        req.Language?.HypaLabel ?? "English", MaxTokens, deadline.Token);
                    var sentences = Sentences(reply.content);
                    if (sentences.Count == 0)
                        throw new HypaException(HypaErrorKind.InvalidResponse, "chat: the proxy returned no speakable text.");

                    Debug.Log($"Hypa Stories: {reply.route?.model} answered the question.");
                    for (var i = 0; i < sentences.Count; i++) onSentence(sentences[i], i, i == sentences.Count - 1);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No answer within {DeadlineMs / 1000} s.");
                }
            }
        }

        /// <summary>The chat messages for a question. Pure.</summary>
        public static JArray Messages(QaRequest req, string title)
        {
            var lang = req.Language?.HypaLabel ?? "English";
            var name = string.IsNullOrEmpty(title) ? "this story" : $"“{title.Replace('\n', ' ')}”";
            var system =
                $"You are the storyteller of {name}, telling it aloud to a listener in virtual reality. " +
                "The listener has paused the story to ask you a question.\n" +
                $"Answer in {lang}, in 2 or 3 short sentences that sound natural spoken aloud. " +
                "No lists, headings, markdown, emoji or stage directions.\n" +
                "Use only what the story has told so far. Never reveal or hint at what happens next: if the answer comes later in " +
                "the story, warmly tell them they will find out soon.\n" +
                "If the question is not about the story, answer briefly and kindly, then lead back to the story. " +
                "Stay in your storyteller's voice, suitable for all ages.";
            var user = $"The story so far:\n{req.StorySoFar}\n\nThe listener asks: {req.Question}";
            return new JArray
            {
                new JObject { ["role"] = "system", ["content"] = system },
                new JObject { ["role"] = "user", ["content"] = user },
            };
        }

        // a sentence ends at . ! ? … (and any closing quote) before a capital or an opening mark: "“Why?” he asks." stays whole
        static readonly Regex SentenceEnd = new Regex(@"(?<=[.!?…。！？][""”»’)]*)\s+(?=[\p{Lu}¿¡“""«‘(\d])", RegexOptions.CultureInvariant);
        // a reasoning model's thinking, if it ever comes back with the reply: never spoken
        static readonly Regex Thinking = new Regex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        static readonly Regex Markup = new Regex(@"[*_#`]+|^\s*[-•]\s*", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>A reply as spoken sentences: markdown stripped, at most <see cref="MaxSentences"/>. Pure.</summary>
        public static List<string> Sentences(string reply)
        {
            var text = Markup.Replace(Thinking.Replace(reply ?? "", ""), "").Trim();
            return SentenceEnd.Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).Take(MaxSentences).ToList();
        }
    }
}
