using System;
using System.Threading.Tasks;
using HypaCore.Hybrid;

namespace HypaStories
{
    /// <summary>"Continue <i>Seven Hot Stones</i> where you left off on your phone?" (HYP-203 item 3): the offer, and when to make it.</summary>
    public sealed class ContinueOffer
    {
        /// <summary>Where the player left off: the story, the line, the language and the place to jump to.</summary>
        public Progress Saved { get; }
        public ContinueOffer(Progress saved) { Saved = saved; }

        /// <summary>The question for the prompt. The story title is plain here; the screen can set it in italics.</summary>
        public string Text(string storyTitle) => $"Continue {storyTitle} where you left off on {Where(Saved.device)}?";

        /// <summary>"your phone", "your Quest", "the web", or "another device".</summary>
        public static string Where(string device)
        {
            switch ((device ?? "").ToLowerInvariant())
            {
                case "android": case "ios": case "phone": return "your phone";
                case "quest": return "your Quest";
                case "web": case "webgl": return "the web";
                default: return "another device";
            }
        }
    }

    /// <summary>
    /// Saves where the player is, and notices when they were further along on another device (HYP-203). Pure bookkeeping around
    /// <see cref="IProgressStore"/>: the clock is passed in, and nothing here waits on the network or blocks the story.
    /// Call <see cref="Record"/> when a line starts, when the player pauses and when they leave a place; call <see cref="TickAsync"/>
    /// about once a second; call <see cref="FlushAsync"/> when the app closes.
    /// </summary>
    public sealed class ProgressSync
    {
        /// <summary>At most one save this often (HYP-203 item 1).</summary>
        public const int MinIntervalMs = 5000;

        readonly IProgressStore _store;
        readonly string _device;
        readonly Func<long> _nowMs;
        Progress _pending;
        long? _lastSentAt; // null until the first send, so the first position goes out at once

        /// <summary>What this device last saved, or null. A newer save from another device is compared against its time.</summary>
        public Progress LastSaved { get; private set; }

        /// <summary>Raised when another device is further along: show the offer.</summary>
        public event Action<ContinueOffer> ContinueOffered;

        /// <param name="device">"android", "ios", "quest" or "web".</param>
        public ProgressSync(IProgressStore store, string device, Func<long> nowMs)
        {
            _store = store;
            _device = device;
            _nowMs = nowMs;
        }

        /// <summary>Note where the player is. Only the latest position is kept: it goes out on the next tick that is allowed to save.</summary>
        public void Record(Progress p)
        {
            p.device = _device;
            p.updatedAt = _nowMs();
            _pending = p;
        }

        /// <summary>Saves the latest position if there is one and the last save was at least <see cref="MinIntervalMs"/> ago.</summary>
        public async Task TickAsync()
        {
            if (_pending != null && (_lastSentAt == null || _nowMs() - _lastSentAt.Value >= MinIntervalMs)) await SendAsync();
        }

        /// <summary>Saves the latest position now, whatever the interval. For when the app closes.</summary>
        public async Task FlushAsync()
        {
            if (_pending != null) await SendAsync();
        }

        async Task SendAsync()
        {
            var p = _pending;
            _lastSentAt = _nowMs();
            try
            {
                await _store.SaveAsync(p);
                LastSaved = p;
                if (ReferenceEquals(_pending, p)) _pending = null;
            }
            catch (Exception)
            {
                // The store keeps a local copy and syncs later; if it still fails, keep the position and try again on a later tick.
            }
        }

        /// <summary>On launch: is the saved position newer and from another device? Returns the offer, or null.</summary>
        public async Task<ContinueOffer> CheckAsync()
            => Decide(await _store.LoadAsync(), LastSaved, _device);

        /// <summary>Connect <see cref="IProgressStore.ChangedElsewhere"/> to <see cref="ContinueOffered"/>.</summary>
        public void Attach() => _store.ChangedElsewhere += OnChangedElsewhere;
        public void Detach() => _store.ChangedElsewhere -= OnChangedElsewhere;

        void OnChangedElsewhere(Progress p)
        {
            var offer = Decide(p, LastSaved, _device);
            if (offer != null) ContinueOffered?.Invoke(offer);
        }

        /// <summary>
        /// An offer only when the saved position has a story, was saved by another device, and is newer than this device's own.
        /// (Last write wins on <c>progress/current</c>.) Pure.
        /// </summary>
        public static ContinueOffer Decide(Progress saved, Progress thisDevice, string device)
        {
            if (saved == null || string.IsNullOrEmpty(saved.storyId)) return null;
            if (string.Equals(saved.device, device, StringComparison.OrdinalIgnoreCase)) return null;
            if (thisDevice != null && saved.updatedAt <= thisDevice.updatedAt) return null;
            return new ContinueOffer(saved);
        }
    }

    /// <summary>
    /// Per-story results and saved words (HYP-203 item 2). A result is read, merged with what this device has
    /// (<see cref="StoryResult.Merge"/>: best score is the higher, finished stays finished) and written back; a word is saved under an id
    /// built from its text and language, so the same word saved on two devices is one word.
    /// </summary>
    public sealed class UserDataSync
    {
        readonly IUserDataStore _store;
        readonly Func<long> _nowMs;

        public UserDataSync(IUserDataStore store, Func<long> nowMs) { _store = store; _nowMs = nowMs; }

        /// <summary>A story or game ended. Returns what is now saved.</summary>
        public async Task<StoryResult> RecordStoryAsync(string storyId, bool finished, int gameScore, string lastLineId, bool playedGame = false)
        {
            var existing = await _store.GetStoryAsync(storyId);
            var incoming = new StoryResult
            {
                storyId = storyId,
                finished = finished,
                bestGameScore = gameScore,
                gamePlays = (existing?.gamePlays ?? 0) + (playedGame ? 1 : 0),
                lastLineId = lastLineId,
                updatedAt = _nowMs(),
            };
            var merged = StoryResult.Merge(existing, incoming);
            await _store.SaveStoryAsync(merged);
            return merged;
        }

        public Task SaveWordAsync(string text, string translation, string language, string storyId)
            => _store.SaveWordAsync(new SavedWord { text = text, translation = translation, language = language, storyId = storyId, savedAt = _nowMs() });

        public Task<System.Collections.Generic.IReadOnlyList<SavedWord>> WordsAsync() => _store.ListWordsAsync();
    }

    /// <summary>
    /// The 6-letter pairing code for signing a Quest in from another device (HYP-203 item 4). Only the code: the short-lived Firestore
    /// document and the <c>/pair/claim</c> function that issues the custom token belong to G04 and do not exist yet.
    /// Consonants only (no vowels, no Y), so a code can never spell a word.
    /// </summary>
    public static class PairingCode
    {
        public const int Length = 6;
        const string Letters = "BCDFGHJKLMNPQRSTVWXZ";

        /// <summary>A new code. <paramref name="next"/> returns a number from 0 up to (but not including) its argument; pass a cryptographic source.</summary>
        public static string Generate(Func<int, int> next)
        {
            var chars = new char[Length];
            for (var i = 0; i < Length; i++) chars[i] = Letters[next(Letters.Length)];
            return new string(chars);
        }

        /// <summary>What a person typed, tidied: upper case, without spaces or hyphens.</summary>
        public static string Normalize(string typed)
            => string.IsNullOrEmpty(typed) ? "" : typed.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");

        public static bool IsValid(string code)
        {
            code = Normalize(code);
            if (code.Length != Length) return false;
            foreach (var c in code) if (Letters.IndexOf(c) < 0) return false;
            return true;
        }
    }
}
