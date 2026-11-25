using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    // PROVISIONAL (HYP-203, G08): G01's contract has only IProgressStore (users/{uid}/progress/current). Syncing per-story results
    // and saved words (docs/contracts.md, section 3) needs these two shapes and one more interface. For Timi to keep or change.

    /// <summary>Firestore <c>users/{uid}/stories/{storyId}</c>.</summary>
    [Serializable]
    public sealed class StoryResult
    {
        public string storyId;
        public bool finished;
        public int bestGameScore;
        public int gamePlays;
        public string lastLineId;
        /// <summary>Unix milliseconds.</summary>
        public long updatedAt;

        /// <summary>
        /// Two devices' results for one story, combined (G08: "stories/* merge"). Finished if either finished; the best score is the
        /// higher; the plays are the larger count (two devices cannot be told apart, so adding would count the same plays twice);
        /// where the player last was comes from whichever was updated later. Either side may be null.
        /// </summary>
        public static StoryResult Merge(StoryResult a, StoryResult b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var newer = b.updatedAt > a.updatedAt ? b : a;
            return new StoryResult
            {
                storyId = a.storyId ?? b.storyId,
                finished = a.finished || b.finished,
                bestGameScore = Math.Max(a.bestGameScore, b.bestGameScore),
                gamePlays = Math.Max(a.gamePlays, b.gamePlays),
                lastLineId = newer.lastLineId,
                updatedAt = Math.Max(a.updatedAt, b.updatedAt),
            };
        }
    }

    /// <summary>Firestore <c>users/{uid}/words/{wordId}</c>: a word saved from Try Hypa.</summary>
    [Serializable]
    public sealed class SavedWord
    {
        public string text;
        public string translation;
        public string language;
        public string storyId;
        /// <summary>Unix milliseconds.</summary>
        public long savedAt;

        /// <summary>The same word in the same language gets the same id on every device, so saving it twice is one word. Safe as a document id.</summary>
        public string Id => IdFor(text, language);

        public static string IdFor(string text, string language)
        {
            var hex = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes((text ?? "").Trim().ToLowerInvariant())) hex.Append(b.ToString("x2"));
            return hex + "-" + (language ?? "").ToLowerInvariant();
        }

        /// <summary>Two devices' saved words combined: every word once, keeping the later save of a word both have.</summary>
        public static List<SavedWord> Merge(IEnumerable<SavedWord> a, IEnumerable<SavedWord> b)
            => (a ?? Enumerable.Empty<SavedWord>()).Concat(b ?? Enumerable.Empty<SavedWord>())
                .GroupBy(w => w.Id)
                .Select(g => g.OrderByDescending(w => w.savedAt).First())
                .OrderBy(w => w.savedAt)
                .ToList();
    }

    /// <summary>Per-story results and saved words (Firestore, with the offline cache on).</summary>
    public interface IUserDataStore
    {
        /// <summary>The saved result for a story, or null if there is none.</summary>
        Task<StoryResult> GetStoryAsync(string storyId);
        Task SaveStoryAsync(StoryResult result);
        Task<IReadOnlyList<SavedWord>> ListWordsAsync();
        Task SaveWordAsync(SavedWord word);
    }

    public sealed class FakeUserDataStore : IUserDataStore
    {
        readonly Dictionary<string, StoryResult> _stories = new Dictionary<string, StoryResult>();
        readonly Dictionary<string, SavedWord> _words = new Dictionary<string, SavedWord>();

        public Task<StoryResult> GetStoryAsync(string storyId) => Task.FromResult(_stories.TryGetValue(storyId, out var r) ? r : null);
        public Task SaveStoryAsync(StoryResult result) { _stories[result.storyId] = result; return Task.CompletedTask; }
        public Task<IReadOnlyList<SavedWord>> ListWordsAsync() => Task.FromResult<IReadOnlyList<SavedWord>>(_words.Values.OrderBy(w => w.savedAt).ToList());
        public Task SaveWordAsync(SavedWord word) { _words[word.Id] = word; return Task.CompletedTask; }
    }
}
