using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    // Progress sync across devices (G08, HYP-203): saves where the player is, offers to continue where another device left off,
    // and records finished stories. ProgressSync and UserDataSync do the bookkeeping; this connects them to the campfire.
    public sealed partial class StoryApp
    {
        const string InstallIdKey = "hypa.device.id";

        ProgressSync _sync;
        ContinueView _continue;
        ContinueOffer _offer;                  // shown, or waiting for a moment it can be shown
        volatile ContinueOffer _incoming;      // from the store, maybe on another thread: picked up in Update
        volatile bool _syncStale;              // the services were upgraded: start again on the new store
        float _syncClock;
        string _recordedStory;
        int _recordedLine = -1;
        float _resumeAt;                       // where the next story starts (a continued story), else 0

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>This device in saved progress: its kind and an id made once per install, so two browsers are two devices.</summary>
        static string DeviceId()
        {
            var id = PlayerPrefs.GetString(InstallIdKey, "");
            if (id.Length == 0)
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 8);
                PlayerPrefs.SetString(InstallIdKey, id);
                PlayerPrefs.Save();
            }
            return ProgressSync.DeviceId(DeviceKind, id);
        }

        static string DeviceKind
        {
            get
            {
#if UNITY_WEBGL
                return Application.isMobilePlatform ? "phone" : "web";
#elif UNITY_IOS
                return "ios";
#elif UNITY_ANDROID && HYPA_FLAT
                return "android";
#elif UNITY_ANDROID
                return "quest";
#else
                return "desktop";
#endif
            }
        }

        void StartSync()
        {
            _continue = new ContinueView(_ui, ContinueStory, () => { _offer = null; _continue.Hide(); });
            _story.UserPaused += SavePositionNow; // the player paused, left the camp, or the app went to the background
            HybridServices.Upgraded += MarkSyncStale;
            ConnectSync();
        }

        void StopSync()
        {
            HybridServices.Upgraded -= MarkSyncStale;
            if (_sync == null) return;
            _sync.Detach();
            _ = _sync.FlushAsync();
        }

        void MarkSyncStale() => _syncStale = true;

        void ConnectSync()
        {
            _sync?.Detach();
            var sync = new ProgressSync(HybridServices.Current.Progress, DeviceId(), NowMs);
            sync.ContinueOffered += offer => _incoming = offer;
            sync.Attach();
            _sync = sync;
            _ = CheckForOfferAsync(sync);
        }

        async Task CheckForOfferAsync(ProgressSync sync)
        {
            try
            {
                var offer = await sync.CheckAsync();
                if (offer != null && sync == _sync) _incoming = offer;
            }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't read the saved progress (" + e.GetType().Name + ")."); }
        }

        /// <summary>Every frame, also while away at another place: records the line, saves on the 5 s rhythm, shows a waiting offer.</summary>
        void UpdateSync()
        {
            if (_sync == null) return;
            if (_syncStale) { _syncStale = false; ConnectSync(); }
            var incoming = _incoming;
            if (incoming != null) { _incoming = null; _offer = incoming; }
            if (_offer != null && !_continue.Shown && CanOffer()) ShowOffer();

            if (_screen == Screen.Play && _story.Live && _story.Story != null && !CircleGuest && _story.CaptionIndex >= 0
                && (_story.StoryId != _recordedStory || _story.CaptionIndex != _recordedLine))
            {
                _recordedStory = _story.StoryId;
                _recordedLine = _story.CaptionIndex;
                _sync.Record(Position()); // a line started
            }
            _syncClock += Time.unscaledDeltaTime;
            if (_syncClock >= 1f) { _syncClock = 0f; _ = _sync.TickAsync(); }
        }

        /// <summary>Where the player is in the story now.</summary>
        Progress Position()
        {
            var caps = _story.Story.Timeline.Captions;
            var i = Mathf.Clamp(_story.CaptionIndex, 0, caps.Length - 1);
            return new Progress
            {
                storyId = _story.StoryId, lineId = caps[i].Id, lineIndex = i, language = _story.Language.Code,
                place = _elderSession ? "village" : PlaceIds.Camp,
            };
        }

        void SavePositionNow()
        {
            if (_sync == null || _story.Story == null || CircleGuest) return;
            _sync.Record(Position());
            _ = _sync.FlushAsync();
        }

        void OnApplicationQuit() => _ = _sync?.FlushAsync();

        // ---------- the offer ----------

        /// <summary>At the campfire with nothing else open: not mid-story, in a circle, in Try Hypa or away at another place.</summary>
        bool CanOffer() => !_busy && !_away && !_creatingProfile && _screen != Screen.Play && !_elderSession && !InCircle && !_circleView.Shown;

        /// <summary>A story you can continue here: one of the campfire's. An elder's story belongs to the village.</summary>
        static StoryInfo CampfireStory(string id) => StoryLibrary.Campfire.FirstOrDefault(s => s != null && s.Id == id);

        void ShowOffer()
        {
            var story = CampfireStory(_offer.Saved.storyId);
            if (story == null) { _offer = null; return; }
            _continue.Show(_offer.Text("<i>" + story.Title + "</i>"));
        }

        void ContinueStory()
        {
            var saved = _offer?.Saved;
            _offer = null;
            _continue.Hide();
            var story = saved == null ? null : CampfireStory(saved.storyId);
            if (story == null) return;
            var lang = StoryLanguages.Find(saved.language);
            if (lang != null) PickLanguage(lang);
            StartCoroutine(ContinueSequence(story, saved));
        }

        /// <summary>To the story list (if not there), then the story, starting at the saved line.</summary>
        IEnumerator ContinueSequence(StoryInfo story, Progress saved)
        {
            if (_screen == Screen.Splash) OpenStories(false);
            var t0 = Time.time;
            while ((_busy || _screen != Screen.List) && Time.time - t0 < 10f) yield return null;
            if (_screen != Screen.List || _elderSession) yield break;
            for (var i = 0; i < _list.Count; i++)
            {
                if (_list.StoryAt(i).Id != story.Id) continue;
                _resumeAt = LineStart(story, saved);
                Play(i);
                yield break;
            }
        }

        /// <summary>When the saved line starts: found by its id, else by its number (the story's text may have changed since).</summary>
        static float LineStart(StoryInfo story, Progress saved)
        {
            var caps = story.Timeline.Captions;
            if (caps.Length == 0) return 0f;
            var i = Array.FindIndex(caps, c => c.Id == saved.lineId);
            if (i < 0) i = Mathf.Clamp(saved.lineIndex, 0, caps.Length - 1);
            return caps[i].At;
        }

        /// <summary>Where the story about to start begins: the continued line once, then the beginning.</summary>
        float TakeResumeAt()
        {
            var at = _resumeAt;
            _resumeAt = 0f;
            return at;
        }

        // ---------- per-story results ----------

        /// <summary>The story reached its end: saved as finished (merged with what other devices saved).</summary>
        async Task RecordFinishedAsync(string storyId, string lastLineId)
        {
            try { await new UserDataSync(HybridServices.Current.UserData, NowMs).RecordStoryAsync(storyId, true, 0, lastLineId); }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't save the finished story (" + e.GetType().Name + ")."); }
        }

        /// <summary>The words store of the services in use now: the real one once Firebase is up, the session's own before.</summary>
        sealed class CurrentUserData : IUserDataStore
        {
            static IUserDataStore Store => HybridServices.Current.UserData;
            public Task<StoryResult> GetStoryAsync(string storyId) => Store.GetStoryAsync(storyId);
            public Task SaveStoryAsync(StoryResult result) => Store.SaveStoryAsync(result);
            public Task<IReadOnlyList<SavedWord>> ListWordsAsync() => Store.ListWordsAsync();
            public Task SaveWordAsync(SavedWord word) => Store.SaveWordAsync(word);
        }
    }
}
