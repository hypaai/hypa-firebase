using System;
using System.Collections.Generic;
using HypaCore;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// Hands-first spirit boxing after Joromi. One Begin, then the fight runs on: spirits follow you as you move,
    /// the next one floats in after each clear or loss, and a lost hand only stops that hand. No paid requests during combat.
    /// The bout is the spirit arena from the telling: the camp, the village, and the elder are hidden until you leave.
    /// When the last spirit falls, the villagers and the masquerade come into that arena and dance round its circle, to the witch song.
    /// </summary>
    public sealed class SpiritBoxingGame : MonoBehaviour
    {
        public const string StoryId = "joromi";
        public bool Active { get; private set; }
        public SpiritBoxingRules Rules { get; private set; }
        Transform _arena, _ring, _spirit, _hud, _handLeft, _handRight;
        readonly List<Transform> _heads = new List<Transform>();
        readonly List<GameObject> _campHidden = new List<GameObject>();
        readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        readonly Vector3?[] _previous = new Vector3?[2];
        Vector3? _previousHead;
        readonly bool[] _armed = { true, true };
        Material _material, _bright, _night, _glow, _haze, _veil;
        Transform _halo, _hurtVeil;
        readonly Transform[] _palms = new Transform[2];
        Material _palm;
        readonly List<float> _knock = new List<float>();
        /// <summary>Seconds left of a downed head shrinking away.</summary>
        readonly List<float> _vanish = new List<float>();
        const float VanishSeconds = 0.35f;
        /// <summary>The head that counts this turn (picked by the rules), or -1 when there is only one to hit.</summary>
        int _mark => Rules.Heads > 1 ? Rules.Mark : -1;
        /// <summary>The head punched last: it stays up while a cleared spirit fades.</summary>
        int _lastHit;
        Transform _markGlow;
        /// <summary>A wrong head: one fist flies in for this many seconds.</summary>
        float _reprisal, _reprisalSide;
        float _radius;
        Func<bool> _inRoom, _switching;
        Action _toggleRoom;
        KitButton _room;
        /// <summary>The forest clearing: sky, ground and palms. Put away in your room, where the spirits fight in the real room.</summary>
        readonly List<GameObject> _forest = new List<GameObject>();
        bool _roomLook;
        Light _lamp;
        TextMeshPro _cue;
        float _lean;
        bool _lightSaved, _fogWas;
        Color _fogColor, _ambientSky, _ambientEquator, _ambientGround;
        float _fogStart, _fogEnd, _flinch, _hurt;
        int _seenHits, _seenGuard;
        UnityEngine.Rendering.AmbientMode _ambientMode;
        TextMeshPro _title, _instruction, _score;
        KitButton _action, _back;
        StoryInfo _story;
        string _language;
        CampWorld _camp;
        StoryAudio _audio;
        Action _return;
        AudioSource _song;
        bool _songOn, _celebrating;
        double _songAt;
        const float SongDelay = 2f, SongFadeIn = 1.5f, SongFadeOut = 14f, SongLevel = 0.9f;
        bool _focused = true, _noHands;
        float _stanceY;
        static readonly string[] Names = { "mist", "ember", "wind", "seven" };
        static readonly Color[] Colors = { new Color(0.38f, 0.8f, 0.85f), new Color(0.95f, 0.53f, 0.3f), new Color(0.62f, 0.55f, 0.95f), new Color(0.86f, 0.78f, 0.43f) };

        string T(string key) => _story.GameText(key, _language);

        /// <param name="inRoom">True while the camp is in your room: the spirits fight in the real room, with no forest round them.</param>
        /// <param name="toggleRoom">In my room / Back to the forest, as the other games after a story. Null: no button.</param>
        /// <param name="switching">True while the room is being switched: the fight holds still behind the veil.</param>
        public void Init(CampWorld camp, StoryAssets assets, StoryAudio audio, Transform ui, Action returnToStory, Func<bool> inRoom,
            Action toggleRoom = null, Func<bool> switching = null)
        {
            _camp = camp; _audio = audio; _return = returnToStory; _inRoom = inRoom; _toggleRoom = toggleRoom; _switching = switching;
            _material = Own(new Material(assets.pine) { name = "Joromi spirit colour" });
            _material.SetFloat("_Sway", 0f);
            _bright = Own(new Material(assets.lit) { color = new Color(0.95f, 0.9f, 0.7f) });
            _night = Own(new Material(assets.sky) { name = "Joromi spirit sky" });
            _night.SetFloat("_Dark", 1f);
            _night.SetColor("_Tint", new Color(0.05f, 0.02f, 0.08f, 0f));
            _glow = Own(new Material(assets.additive) { name = "Joromi spirit glow" });
            _haze = Own(new Material(assets.additive) { name = "Joromi spirit haze" });
            _palm = Own(new Material(assets.additive) { name = "Joromi your hands" });
            _veil = Own(new Material(assets.additive) { name = "Joromi hurt flash", color = new Color(0.9f, 0.08f, 0.05f) });
            _arena = new GameObject("Joromi Spirit Arena").transform;
            _arena.SetParent(camp.transform, false);
            _song = _arena.gameObject.AddComponent<AudioSource>();
            _song.playOnAwake = false; _song.loop = false; _song.spatialBlend = 0f; _song.volume = 0f;
            _song.clip = assets.witchSong;
            BuildWorld();
            _ring = new GameObject("Spirit ring").transform;
            _ring.SetParent(_arena, false);
            _hud = new GameObject("Spirit Boxing HUD").transform;
            _hud.SetParent(ui, false);
            var follow = _hud.gameObject.AddComponent<FollowView>();
            // up and off to the left, out of the way of your punches
            follow.Distance = 1.05f; follow.Height = 0.45f; follow.YawOffset = -28f; follow.Threshold = 35f;
            var glass = Kit.Glass(_hud, "Spirit practice", new Vector3(0f, 0f, 0.006f), new Vector2(0.78f, 0.34f), 0.035f);
            // The hand's ray shows over the panel only when it has something to press (Begin, Play again): a raised fist mid-fight draws none.
            glass.GetComponent<PointerZone>().Shown = () => glass.Seen() && (Rules == null || Rules.Phase == SpiritFightPhase.Ready || Rules.Phase == SpiritFightPhase.Won);
            _title = Kit.Text(_hud, "", 0.029f, new Vector3(0f, 0.125f, 0f), new Vector2(0.73f, 0.045f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.Serif);
            _instruction = Kit.Text(_hud, "", 0.024f, new Vector3(0f, 0.032f, 0f), new Vector2(0.72f, 0.13f), KitTheme.Paper, TextAlignmentOptions.Center);
            _score = Kit.Text(_hud, "", 0.019f, new Vector3(0f, -0.052f, 0f), new Vector2(0.72f, 0.035f), KitTheme.Dim, TextAlignmentOptions.Center);
            _action = Kit.Button(_hud, new Vector3(0.14f, -0.105f, 0f), new Vector2(0.29f, 0.05f), "Begin", null, KitStyle.Primary, Action, null, 0.016f);
            _back = Kit.Button(_hud, new Vector3(-0.17f, -0.105f, 0f), new Vector2(0.26f, 0.05f), "Back to the story", null, KitStyle.Ghost, () => { End(); _return(); }, null, 0.015f);
            if (_toggleRoom != null)
            {
                _room = Kit.Button(_hud, new Vector3(0f, -0.205f, 0f), new Vector2(0.28f, 0.04f), "In my room", "house", KitStyle.Ghost, _toggleRoom, null, 0.014f);
                SetInRoom(false);
            }
            _arena.gameObject.SetActive(false); _hud.gameObject.SetActive(false);
        }

        U Own<U>(U item) where U : UnityEngine.Object { _owned.Add(item); return item; }
        static void Release(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
        }

        public void Begin(StoryInfo story, StoryLanguage language)
        {
            _story = story; _language = language.Code;
            Rules = new SpiritBoxingRules();
            Active = true; _noHands = false;
            _arena.gameObject.SetActive(true); _hud.gameObject.SetActive(true);
            KeepVillageHidden(); TakeLight(); Look();
            PlaceArena(); BuildSpirit(); ResetHands();
            _hud.GetComponent<FollowView>().Snap();
            _back.SetLabel(T("back"));
            DrawHud();
        }

        public void End()
        {
            if (!Active) return;
            Active = false;
            EndVictory();
            ShowVillage(); RestoreLight();
            _arena.gameObject.SetActive(false); _hud.gameObject.SetActive(false);
            ResetHands();
        }

        bool InRoom => _inRoom != null && _inRoom();

        /// <summary>The room button's label, as on the other games' panels.</summary>
        public void SetInRoom(bool inRoom)
        {
            if (_room == null) return;
            _room.SetLabel(inRoom ? "Back to the forest" : "In my room");
            _room.Style = inRoom ? KitStyle.Primary : KitStyle.Ghost;
            _room.Selected = inRoom;
        }

        /// <summary>
        /// Before the camp switches between the forest and your room: give back what the fight hid and the light it changed, so
        /// the camp saves and restores its own state. The fight takes them again on the next frame, for the new world.
        /// </summary>
        public void LetGoOfWorld()
        {
            if (!Active) return;
            ShowVillage(); RestoreLight();
        }

        /// <summary>The forest round the fight, or none in your room (the depth sensor then lets your real furniture hide the spirits).</summary>
        void Look()
        {
            _roomLook = InRoom;
            foreach (var go in _forest) if (go != null) go.SetActive(!_roomLook);
            SetInRoom(_roomLook);
        }

        /// <summary>The camp stays built. Its scenery goes away, so the elder and the village are not standing behind the fight. The view itself stays.</summary>
        static bool IsView(Transform child, Transform arena) => child == arena || child.GetComponentInChildren<Camera>(true) != null;

        void KeepVillageHidden()
        {
            if (_camp == null) return;
            // anything the camp turned back on (a room switch) is put away again, and remembered so the end of the fight restores it
            foreach (Transform child in _camp.transform)
                if (!IsView(child, _arena) && child.gameObject.activeSelf) { _campHidden.Add(child.gameObject); child.gameObject.SetActive(false); }
        }

        void ShowVillage()
        {
            foreach (var go in _campHidden) if (go != null) go.SetActive(true);
            _campHidden.Clear();
        }

        void TakeLight()
        {
            if (_lightSaved) return;
            _lightSaved = true;
            _fogWas = RenderSettings.fog;
            _fogColor = RenderSettings.fogColor;
            _fogStart = RenderSettings.fogStartDistance;
            _fogEnd = RenderSettings.fogEndDistance;
            _ambientMode = RenderSettings.ambientMode;
            _ambientSky = RenderSettings.ambientSkyColor;
            _ambientEquator = RenderSettings.ambientEquatorColor;
            _ambientGround = RenderSettings.ambientGroundColor;
        }

        void SpiritLight()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.015f, 0.018f, 0.035f);
            RenderSettings.fogStartDistance = 2.8f;
            RenderSettings.fogEndDistance = 9f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.09f, 0.07f, 0.14f);
            RenderSettings.ambientEquatorColor = new Color(0.16f, 0.08f, 0.05f);
            RenderSettings.ambientGroundColor = new Color(0.03f, 0.025f, 0.04f);
        }

        void RestoreLight()
        {
            if (!_lightSaved) return;
            _lightSaved = false;
            RenderSettings.fog = _fogWas;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogStartDistance = _fogStart;
            RenderSettings.fogEndDistance = _fogEnd;
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientSkyColor = _ambientSky;
            RenderSettings.ambientEquatorColor = _ambientEquator;
            RenderSettings.ambientGroundColor = _ambientGround;
        }

        void LateUpdate()
        {
            if (!Active) return;
            KeepVillageHidden();
            TakeLight();
            if (_roomLook != InRoom) Look();
            if (!_roomLook) SpiritLight(); // in your room the camp's own room light stays (its fog pushed out of reach)
        }

        void PlaceArena()
        {
            var cam = Camera.main.transform;
            _arena.SetPositionAndRotation(cam.position, Quaternion.Euler(0f, cam.eulerAngles.y, 0f));
            _stanceY = cam.position.y;
            FollowPlayer(cam, true);
        }

        /// <summary>The spirit keeps facing you as you step and turn, a little behind, like an opponent circling.</summary>
        void FollowPlayer(Transform cam, bool snap = false)
        {
            var move = snap ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 2.5f);
            var turn = snap ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 1.2f);
            var at = new Vector3(cam.position.x, _stanceY, cam.position.z);
            _ring.SetPositionAndRotation(Vector3.Lerp(_ring.position, at, move),
                Quaternion.Euler(0f, Mathf.LerpAngle(_ring.eulerAngles.y, cam.eulerAngles.y, turn), 0f));
        }

        void ResetHands() { _previousHead = null; for (var i = 0; i < 2; i++) { _previous[i] = null; _armed[i] = true; } }

        /// <summary>The only presses: Begin once, and Play again after the last spirit.</summary>
        void Action()
        {
            if (Rules.Phase == SpiritFightPhase.Won) { EndVictory(); Rules = new SpiritBoxingRules(); BuildSpirit(); }
            if (Rules.Phase != SpiritFightPhase.Ready) return;
            _noHands = !InputReady(out _, out _);
            if (!_noHands) { PlaceArena(); ResetHands(); Rules.Start(); _audio.Tap(); }
            DrawHud();
        }

        bool InputReady(out Vector3? left, out Vector3? right)
        {
            left = StoryInput.Hand?.Invoke(0); right = StoryInput.Hand?.Invoke(1);
            return Application.isEditor || FlatInput || left.HasValue && right.HasValue;
        }

        /// <summary>Phones, tablets and the web have no tracked hands: the fight is played on the screen (<see cref="FlatDefending"/>, <see cref="FlatHead"/>).</summary>
        static bool FlatInput => !UnityEngine.XR.XRSettings.isDeviceActive;

        /// <summary>
        /// On a screen: hold the left or right half to guard that side, hold the bottom third to duck the sweep. Keys: A/D or ←/→ to
        /// guard, S or ↓ to duck.
        /// </summary>
        static bool FlatDefending(SpiritAttack attack)
        {
            var held = Input.GetMouseButton(0);
            var p = Input.mousePosition;
            float x = p.x / Mathf.Max(1, UnityEngine.Screen.width), y = p.y / Mathf.Max(1, UnityEngine.Screen.height);
            switch (attack)
            {
                case SpiritAttack.Sweep: return Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || held && y < 0.33f;
                case SpiritAttack.Left: return Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) || held && y >= 0.33f && x < 0.5f;
                default: return Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) || held && y >= 0.33f && x >= 0.5f;
            }
        }

        /// <summary>The standing head under a tap or click (the nearest within 12% of the screen's height), or -1.</summary>
        int FlatHead(Vector2 point, Camera cam)
        {
            int best = -1; var bestD = Mathf.Pow(0.12f * UnityEngine.Screen.height, 2f);
            for (var h = 0; h < _heads.Count; h++)
            {
                if (!_heads[h].gameObject.activeSelf) continue;
                var sp = cam.WorldToScreenPoint(_heads[h].position);
                if (sp.z <= 0f) continue;
                var d = ((Vector2)sp - point).sqrMagnitude;
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        void Update()
        {
            if (!Active || Camera.main == null) return;
            InputReady(out var left, out var right);
            if (!_focused || _switching != null && _switching()) return;
            var cam = Camera.main.transform;
            FollowPlayer(cam);
            var inverse = Quaternion.Inverse(_ring.rotation);
            Vector3 Local(Vector3? hand) => hand.HasValue ? inverse * (hand.Value - cam.position) : Vector3.zero;
            var a = Local(left); var b = Local(right);
            var fist = Rules.Attack == SpiritAttack.Right ? _handRight : _handLeft;
            bool Meets(Vector3? hand) => hand.HasValue && (hand.Value - fist.position).sqrMagnitude < BlockReach * BlockReach;
            var defending = SpiritBoxingRules.Defending(Rules.Attack, a, b, _stanceY - cam.position.y)
                || Rules.Phase == SpiritFightPhase.Strike && (Meets(left) || Meets(right));
            if (Application.isEditor || FlatInput) defending |= FlatDefending(Rules.Attack);
            var lastPhase = Rules.Phase;
            Rules.Tick(Time.deltaTime, defending, false);
            if (lastPhase != Rules.Phase && (lastPhase == SpiritFightPhase.LevelComplete || lastPhase == SpiritFightPhase.Lost)) BuildSpirit();
            if (lastPhase != Rules.Phase && Rules.Phase == SpiritFightPhase.Counter) ResetHands();
            if (lastPhase != Rules.Phase && Rules.Phase == SpiritFightPhase.Strike) _audio.EffectAt("throw", _spirit.position, 0.7f, 1.3f, 2f);
            if (Rules.LastStrikeBlocked == true) _audio.EffectAt("clink", cam.position + cam.forward * 0.3f, 0.9f, 0.8f, 2f);
            if (Rules.LastStrikeBlocked == false) _audio.EffectAt("fall", cam.position, 0.8f, 1.4f, 2f);
            if (lastPhase != Rules.Phase && (Rules.Phase == SpiritFightPhase.LevelComplete || Rules.Phase == SpiritFightPhase.Won))
                _audio.EffectAt("sparks", _spirit.position, 1f, 1f, 2f);
            if (lastPhase != Rules.Phase && Rules.Phase == SpiritFightPhase.Won) BeginVictory();
            if (_celebrating)
            {
                var village = _camp != null ? _camp.Village : null;
                if (village != null) village.StepParty(Time.deltaTime);
                Song();
            }
            if (Rules.Phase == SpiritFightPhase.Counter)
            {
                var head = _ring.InverseTransformPoint(cam.position);
                var headMotion = _previousHead.HasValue ? head - _previousHead.Value : Vector3.zero;
                var hands = new[] { left, right };
                for (var i = 0; i < 2; i++)
                {
                    if (!hands[i].HasValue) { _previous[i] = null; continue; }
                    var position = _ring.InverseTransformPoint(hands[i].Value);
                    // The hand re-arms once it pulls back clear of every face.
                    var reach = _radius + (_heads.Count == 7 ? 0.05f : 0.1f);
                    var clear = true;
                    for (var h = 0; h < _heads.Count; h++)
                    {
                        if (!_heads[h].gameObject.activeSelf) continue;
                        if ((position - _ring.InverseTransformPoint(_heads[h].position)).sqrMagnitude <= (reach + 0.08f) * (reach + 0.08f)) clear = false;
                    }
                    if (clear) _armed[i] = true;
                    if (_armed[i] && _previous[i].HasValue)
                    {
                        var struck = StruckHead(_previous[i].Value, position, Time.deltaTime, reach, headMotion);
                        if (struck >= 0) { _armed[i] = false; Strike(struck); }
                    }
                    _previous[i] = position;
                }
                _previousHead = head;
                if ((Application.isEditor || FlatInput) && (Input.GetKeyDown(KeyCode.J) || Input.GetKeyDown(KeyCode.L) || Input.GetKeyDown(KeyCode.Space)))
                    Strike(_mark >= 0 ? _mark : _heads.FindIndex(x => x.gameObject.activeSelf));
                // a tap on a face punches it: a tap on the wrong head of a many-headed spirit counts against you, as a wrong punch does
                if (FlatInput && Input.GetMouseButtonDown(0)) Strike(FlatHead(Input.mousePosition, Camera.main));
            }
            else ResetHands();
            if (Rules.Hits != _seenHits) { _seenHits = Rules.Hits; _flinch = 0.18f; }
            for (var h = 0; h < _knock.Count; h++) { _knock[h] = Mathf.Max(0f, _knock[h] - Time.deltaTime); _vanish[h] = Mathf.Max(0f, _vanish[h] - Time.deltaTime); }
            if (Rules.Guard < _seenGuard) { _seenGuard = Rules.Guard; _hurt = 0.32f; }
            _reprisal = Mathf.Max(0f, _reprisal - Time.deltaTime);
            _flinch = Mathf.Max(0f, _flinch - Time.deltaTime);
            _hurt = Mathf.Max(0f, _hurt - Time.deltaTime);
            DrawSpirit(); DrawHud();
            DrawPalms(left, right, defending);
        }

        /// <summary>A small glow where the game sees each of your hands: green while it counts as a guard. If it is not on your hand, tracking is off.</summary>
        void DrawPalms(Vector3? left, Vector3? right, bool guarding)
        {
            var hands = new[] { left, right };
            for (var i = 0; i < 2; i++)
            {
                _palms[i].gameObject.SetActive(hands[i].HasValue);
                if (hands[i].HasValue) _palms[i].position = hands[i].Value;
            }
            var warned = Rules.Phase == SpiritFightPhase.Tell || Rules.Phase == SpiritFightPhase.Strike;
            _palm.color = warned && guarding ? new Color(0.35f, 1f, 0.5f) : new Color(1f, 0.95f, 0.85f);
            _palm.SetFloat("_Opacity", 0.55f);
        }

        /// <summary>Your hand blocks a strike when it meets the incoming fist within this many metres, wherever your guard is.</summary>
        const float BlockReach = 0.2f;

        /// <summary>
        /// Which standing head this jab hits. The called head is tested first, at the normal reach, so a punch on the way to it
        /// is not stolen by a neighbour. Another head counts when the fist reaches its face: as deep as the front of that mask
        /// (the palm sits about 10 cm behind the knuckles), over its face, and over it more than over the called head.
        /// </summary>
        int StruckHead(Vector3 previous, Vector3 position, float dt, float reach, Vector3 headMotion)
        {
            float Approach(int h)
            {
                var target = _ring.InverseTransformPoint(_heads[h].position);
                var motion = position - previous;
                var along = motion.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(target - previous, motion) / motion.sqrMagnitude) : 0f;
                return (previous + motion * along - target).magnitude;
            }
            bool Standing(int h) => Rules.Standing(h); // a downed head still shrinking away is not a target
            if (_mark >= 0 && _mark < _heads.Count && Standing(_mark))
            {
                var called = Approach(_mark);
                var closer = float.MaxValue;
                for (var h = 0; h < _heads.Count; h++)
                    if (h != _mark && Standing(h)) closer = Mathf.Min(closer, Approach(h));
                if (called <= reach && called <= closer
                    && SpiritBoxingRules.HitsTarget(previous, position, _ring.InverseTransformPoint(_heads[_mark].position), dt, reach, headMotion))
                    return _mark;
                static float Across(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.y - b.y).magnitude;
                var calledAt = _ring.InverseTransformPoint(_heads[_mark].position);
                var wrong = -1; var best = _radius * 0.9f; // the mask's half width, with its jaw band
                for (var h = 0; h < _heads.Count; h++)
                {
                    if (h == _mark || !Standing(h)) continue;
                    var at = _ring.InverseTransformPoint(_heads[h].position);
                    var across = Across(position, at);
                    if (position.z >= at.z - _radius * 0.7f - 0.1f && across <= best && across < Across(position, calledAt)
                        && SpiritBoxingRules.HitsTarget(previous, position, at, dt, reach, headMotion))
                    { best = across; wrong = h; }
                }
                return wrong;
            }
            int near = -1; var nearest = reach;
            for (var h = 0; h < _heads.Count; h++)
            {
                if (!Standing(h)) continue;
                var gap = Approach(h);
                if (gap <= nearest
                    && SpiritBoxingRules.HitsTarget(previous, position, _ring.InverseTransformPoint(_heads[h].position), dt, reach, headMotion))
                { nearest = gap; near = h; }
            }
            return near;
        }

        /// <summary>
        /// A punch on head <paramref name="h"/>. The marked head counts and snaps back; once it has taken its share it goes down (see the rules).
        /// Any other head does not count, and the spirit strikes back at once.
        /// </summary>
        void Strike(int h)
        {
            if (h < 0) return;
            if (_mark >= 0 && h != _mark)
            {
                if (!Rules.Suffer()) return;
                _reprisal = 0.35f;
                _reprisalSide = _heads[h].localPosition.x < 0f ? -1f : 1f;
                _audio.EffectAt("throw", _spirit.position, 0.7f, 1.3f, 2f);
                _audio.EffectAt("fall", Camera.main != null ? Camera.main.transform.position : _heads[h].position, 0.8f, 1.4f, 2f);
                return;
            }
            if (!Rules.Punch()) return;
            // every landed punch thuds; the punch that downs a head also draws the spirit's cry as the head shrinks away
            _audio.EffectAt("stomp", _heads[h].position, 0.9f, 1.6f, 2f);
            _knock[h] = 0.3f;
            if (!Rules.Standing(h))
            {
                _vanish[h] = VanishSeconds;
                _audio.EffectAt("groan", _heads[h].position, 0.9f, 1.7f, 2f);
                _audio.EffectAt("sizzle", _heads[h].position, 0.8f, 1.3f, 2f);
            }
            _lastHit = h;
        }

        void DrawSpirit()
        {
            var approach = Rules.Phase == SpiritFightPhase.Ready ? 0f
                : Rules.Phase == SpiritFightPhase.Approach ? Mathf.SmoothStep(0f, 1f, Rules.Elapsed / 2f) : 1f;
            var t = Time.time;
            var telling = Rules.Phase == SpiritFightPhase.Tell ? Mathf.Clamp01(Rules.Elapsed / 0.45f) : 0f;
            var strike = Rules.Phase == SpiritFightPhase.Strike ? Mathf.Sin(Mathf.PI * Mathf.Clamp01(Rules.Elapsed / 0.35f)) : 0f;
            // On your turn the heads drop in, so a face sits at chin height within a normal jab.
            _lean = Mathf.MoveTowards(_lean, Rules.Phase == SpiritFightPhase.Counter ? 1f : 0f, Time.deltaTime * 4f);
            var lean = Rules.Attack == SpiritAttack.Right ? -1f : 1f;
            var style = Style(t);
            var fading = Rules.Phase == SpiritFightPhase.LevelComplete || Rules.Phase == SpiritFightPhase.Won ? Mathf.Clamp01(Rules.Elapsed) : 0f;
            _spirit.localPosition = new Vector3(style.x * (1f - _lean), Mathf.Sin(t * 1.6f) * 0.018f - _lean * 0.1f + fading * 0.6f,
                Mathf.Lerp(3.4f, 0.92f, approach) - strike * 0.1f - _lean * 0.3f + _flinch * 0.28f - _hurt * 0.1f);
            _spirit.localScale = Vector3.one * (fading > 0f ? 1f - fading : style.y);
            _spirit.localRotation = Quaternion.Euler(strike * 6f - telling * 4f - _flinch * 10f + _lean * 8f, fading * 540f, lean * (strike * 8f + telling * 4f) + style.z);
            var pulse = 0.55f + 0.25f * Mathf.Sin(t * 3f) + telling * 0.6f + _flinch * 2f;
            _glow.SetFloat("_Opacity", Rules.Level == 1 ? pulse * (0.8f + 0.2f * Mathf.PerlinNoise(t * 9f, 0f)) : pulse);
            _halo.localScale = Vector3.one * (1f + fading * 2.5f + _flinch);
            _haze.SetFloat("_Opacity", 1f + fading * 3f + telling);
            _hurtVeil.gameObject.SetActive(_hurt > 0f);
            if (_hurt > 0f) { _hurtVeil.position = Camera.main.transform.position; _veil.SetFloat("_Opacity", _hurt / 0.32f * 0.35f); }
            var cue = Rules.Phase == SpiritFightPhase.Tell || Rules.Phase == SpiritFightPhase.Strike || Rules.Phase == SpiritFightPhase.Counter;
            _cue.gameObject.SetActive(cue);
            if (cue) _cue.text = T(Rules.Phase == SpiritFightPhase.Counter ? "counter" : Rules.Attack == SpiritAttack.Left ? "left" : Rules.Attack == SpiritAttack.Right ? "right" : "duck");
            PoseHand(_handLeft, -1f, telling, fading);
            PoseHand(_handRight, 1f, telling, fading);
            var telegraph = Rules.Phase == SpiritFightPhase.Tell || Rules.Phase == SpiritFightPhase.Strike;
            var answerLeft = _reprisal > 0f && _reprisalSide < 0f;
            var answerRight = _reprisal > 0f && _reprisalSide > 0f;
            _handLeft.GetComponent<MeshRenderer>().sharedMaterial = answerLeft || telegraph && Rules.Attack != SpiritAttack.Right ? _bright : _material;
            _handRight.GetComponent<MeshRenderer>().sharedMaterial = answerRight || telegraph && Rules.Attack == SpiritAttack.Right ? _bright : _material;
            var marked = Rules.Phase == SpiritFightPhase.Counter && _mark >= 0 && _mark < _heads.Count && _heads[_mark].gameObject.activeSelf;
            for (var i = 0; i < _heads.Count; i++)
            {
                var bob = Mathf.Sin(t * 1.3f + i) * 0.012f;
                var cleared = Rules.Phase == SpiritFightPhase.LevelComplete || Rules.Phase == SpiritFightPhase.Won;
                var going = !Rules.Standing(i) && _vanish[i] > 0f;
                _heads[i].gameObject.SetActive(Rules.Standing(i) || going || cleared && i == _lastHit);
                var called = marked && i == _mark;
                var swell = going ? _vanish[i] / VanishSeconds * 1.2f : called ? 1.08f + 0.06f * Mathf.Sin(t * 6f) : 1f;
                _heads[i].localScale = Vector3.one * swell;
                _heads[i].localPosition = HeadPose(i, _heads.Count) + new Vector3(0f, bob, _knock[i] * 0.5f + (called ? -0.03f : 0f));
                _heads[i].localRotation = Quaternion.Euler(-_knock[i] * 60f, 0f, Mathf.Sin(t * 0.9f + i * 1.7f) * 4f);
            }
            if (_markGlow != null)
            {
                _markGlow.gameObject.SetActive(marked);
                if (marked)
                {
                    _markGlow.SetParent(_heads[_mark], false);
                    // Behind the mask, a little larger than the face, so the outline shows which head is called.
                    _markGlow.localPosition = new Vector3(0f, 0f, _radius * 0.35f);
                    _markGlow.localScale = Vector3.one * (_radius * 1.2f);
                }
            }
        }

        /// <summary>How each spirit moves when idle: x drift, breathing scale, roll. Mist drifts, ember flickers, wind sways, seven breathes.</summary>
        Vector3 Style(float t)
        {
            switch (Rules.Level)
            {
                case 0: return new Vector3(Mathf.Sin(t * 0.7f) * 0.08f, 1f, 0f);
                case 1: return new Vector3((Mathf.PerlinNoise(t * 4f, 1f) - 0.5f) * 0.05f, 1f + (Mathf.PerlinNoise(t * 7f, 2f) - 0.5f) * 0.04f, 0f);
                case 2: return new Vector3(Mathf.Sin(t * 1.3f) * 0.12f, 1f, Mathf.Sin(t * 1.3f + 0.6f) * 6f);
                default: return new Vector3(0f, 1f + Mathf.Sin(t * 1.1f) * 0.025f, 0f);
            }
        }

        /// <summary>
        /// The spirit is only its masks and two hands: no body, no arms. The hands float below the face. The attacking hand coils on the tell, then flies at your
        /// guard on that side. A sweep flies across at head height, which is the duck. On your turn both hands drop open.
        /// Positions are in the ring, where your head is the origin.
        /// </summary>
        void PoseHand(Transform hand, float side, float telling, float fading)
        {
            var phase = Rules.Phase;
            var attacking = Rules.Attack == SpiritAttack.Sweep ? side < 0f : side < 0f == (Rules.Attack != SpiritAttack.Right);
            var answering = _reprisal > 0f && Mathf.Abs(side - _reprisalSide) < 0.1f;
            var body = _spirit.localPosition;
            var guard = body + new Vector3(side * 0.24f, -0.12f + Mathf.Sin(Time.time * 2.1f + side) * 0.025f, -0.1f)
                + new Vector3(side * 0.08f, -0.06f, 0.04f) * _lean;
            var coil = guard + new Vector3(side * 0.08f, 0.08f, 0.14f);
            var p = attacking && phase == SpiritFightPhase.Tell ? Vector3.Lerp(guard, coil, telling) : guard;
            if (answering)
                p = Vector3.Lerp(guard, new Vector3(side * 0.16f, 0.02f, 0.16f), Mathf.Sin(Mathf.PI * (1f - _reprisal / 0.35f)));
            else if (attacking && phase == SpiritFightPhase.Strike)
            {
                var e = Mathf.Clamp01(Rules.Elapsed / 0.35f);
                p = Rules.Attack == SpiritAttack.Sweep
                    ? Vector3.Lerp(new Vector3(-0.6f, 0.04f, 0.22f), new Vector3(0.6f, 0.04f, 0.22f), Mathf.SmoothStep(0f, 1f, e))
                    : Vector3.Lerp(coil, new Vector3(side * 0.2f, -0.02f, 0.2f), Mathf.Sin(Mathf.PI * e));
            }
            p.z += _flinch * 0.2f;
            hand.position = _ring.TransformPoint(Vector3.Lerp(p, body, fading));
            hand.rotation = _ring.rotation * Quaternion.Euler(0f, 0f, side * 12f + Mathf.Sin(Time.time * 1.7f + side) * 8f);
        }

        void DrawHud()
        {
            var phase = Rules.Phase;
            _title.text = string.Format(T("level"), Rules.Level + 1, SpiritBoxingRules.LevelCount) + " · " + T(Names[Rules.Level]);
            _score.text = string.Format(T("health"), Rules.Guard, SpiritBoxingRules.MaxGuard) + "   ·   " + string.Format(T("progress"), Rules.Hits, Rules.NeededHits);
            if (phase == SpiritFightPhase.Ready) _instruction.text = (_noHands ? T("tracking") + "\n" : "") + T(FlatInput ? "flat" : "intro") + "\n" + T("adaptation");
            else _instruction.text = T(phase == SpiritFightPhase.Approach ? "approach" : phase == SpiritFightPhase.Counter ? "counter"
                : phase == SpiritFightPhase.LevelComplete ? "clear" : phase == SpiritFightPhase.Won ? "won"
                : phase == SpiritFightPhase.Lost ? "lost" : phase == SpiritFightPhase.Recover ? "recover"
                : Rules.Attack == SpiritAttack.Left ? "left" : Rules.Attack == SpiritAttack.Right ? "right" : "duck");
            var action = phase == SpiritFightPhase.Ready || phase == SpiritFightPhase.Won;
            // Back and the room button only between bouts: while the spirit attacks or you punch, a raised fist would point the hand's ray at them
            var between = action || phase == SpiritFightPhase.LevelComplete || phase == SpiritFightPhase.Lost;
            _back.gameObject.SetActive(between);
            if (_room != null) _room.gameObject.SetActive(between);
            _action.gameObject.SetActive(action);
            if (action) _action.SetLabel(T(phase == SpiritFightPhase.Ready ? "start" : "replay"));
        }

        static Vector3 HeadPose(int i, int count)
        {
            if (count <= 1) return Vector3.zero;
            var arc = (i - (count - 1) * 0.5f) / ((count - 1) * 0.5f);
            var x = (i - (count - 1) * 0.5f) * (count == 7 ? 0.09f : 0.16f);
            var y = (1f - arc * arc) * (count == 7 ? 0.1f : 0.06f);
            return new Vector3(x, y, -Mathf.Abs(arc) * 0.03f);
        }

        void BuildSpirit()
        {
            if (_spirit != null)
            {
                foreach (var filter in _spirit.GetComponentsInChildren<MeshFilter>()) { _owned.Remove(filter.sharedMesh); Release(filter.sharedMesh); }
                Release(_spirit.gameObject);
            }
            _heads.Clear();
            _lastHit = 0; _reprisal = 0f;
            _seenHits = 0; _seenGuard = Rules.Guard; _flinch = 0f; _hurt = 0f;
            _spirit = new GameObject("Practice Spirit " + (Rules.Level + 1)).transform;
            _spirit.SetParent(_ring, false);
            var color = Colors[Rules.Level];
            var count = Rules.Heads;
            var radius = _radius = count == 7 ? 0.08f : 0.128f;
            _knock.Clear(); _vanish.Clear();
            for (var i = 0; i < count; i++)
            {
                var pose = HeadPose(i, count);
                var head = new TrailBuilder.MeshKit();
                Mask(head, radius, color);
                var node = Mesh("Spirit mask " + (i + 1), head, _spirit, _material);
                node.localPosition = pose;
                var eyes = new TrailBuilder.MeshKit();
                foreach (var side in new[] { -1f, 1f })
                    eyes.Ellipsoid(new Vector3(side * radius * 0.3f, radius * 0.08f, -radius * 0.67f), new Vector3(radius * 0.11f, radius * 0.06f, radius * 0.02f), Quaternion.Euler(0f, 0f, -side * 8f), Color.white, 8, 3);
                Mesh("Spirit eyes", eyes, node, _glow);
                _heads.Add(node); _knock.Add(0f); _vanish.Add(0f);
            }
            var mark = new TrailBuilder.MeshKit();
            mark.Ellipsoid(Vector3.zero, Vector3.one, Quaternion.identity, Color.white, 10, 6);
            _markGlow = Mesh("Called head", mark, _spirit, _glow);
            _markGlow.gameObject.SetActive(false);
            _handLeft = Hand("Left", -1f, color);
            _handRight = Hand("Right", 1f, color);
            _glow.color = color;
            _haze.color = new Color(color.r, color.g, color.b, 0.22f);
            _lamp.color = Color.Lerp(new Color(0.95f, 0.78f, 0.55f), color, 0.5f);
            var halo = new TrailBuilder.MeshKit();
            halo.Ellipsoid(Vector3.zero, new Vector3(count == 7 ? 0.42f : 0.2f, count == 7 ? 0.22f : 0.25f, 0.02f), Quaternion.identity, Color.white * 0.35f, 28, 10);
            _halo = Mesh("Spirit halo", halo, _spirit, _haze);
            _halo.localPosition = new Vector3(0f, count == 7 ? 0.05f : 0.02f, 0.1f);
            _cue = Kit.Text(_spirit, "", 0.03f, new Vector3(0f, -0.3f, -0.05f), new Vector2(0.5f, 0.06f), KitTheme.Paper, TextAlignmentOptions.Center);
            DrawSpirit(); ResetHands();
        }

        /// <summary>
        /// An ivory pendant-style mask in the game's own simple shapes: an oval face under a netted cap, a crown of small
        /// heads, two inlaid bars on the forehead, almond eyes, full lips, and a braided band round the jaw. Each spirit tints
        /// the ivory with its colour. It floats on its own: there is no neck or body.
        /// </summary>
        void Mask(TrailBuilder.MeshKit head, float r, Color color)
        {
            var ivory = Ivory(color);
            var shade = ivory * 0.78f;
            var inlay = new Color(0.1f, 0.07f, 0.05f);
            var flat = Quaternion.identity;
            head.Ellipsoid(Vector3.zero, new Vector3(r * 0.78f, r * 1.1f, r * 0.7f), flat, ivory, 14, 9);
            head.Ellipsoid(new Vector3(0f, r * 0.36f, r * 0.04f), new Vector3(r * 0.82f, r * 0.9f, r * 0.74f), flat, shade, 16, 6, capDegrees: 72f);
            // The netted cap: small beads in rows over it.
            for (var row = 0; row < 3; row++)
                for (var k = 0; k < 9 - row * 2; k++)
                {
                    var a = Mathf.Lerp(-60f + row * 12f, 60f - row * 12f, k / (8f - row * 2f)) * Mathf.Deg2Rad;
                    var y = r * (0.72f + row * 0.18f);
                    var w = r * (0.7f - row * 0.16f);
                    head.Ellipsoid(new Vector3(Mathf.Sin(a) * w, y, -Mathf.Cos(a) * w * 0.9f), Vector3.one * r * 0.045f, flat, ivory, 5, 3);
                }
            for (var i = 0; i < 9; i++)
            {
                var a = Mathf.Lerp(-72f, 72f, i / 8f) * Mathf.Deg2Rad;
                var at = new Vector3(Mathf.Sin(a) * r * 0.8f, r * 0.98f + Mathf.Cos(a) * r * 0.42f, -r * 0.12f);
                head.Ellipsoid(at, new Vector3(r * 0.1f, r * 0.12f, r * 0.09f), flat, ivory, 6, 4);
                head.Ellipsoid(at + new Vector3(0f, r * 0.1f, 0f), new Vector3(r * 0.11f, r * 0.08f, r * 0.1f), flat, shade, 6, 3, capDegrees: 90f);
            }
            foreach (var side in new[] { -1f, 1f })
            {
                head.Box(new Vector3(side * r * 0.1f, r * 0.48f, -r * 0.63f), new Vector3(r * 0.07f, r * 0.3f, r * 0.06f), flat, inlay);
                var eye = new Vector3(side * r * 0.3f, r * 0.08f, -r * 0.62f);
                head.Ellipsoid(eye, new Vector3(r * 0.2f, r * 0.09f, r * 0.06f), Quaternion.Euler(0f, 0f, -side * 8f), inlay, 8, 4);
                head.Ellipsoid(eye + new Vector3(0f, r * 0.07f, -r * 0.01f), new Vector3(r * 0.22f, r * 0.045f, r * 0.05f), Quaternion.Euler(0f, 0f, -side * 8f), ivory, 8, 3);
                head.Ellipsoid(new Vector3(side * r * 0.11f, -r * 0.15f, -r * 0.72f), new Vector3(r * 0.07f, r * 0.06f, r * 0.06f), flat, ivory, 6, 3);
                head.Ellipsoid(new Vector3(side * r * 0.36f, -r * 0.2f, -r * 0.5f), new Vector3(r * 0.17f, r * 0.17f, r * 0.13f), flat, ivory, 10, 6);
                head.Ellipsoid(new Vector3(side * r * 0.78f, r * 0.05f, r * 0.05f), new Vector3(r * 0.1f, r * 0.2f, r * 0.08f), flat, shade, 6, 4);
                for (var b = 0; b < 2; b++)
                    head.Ellipsoid(new Vector3(side * r * 0.8f, r * (0.36f + b * 0.1f), -r * 0.08f), new Vector3(r * 0.12f, r * 0.045f, r * 0.12f), flat, b == 0 ? shade : ivory, 8, 3);
            }
            head.Ellipsoid(new Vector3(0f, r * 0.22f, -r * 0.6f), new Vector3(r * 0.55f, r * 0.06f, r * 0.11f), flat, shade, 8, 3);
            head.Limb(new Vector3(0f, r * 0.18f, -r * 0.66f), new Vector3(0f, -r * 0.12f, -r * 0.8f), r * 0.07f, r * 0.12f, ivory, 6);
            head.Ellipsoid(new Vector3(0f, -r * 0.42f, -r * 0.64f), new Vector3(r * 0.22f, r * 0.06f, r * 0.08f), flat, ivory, 8, 4);
            head.Ellipsoid(new Vector3(0f, -r * 0.54f, -r * 0.62f), new Vector3(r * 0.2f, r * 0.075f, r * 0.09f), flat, ivory, 8, 4);
            head.Ellipsoid(new Vector3(0f, -r * 0.48f, -r * 0.69f), new Vector3(r * 0.18f, r * 0.012f, r * 0.02f), flat, inlay, 6, 3);
            // The braided band: three rows of slanted beads following the jaw, alternating like a plait.
            for (var row = 0; row < 3; row++)
                for (var k = 0; k < 15; k++)
                {
                    var a = Mathf.Lerp(-78f, 78f, k / 14f) * Mathf.Deg2Rad;
                    var at = new Vector3(Mathf.Sin(a) * r * (0.8f + row * 0.1f), -Mathf.Cos(a) * r * (1.05f + row * 0.2f), -Mathf.Cos(a) * r * 0.32f - r * 0.06f);
                    head.Ellipsoid(at, new Vector3(r * 0.075f, r * 0.06f, r * 0.05f), Quaternion.Euler(0f, 0f, (k + row) % 2 == 0 ? 35f : -35f), row == 1 ? ivory : shade, 5, 3);
                }
        }

        static Color Ivory(Color color) => Color.Lerp(new Color(0.88f, 0.8f, 0.62f), color, 0.14f);

        /// <summary>
        /// A carved ivory fist that matches the masks: dark inlay between the knuckles, two inlaid bars on the back like the
        /// forehead's, a glowing stone in the spirit's colour, and a braided bead cuff at the wrist. Nothing joins it to the face.
        /// </summary>
        Transform Hand(string sideName, float side, Color color)
        {
            var ivory = Ivory(color);
            var shade = ivory * 0.78f;
            var inlay = new Color(0.1f, 0.07f, 0.05f);
            var flat = Quaternion.identity;
            var fist = new TrailBuilder.MeshKit();
            fist.Ellipsoid(Vector3.zero, new Vector3(0.05f, 0.045f, 0.058f), flat, ivory, 12, 8);
            fist.Ellipsoid(new Vector3(side * 0.048f, -0.008f, -0.008f), new Vector3(0.018f, 0.03f, 0.022f), Quaternion.Euler(0f, 0f, -side * 35f), shade, 8, 5);
            for (var i = 0; i < 4; i++)
            {
                fist.Ellipsoid(new Vector3((i - 1.5f) * 0.021f, 0.012f, -0.05f), new Vector3(0.012f, 0.019f, 0.017f), flat, ivory, 8, 4);
                if (i < 3) fist.Box(new Vector3((i - 1f) * 0.021f, 0.012f, -0.062f), new Vector3(0.003f, 0.026f, 0.008f), flat, inlay);
            }
            foreach (var bar in new[] { -1f, 1f })
                fist.Box(new Vector3(bar * 0.009f, 0.045f, 0.004f), new Vector3(0.006f, 0.004f, 0.034f), flat, inlay);
            for (var k = 0; k < 12; k++)
            {
                var a = k / 12f * Mathf.PI * 2f;
                fist.Ellipsoid(new Vector3(Mathf.Cos(a) * 0.04f, Mathf.Sin(a) * 0.036f, 0.056f), new Vector3(0.011f, 0.009f, 0.01f), Quaternion.Euler(0f, 0f, k % 2 == 0 ? 35f : -35f), k % 2 == 0 ? shade : ivory, 5, 3);
            }
            var hand = Mesh(sideName + " attack", fist, _spirit, _material);
            var stone = new TrailBuilder.MeshKit();
            stone.Ellipsoid(new Vector3(0f, 0.046f, 0.03f), new Vector3(0.011f, 0.004f, 0.009f), flat, Color.white, 8, 3);
            Mesh(sideName + " stone", stone, hand, _glow);
            return hand;
        }

        /// <summary>
        /// The villagers and the masquerade come out of the spirit trees and dance round the stone circle.
        /// The camp stays hidden. The witch song plays instead of the village drums, as it does after the drum game.
        /// </summary>
        void BeginVictory()
        {
            if (_celebrating) return;
            _celebrating = true;
            if (_lamp != null) { _lamp.range = 8f; _lamp.intensity = 3.4f; }
            var song = _song != null && _song.clip != null;
            // feet on the spirit ground, centred on the stone circle (it sits 0.7 m in front of where you began)
            var village = _camp != null ? _camp.Village : null;
            if (village != null) village.CelebrateAround(_arena, new Vector3(0f, -1.42f, 0.7f), 1.9f, 4.2f, !song);
            if (song) StartSong(SongDelay);
        }

        void EndVictory()
        {
            if (!_celebrating && !_songOn) return;
            _celebrating = false;
            StopSong();
            if (_lamp != null) { _lamp.range = 4.5f; _lamp.intensity = 2.2f; }
            var village = _camp != null ? _camp.Village : null;
            if (village != null) village.Celebrate(false);
        }

        void StartSong(float delay)
        {
            if (_song == null || _song.clip == null) return;
            _song.Stop();
            _song.time = 0f;
            _song.volume = 0f;
            _songAt = AudioSettings.dspTime + delay;
            _song.PlayScheduled(_songAt);
            _songOn = true;
        }

        void StopSong()
        {
            if (_song != null) _song.Stop();
            _songOn = false;
        }

        /// <summary>The same fade as the drum game: in over a moment, out over the last part of the song.</summary>
        void Song()
        {
            if (!_songOn || _song == null || !_song.isPlaying) return;
            var t = _song.time;
            var left = _song.clip.length - t;
            _song.volume = SongLevel * Mathf.Clamp01(t / SongFadeIn) * Mathf.Clamp01(left / SongFadeOut);
        }

        /// <summary>A dark clearing past the palm: ground, a ring, and trees. No village and no elder.</summary>
        void BuildWorld()
        {
            // The sky shader draws the inside of the sphere (Cull Front): stars and a moon over the clearing, opaque, so nothing of the camp or the room shows.
            var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "Spirit sky";
            dome.transform.SetParent(_arena, false);
            dome.transform.localScale = Vector3.one * 20f;
            var collider = dome.GetComponent<Collider>();
            if (collider != null) Release(collider);
            var sky = dome.GetComponent<MeshRenderer>();
            sky.sharedMaterial = _night;
            sky.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sky.receiveShadows = false;
            _forest.Add(dome);

            var ground = new TrailBuilder.MeshKit();
            ground.Cylinder(new Vector3(0f, -1.42f, 1.1f), 8f, 8f, 0.06f, new Color(0.035f, 0.04f, 0.05f), new Color(0.06f, 0.055f, 0.07f), 28);
            for (var i = 0; i < 16; i++)
            {
                var a = i / 16f * Mathf.PI * 2f;
                var at = new Vector3(Mathf.Sin(a) * 1.15f, -1.36f, Mathf.Cos(a) * 1.15f + 0.7f);
                ground.Ellipsoid(at, new Vector3(0.07f, 0.035f, 0.05f), Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f), new Color(0.16f, 0.13f, 0.1f), 6, 3);
            }
            _forest.Add(Mesh("Spirit ground", ground, _arena, _material).gameObject);

            var trees = new TrailBuilder.MeshKit();
            for (var i = 0; i < 8; i++)
            {
                var a = (-70f + i * 20f) * Mathf.Deg2Rad;
                var trunk = new Vector3(Mathf.Sin(a) * 4.6f, -1.4f, Mathf.Cos(a) * 4.2f + 0.5f);
                var crown = trunk + new Vector3(0f, 2.5f, 0f);
                trees.Limb(trunk, crown, 0.08f, 0.045f, new Color(0.1f, 0.07f, 0.04f), 6);
                for (var f = 0; f < 6; f++)
                {
                    var yaw = (f / 6f) * Mathf.PI * 2f + i * 0.4f;
                    var mid = crown + new Vector3(Mathf.Sin(yaw) * 0.32f, -0.08f, Mathf.Cos(yaw) * 0.24f);
                    trees.Ellipsoid(mid, new Vector3(0.42f, 0.018f, 0.08f), Quaternion.Euler(32f, -yaw * Mathf.Rad2Deg, 8f), new Color(0.04f, 0.16f, 0.07f), 5, 2);
                }
            }
            _forest.Add(Mesh("Spirit palms", trees, _arena, _material).gameObject);

            var lamp = new GameObject("Spirit light");
            lamp.transform.SetParent(_arena, false);
            lamp.transform.localPosition = new Vector3(0f, 1.2f, 1.1f);
            var light = _lamp = lamp.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.95f, 0.78f, 0.55f);
            light.intensity = 2.2f;
            light.range = 4.5f;
            light.shadows = LightShadows.None;

            var veil = new TrailBuilder.MeshKit();
            veil.Ellipsoid(Vector3.zero, Vector3.one * 0.2f, Quaternion.identity, Color.white, 12, 8);
            _hurtVeil = Mesh("Hurt flash", veil, _arena, _veil);
            for (var i = 0; i < 2; i++)
            {
                var dot = new TrailBuilder.MeshKit();
                dot.Ellipsoid(Vector3.zero, Vector3.one * 0.022f, Quaternion.identity, Color.white, 10, 6);
                _palms[i] = Mesh(i == 0 ? "Your hand A" : "Your hand B", dot, _arena, _palm);
            }
            _hurtVeil.gameObject.SetActive(false);
        }

        Transform Mesh(string name, TrailBuilder.MeshKit builder, Transform parent, Material material)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Own(builder.Build(name));
            var draw = go.AddComponent<MeshRenderer>(); draw.sharedMaterial = material;
            draw.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        /// <summary>Taking the headset off or opening the system menu freezes the fight; it carries on by itself when you return.</summary>
        void OnApplicationPause(bool paused) { _focused = !paused; ResetHands(); }
        void OnApplicationFocus(bool focus) { _focused = focus; ResetHands(); }
        void OnDestroy()
        {
            Release(_arena != null ? _arena.gameObject : null);
            Release(_hud != null ? _hud.gameObject : null);
            foreach (var item in _owned) Release(item);
        }
    }
}
