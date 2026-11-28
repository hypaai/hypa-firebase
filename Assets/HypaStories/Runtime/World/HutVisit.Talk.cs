using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// Talking with the old woman (G10): "Talk with her" in the hut (or, in the headset, looking at her for a second) opens a voice
    /// conversation that stays open until End or you leave the hut. The language picks the engine (<see cref="TalkRouter"/>):
    /// Gemini Live for English, French, Spanish and Kiswahili, Hypa turn by turn for Yorùbá, Hausa, Igbo and Pidgin (and for
    /// any language when Live is not set up). She can light up a thing's card and point to it, tell its story, teach and save a
    /// word, and offer to walk you to the fire for a story (<see cref="ITalkHost"/>). Offline she says, from a clip cached while
    /// online, that she needs the connection; the items' stories still play.
    /// </summary>
    public sealed partial class HutVisit : ITalkHost
    {
        const float GazeSeconds = 1f, GazeDegrees = 12f, GazeRearmDegrees = 25f, ShowSeconds = 5f, InsideMargin = 0.4f;
        const string WordSource = "hut/elder";

        ElderPersona _persona;
        StoryVoice _talkVoice;
        LivePlayback _livePlayback;
        TalkView _talkView;
        IVoiceConversation _talk;
        KitButton _talkButton;
        CancellationTokenSource _talkCts;
        UserDataSync _words;
        Action<string> _goToStory;
        int _shownItem = -1;
        float _shownUntil, _gaze;
        bool _gazeArmed = true, _heldForStory;
        string _prefetched;

        /// <summary>Every reply's timing this visit (see <see cref="TalkTurnLog"/>).</summary>
        public readonly List<TalkTurnLog> TalkLog = new List<TalkTurnLog>();

        /// <summary>True from "Talk with her" until the conversation has ended.</summary>
        public bool Talking => _talk != null;
        float TalkLevel => Mathf.Max(_talkVoice != null ? _talkVoice.Level : 0f, _talk != null ? _talk.VoiceLevel : 0f);

        void BuildTalk(Transform mouth)
        {
            try { _persona = ElderPersona.Load(); }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: she can't talk yet (" + e.Message + ")."); return; }
            // her own voice for the conversation, so an item's story (which starts its voice afresh) never drops its cached lines
            _talkVoice = gameObject.AddComponent<StoryVoice>();
            _talkVoice.Init(mouth);
            // a woman's voice: Hypa's Auto picks a man's for English (OpenAI alloy). Her remarks too (an item's story sets its
            // voices from hers, see Play). Languages she has no voice named for are Auto's pick
            _talkVoice.Voices = _persona.VoicesFor;
            _voice.Voices = _persona.VoicesFor;
#if !UNITY_WEBGL || UNITY_EDITOR
            _livePlayback = LivePlayback.On(mouth); // in the browser the page plays the Live voice
#endif
            _talkView = new TalkView(_ui, EndTalk, () => _talk?.EndTurn());
            _talkView.Root.gameObject.SetActive(false);
        }

        /// <summary>The ids and words she knows: her things (their own story.json) and the stories told at the fire and in the village.</summary>
        TalkContext Context(StoryLanguage lang)
        {
            IReadOnlyList<TalkTopic> Stories(IEnumerable<string> ids) =>
                ids.Select(StoryLibrary.Find).Where(s => s != null).Select(TalkTopic.Story).ToList();
            var flags = HybridServices.Current.Flags;
            float.TryParse(flags.Get(RemoteConfigKeys.TalkMaxSeconds, RemoteConfigKeys.Default(RemoteConfigKeys.TalkMaxSeconds)),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var max);
            return new TalkContext
            {
                Persona = _persona, Language = lang, Host = this, MaxSeconds = max,
                Items = _items.Select(it => TalkTopic.Item(it.Story)).ToList(),
                Fire = Stories(_persona.StoriesByTheFire),
                Village = Stories(_persona.StoriesInTheVillage),
            };
        }

        /// <summary>"Talk with her": close what she was telling, pick the engine, connect, and she greets you.</summary>
        void StartTalk()
        {
            if (Talking || _persona == null) return;
            Close();
            CancelRemarks();
            _pauseCamp?.Invoke();
            MicListener.RequestPermission();
            var lang = _lang();
            var flags = HybridServices.Current.Flags;
            var liveLangs = RouteContext.Set(flags.Get(RemoteConfigKeys.LiveLangs, RemoteConfigKeys.Default(RemoteConfigKeys.LiveLangs)));
            var liveReady = !(HybridServices.Current.Live is NoLiveModel);
            var route = TalkRouter.Decide(lang, liveLangs, liveReady, NetworkStatus.Online, HypaConfig.Current.HasProxy);
            Debug.Log($"Hypa Stories: talking with her in {lang.Code}: {route.Chip()} ({route.Reason})");
            if (route.Route == Route.Offline) { SayAside(_persona.Line("offline", lang.Code), lang); return; }

            _talkView.Reset();
            _talkView.SetChip(route.Chip());
            _talkView.Root.gameObject.SetActive(true);
            _talkView.Follow.Snap();
            _talkView.Fade.Set(1f);
            _ = Converse(Context(lang), route);
        }

        async Task Converse(TalkContext ctx, RouteDecision route)
        {
            var cts = _talkCts = new CancellationTokenSource();
            var flags = HybridServices.Current.Flags;
            IVoiceConversation talk = null;
            if (route.Route == Route.CloudGemini)
            {
                talk = new LiveTalk(ctx, HybridServices.Current.Live,
                    flags.Get(RemoteConfigKeys.LiveModel, RemoteConfigKeys.Default(RemoteConfigKeys.LiveModel)),
                    flags.Get(RemoteConfigKeys.LiveVoice, RemoteConfigKeys.Default(RemoteConfigKeys.LiveVoice)),
                    () => new MicTalkInput(MicStream.Open()), _livePlayback);
                Attach(talk);
                try { await talk.StartAsync(cts.Token); }
                catch (OperationCanceledException) { Detach(talk); return; }
                catch (Exception e)
                {
                    Debug.LogWarning("Hypa Stories: Live could not start (" + e.Message + ").");
                    Detach(talk);
                    talk = null;
                    if (cts.IsCancellationRequested) return;
                    if (!HypaConfig.Current.HasProxy || !NetworkStatus.Online)
                    {
                        HideTalk();
                        SayAside(_persona.Line("offline", ctx.Language.Code), ctx.Language);
                        return;
                    }
                }
            }
            if (talk == null)
            {
                try { talk = HypaTalk.OnProxy(ctx, _talkVoice, () => _talkVoice.Level); }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: she can't talk: " + e.Message); HideTalk(); return; }
                Attach(talk);
                _talkView.SetChip(talk.Route.Chip());
                try { await talk.StartAsync(cts.Token); }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: Hypa conversation could not start: " + e.Message); Detach(talk); HideTalk(); return; }
            }
            if (cts.IsCancellationRequested) { Detach(talk); return; }
            _talkView.SetMicOff(talk.MicOff);
        }

        void Attach(IVoiceConversation talk)
        {
            _talk = talk;
            _talkView.CanEndTurn = true; // known once connected (OnTalkState)
            talk.StateChanged += OnTalkState;
            talk.LineChanged += _talkView.ShowLine;
            talk.Notice += _talkView.SetNotice;
            talk.Logged += OnTalkLogged;
        }

        void Detach(IVoiceConversation talk)
        {
            if (talk == null) return;
            talk.StateChanged -= OnTalkState;
            talk.LineChanged -= _talkView.ShowLine;
            talk.Notice -= _talkView.SetNotice;
            talk.Logged -= OnTalkLogged;
            talk.Dispose();
            if (_talk == talk) _talk = null;
        }

        void OnTalkState(TalkState s)
        {
            if (_talk != null) { _talkView.CanEndTurn = _talk.CanEndTurn; _talkView.SetMicOff(_talk.MicOff); }
            _talkView.SetState(s);
            if (s == TalkState.Ended) HideTalk();
        }

        void OnTalkLogged(TalkTurnLog log)
        {
            TalkLog.Add(log);
            Debug.Log("Hypa Stories hut " + log);
        }

        /// <summary>End: she says nothing more, the mic closes, the panel goes.</summary>
        void EndTalk()
        {
            var talk = _talk;
            if (talk == null) return;
            _ = Finish();
            async Task Finish()
            {
                try { await talk.EndAsync(); }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: the conversation didn't end cleanly: " + e.Message); }
                HideTalk();
            }
        }

        /// <summary>At once (you left the hut, or the hut is going): no goodbye.</summary>
        void EndTalkNow()
        {
            if (_talk == null && _talkCts == null) return;
            _talkCts?.Cancel();
            _talkCts = null;
            HideTalk();
        }

        void HideTalk()
        {
            var talk = _talk;
            _talk = null;
            Detach(talk);
            _heldForStory = false;
            _shownItem = -1;
            _gazeArmed = false; // look away before looking at her starts another
            _talkVoice?.StopAll();
            _livePlayback?.Clear();
            if (_talkView != null && _talkView.Root != null) // not when the hut is being destroyed
            {
                _talkView.Offer(null, null);
                _talkView.Root.gameObject.SetActive(false);
            }
        }

        /// <summary>A line of hers outside a conversation (offline): over her head, and in her voice when it was cached.</summary>
        async void SayAside(string text, StoryLanguage lang)
        {
            if (string.IsNullOrEmpty(text)) return;
            ShowCaption(text);
            if (_talkVoice == null) return;
            try
            {
                var line = await _talkVoice.PrepareAsync(text, lang, _cts.Token);
                await _talkVoice.PlayAsync(line, null, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: the old woman couldn't speak: " + e.Message); }
        }

        /// <summary>
        /// Every frame: the conversation's clock and panel, coming back from an item's story she told, the card she is showing,
        /// looking at her to start (headset), the "Talk with her" button only inside, and her offline line kept ready.
        /// </summary>
        void TalkUpdate(Transform cam, float dt, float dist)
        {
            if (_talkView == null) return;
            var inside = dist < HutTrailLayout.HutRadius - InsideMargin;
            if (Talking && dist > HutTrailLayout.HutRadius + InsideMargin) EndTalkNow(); // you left the hut
            if (_talkButton != null && _talkButton.gameObject.activeSelf != (inside && !Talking)) _talkButton.gameObject.SetActive(inside && !Talking);

            if (_talk != null)
            {
                _talk.Tick(dt);
                if (_talk != null)
                {
                    _talkView.SetMicOff(_talk.MicOff); // the microphone can be allowed after the conversation started
                    _talkView.Update(dt, _talk.MicLevel, _talk.VoiceLevel);
                }
                // the item's story she started is over (or was closed): back to the conversation
                if (_heldForStory && (_current < 0 || (_story.Live && _story.Ended)))
                {
                    _heldForStory = false;
                    if (_current >= 0) Close();
                    _talk?.Hold(false);
                    if (_talk != null) { _talkView.Root.gameObject.SetActive(true); _talkView.Follow.Snap(); }
                }
            }
            if (_shownItem >= 0 && Time.time > _shownUntil)
            {
                if (_shownItem < _items.Count) _items[_shownItem].Card.HoverTarget = 0f;
                _shownItem = -1;
            }

            // in the headset, looking at her for a second starts a conversation
            if (_woman != null && UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var angle = Vector3.Angle(cam.forward, _woman.Head.position - cam.position);
                if (angle > GazeRearmDegrees) { _gazeArmed = true; _gaze = 0f; }
                else if (_gazeArmed && inside && !Talking && _current < 0 && angle < GazeDegrees)
                {
                    _gaze += dt;
                    if (_gaze >= GazeSeconds) { _gazeArmed = false; _gaze = 0f; StartTalk(); }
                }
                else _gaze = 0f;
            }

            // what she says offline (and her Hypa greeting) is fetched while there is a connection, so it is there without one
            var lang = _lang();
            if (dist < NearMetres && _persona != null && _talkVoice.TtsAvailable && NetworkStatus.Online && _prefetched != lang.Code)
            {
                _prefetched = lang.Code;
                _talkVoice.Prefetch(_persona.Line("offline", lang.Code), lang);
                _talkVoice.Prefetch(_persona.Line("greet", lang.Code), lang);
            }
        }

        /// <summary>Her hands while she shows a thing: the right arm reaches toward it.</summary>
        void PointAtShown(float dt)
        {
            if (_woman == null || _shownItem < 0 || _shownItem >= _items.Count) return;
            var d = _woman.Root.InverseTransformPoint(_items[_shownItem].Anchor.position);
            var yaw = Mathf.Clamp(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -80f, 80f);
            var k = 1f - Mathf.Exp(-6f * dt);
            _woman.ArmR.localRotation = Quaternion.Slerp(_woman.ArmR.localRotation, Quaternion.Euler(-72f, yaw, 0f), k);
            _woman.ForeR.localRotation = Quaternion.Slerp(_woman.ForeR.localRotation, Quaternion.Euler(-12f, 0f, 0f), k);
        }

        // ---------- what she can do (ITalkHost) ----------

        int ItemIndex(string id)
        {
            var want = TalkActions.ItemId(id);
            return _items.FindIndex(it => it.Story.Id == want);
        }

        string ItemName(int i) => _items[i].Story.Title.Replace('\n', ' ');

        public string ShowItem(string id)
        {
            var i = ItemIndex(id);
            if (i < 0) return $"There is no \"{id}\" in your hut. Your things are: {string.Join(", ", _items.Select(it => TalkTopic.Item(it.Story).Id))}.";
            if (_shownItem >= 0 && _shownItem < _items.Count) _items[_shownItem].Card.HoverTarget = 0f;
            _shownItem = i;
            _shownUntil = Time.time + ShowSeconds;
            _items[i].Card.HoverTarget = 1f;
            _items[i].Card.Press = 1f;
            return $"The card over {ItemName(i)} is lit and you are pointing to it.";
        }

        public string TellItemStory(string id)
        {
            var i = ItemIndex(id);
            if (i < 0) return $"There is no \"{id}\" in your hut.";
            Play(i); // holds the conversation while it plays (see Play)
            return $"The story of {ItemName(i)} is playing now. Say nothing until it ends; then ask the player what they thought.";
        }

        public string TeachWord(string word, string language)
        {
            word = word?.Trim();
            if (string.IsNullOrEmpty(word)) return "teach_word needs a word.";
            var code = StoryLanguages.Find(language)?.Code ?? language ?? _lang().Code;
            ShowCaption(word);
            _talkView?.SetNotice("Saved to your words: " + word);
            if (_words == null) return "The word could not be saved here, but say it slowly, twice, anyway.";
            _ = Save();
            return $"\"{word}\" is saved to the player's words. Now say it slowly, twice, and what it means.";

            async Task Save()
            {
                try { await _words.SaveWordAsync(word, null, code, WordSource); }
                catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't save the word: " + e.Message); }
            }
        }

        public string SuggestStory(string storyId)
        {
            var story = StoryLibrary.Campfire.FirstOrDefault(s => s != null && s.Id == storyId?.Trim());
            if (story == null)
                return $"\"{storyId}\" is not told at the fire. The fire's stories are: {string.Join(", ", _persona.StoriesByTheFire)}.";
            if (_goToStory == null) return "The way to the fire is closed just now.";
            var title = story.Title.Replace('\n', ' ');
            _talkView.Offer(title, () => { EndTalkNow(); _goToStory(story.Id); });
            return $"A button offering to walk to the fire for {title} is showing. Tell the player they can tap it.";
        }
    }
}
