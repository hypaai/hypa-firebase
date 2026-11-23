using System;
using System.Collections.Generic;
using HypaCore;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The profile card shown before the campfire: a portrait of your character (a round photo, rendered by its own camera only when
    /// the look changes, so nothing runs per frame), your username and display name under it, and five quick choices: skin tone, hair,
    /// hair colour, outfit and body. In a headset the card floats in front of you with your room around it and glides back when you turn; on a screen it is
    /// centred. Offline; uses the same pointing input as Stories.
    /// </summary>
    public sealed class ProfileCreator : MonoBehaviour
    {
        public const string EditRoute = "stories.profile";
        /// <summary>The portrait's own layer: only the portrait camera draws the character, and the main camera skips it.</summary>
        const int PortraitLayer = 29;
        const float RoomDistance = 1.0f, RoomScale = 0.56f;
        const int PhotoSize = 512;

        StoryProfile _draft;
        Action<bool> _finished;
        Camera _camera;
        Color _oldBackground, _oldAmbient;
        CameraClearFlags _oldClear;
        int _oldMask;
        UnityEngine.Rendering.AmbientMode _oldAmbientMode;
        float _reach;
        bool _portrait, _room, _closing, _hadProfile;
        int _lastWidth, _lastHeight;

        Transform _card, _options, _keyboard;
        Vector2 _size;
        TextMeshPro _note, _keyboardValue;
        KitButton _username, _displayName;
        TouchScreenKeyboard _nativeKeyboard;
        int _field = -1;

        // the portrait: a small studio far below the scene, switched on only to take the photo
        Transform _studio, _figure;
        ProfileAvatar _avatar;
        Camera _photoCamera;
        RenderTexture _photo;
        Material _photoMaterial;
        /// <summary>A three-quarter view, turned a little toward the card's options.</summary>
        const float PhotoYaw = 18f;
        /// <summary>Frames left to retake the photo: a just-loaded character's skinned meshes bake empty on their first frame.</summary>
        int _retake;

        public static void Show(StoryAssets assets, Action<bool> finished)
        {
            Kit.Init(assets);
            var view = new GameObject("Your profile").AddComponent<ProfileCreator>();
            view._finished = finished;
            view.Open();
        }

        void Open()
        {
            _camera = Camera.main;
            if (_camera == null) { _finished?.Invoke(false); Destroy(gameObject); return; }
            _draft = StoryProfileStore.Load();
            _hadProfile = _draft != null;
            _draft = _draft?.Copy() ?? new StoryProfile();
            _draft.Normalize();
            _oldBackground = _camera.backgroundColor; _oldClear = _camera.clearFlags; _oldMask = _camera.cullingMask;
            _oldAmbient = RenderSettings.ambientLight; _oldAmbientMode = RenderSettings.ambientMode;
            _camera.cullingMask &= ~(1 << PortraitLayer);
            // a warm fill so the side of the face away from the key light isn't black
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.38f, 0.35f);
            var pointer = PinchPointer.EnsureExists(); _reach = pointer.MaxDistance; pointer.MaxDistance = 8f;
            BuildPortraitStudio();
            SetMode(UnityEngine.XR.XRSettings.isDeviceActive);
        }

        /// <summary>
        /// Headset (the card floats in your room) or screen. The XR session starts a few frames after the scene, and the card opens on
        /// its first frame, so <see cref="Update"/> switches when it comes up.
        /// </summary>
        void SetMode(bool room)
        {
            _room = room;
            var follow = GetComponent<FollowView>();
            var forward = _camera.transform.forward;
            if (room)
            {
                // keep the camera's see-through clear colour (PassthroughKeep), so your room shows around the card
                var clear = _oldBackground; clear.a = 0f;
                _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = clear;
                forward.y = 0;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                transform.SetPositionAndRotation(_camera.transform.position + forward.normalized * RoomDistance, Quaternion.LookRotation(forward.normalized));
                if (follow == null) follow = gameObject.AddComponent<FollowView>();
                follow.Distance = RoomDistance; follow.Height = -0.04f; follow.Threshold = 40f; follow.Speed = 2.5f;
                follow.Snap();
            }
            else
            {
                if (follow != null) Destroy(follow);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.02f, 0.019f, 0.018f);
                transform.SetPositionAndRotation(_camera.transform.position + forward * 1.9f, Quaternion.LookRotation(forward));
            }
            Layout();
        }

        // ---------------------------------------------------------------- the portrait

        void BuildPortraitStudio()
        {
            _studio = new GameObject("Profile portrait studio").transform;
            _studio.position = new Vector3(0f, -400f, 0f);
            _figure = new GameObject("Character").transform;
            _figure.SetParent(_studio, false);
            _figure.localRotation = Quaternion.Euler(0, PhotoYaw, 0);
            _avatar = _figure.gameObject.AddComponent<ProfileAvatar>();
            _avatar.Animate = false;
            bool built = _avatar.Build();
            foreach (var t in _figure.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PortraitLayer;

            // head and shoulders: aim a little below the eyes, a long lens like a portrait photographer's
            float eye = built ? _avatar.EyeHeight : 1.6f;
            var target = _studio.position + new Vector3(0f, eye - 0.12f, 0f);
            // what the card shows: a plain colour texture (no depth, no multisampling, about 1 MB); TakePhoto renders into a temporary
            // multisampled target and copies the result here
            _photo = new RenderTexture(PhotoSize, PhotoSize, 0) { name = "Profile portrait" };
            _photo.Create();
            _photoCamera = new GameObject("Portrait camera").AddComponent<Camera>();
            _photoCamera.transform.SetParent(_studio, false);
            _photoCamera.transform.position = target + new Vector3(0f, 0.04f, 1.75f);
            _photoCamera.transform.rotation = Quaternion.LookRotation(target - _photoCamera.transform.position);
            _photoCamera.fieldOfView = 21f;
            _photoCamera.nearClipPlane = 0.3f; _photoCamera.farClipPlane = 5f;
            _photoCamera.cullingMask = 1 << PortraitLayer;
            _photoCamera.clearFlags = CameraClearFlags.SolidColor;
            _photoCamera.backgroundColor = new Color(0.2f, 0.16f, 0.13f, 1f);
            _photoCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _photoCamera.enabled = false; // rendered by hand in TakePhoto

            var key = new GameObject("Key light").AddComponent<Light>();
            key.transform.SetParent(_studio, false);
            key.transform.rotation = Quaternion.LookRotation(new Vector3(-0.55f, -0.45f, -1f));
            key.type = LightType.Directional; key.color = new Color(1f, 0.92f, 0.84f); key.intensity = 1.2f;
            key.shadows = LightShadows.None; key.cullingMask = 1 << PortraitLayer;
            var rim = new GameObject("Rim light").AddComponent<Light>();
            rim.transform.SetParent(_studio, false);
            rim.transform.position = _studio.position + new Vector3(0.6f, eye + 0.35f, -0.6f);
            rim.type = LightType.Point; rim.color = new Color(1f, 0.62f, 0.34f); rim.intensity = 1.6f; rim.range = 2.5f;
            rim.shadows = LightShadows.None; rim.cullingMask = 1 << PortraitLayer; rim.renderMode = LightRenderMode.ForceVertex;
            TakePhoto();
            _retake = 2;
        }

        /// <summary>
        /// Dresses the character in the draft, poses and bakes it once, renders one frame into the photo, then switches the studio off
        /// (its lights and meshes cost nothing until the next change).
        /// </summary>
        void TakePhoto()
        {
            if (_studio == null) return;
            _studio.gameObject.SetActive(true);
            _avatar.Apply(_draft);
            _avatar.SnapPose();
            var shot = RenderTexture.GetTemporary(PhotoSize, PhotoSize, 24, RenderTextureFormat.Default, RenderTextureReadWrite.Default, 4);
            _photoCamera.targetTexture = shot;
            _photoCamera.Render();
            _photoCamera.targetTexture = null;
            Graphics.Blit(shot, _photo); // resolves the multisampling
            RenderTexture.ReleaseTemporary(shot);
            _studio.gameObject.SetActive(false);
        }

        // a render texture's contents can be lost while the app is paused
        void OnApplicationFocus(bool focused) { if (focused && !_closing) _retake = 1; }

        // ---------------------------------------------------------------- the card

        void Layout()
        {
            EndInput();
            if (_card != null) { _card.gameObject.SetActive(false); Destroy(_card.gameObject); }
            _lastWidth = UnityEngine.Screen.width; _lastHeight = UnityEngine.Screen.height;
            _portrait = !_room && _camera.aspect < 0.85f;
            _size = _portrait ? new Vector2(1.0f, 1.9f) : new Vector2(1.56f, 1.02f);
            _card = new GameObject("Profile card").transform;
            _card.SetParent(transform, false);
            if (_room) _card.localScale = Vector3.one * RoomScale;
            else
            {
                float visibleHeight = 2 * 1.9f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                _card.localScale = Vector3.one * Mathf.Min(1f, visibleHeight * 0.9f / _size.y, visibleHeight * _camera.aspect * 0.94f / _size.x);
            }
            float top = _size.y * 0.5f, left = -_size.x * 0.5f;
            Kit.Panel(_card, "Card", new Vector3(0, 0, 0.05f), _size + new Vector2(0.06f, 0.06f), 0.06f,
                new Color(0.085f, 0.078f, 0.07f, 0.97f), new Color(0.04f, 0.037f, 0.034f, 0.97f), ProfileStyle.Line, 9, 0.35f);

            // header: back, what this is, and Done
            Kit.Button(_card, new Vector3(left + 0.08f, top - 0.08f, 0), new Vector2(0.075f, 0.075f), null, "chevron-left", KitStyle.Ghost, Cancel);
            Kit.Text(_card, "HYPA STORIES", 0.016f, new Vector3(left + 0.15f + 0.2f, top - 0.055f, 0), new Vector2(0.4f, 0.03f), KitTheme.Ember, TextAlignmentOptions.Left, KitFont.UiBold);
            Kit.Text(_card, _hadProfile ? "Your profile" : "Create your profile", 0.036f, new Vector3(left + 0.15f + 0.35f, top - 0.1f, 0),
                new Vector2(0.7f, 0.06f), KitTheme.Paper, TextAlignmentOptions.Left, KitFont.Serif);
            Kit.Button(_card, new Vector3(-left - 0.12f, top - 0.08f, 0), new Vector2(0.18f, 0.07f), "Done", "check", KitStyle.Primary, Done, em: 0.021f);

            // the portrait, with your name under it
            Vector2 photo = _portrait ? new Vector2(0f, 0.5f) : new Vector2(-0.45f, 0.06f);
            const float diameter = 0.5f;
            var ring = Kit.Panel(_card, "Photo ring", new Vector3(photo.x, photo.y, 0.01f), new Vector2(diameter + 0.03f, diameter + 0.03f), (diameter + 0.03f) * 0.5f,
                KitTheme.Clear, KitTheme.Clear, new Color(1f, 0.66f, 0.32f, 0.7f), 10, 0.4f);
            ring.Glow = new Color(1f, 0.6f, 0.3f, 0.25f); ring.BorderWidth = 0.003f; ring.Apply();
            PhotoQuad(new Vector3(photo.x, photo.y, 0f), diameter);
            float fieldY = photo.y - diameter * 0.5f - 0.09f, fieldW = diameter + 0.1f;
            _username = Kit.Button(_card, new Vector3(photo.x, fieldY, 0), new Vector2(fieldW, 0.068f), " ", null, KitStyle.Ghost, () => BeginInput(0), em: 0.02f, radius: 0.014f);
            _displayName = Kit.Button(_card, new Vector3(photo.x, fieldY - 0.083f, 0), new Vector2(fieldW, 0.068f), " ", null, KitStyle.Ghost, () => BeginInput(1), em: 0.02f, radius: 0.014f);
            _note = Kit.Text(_card, "", 0.014f, new Vector3(photo.x, fieldY - 0.145f, 0), new Vector2(fieldW + 0.1f, 0.03f), ProfileStyle.Muted);
            Note(null);

            Options();
            UpdateNames();
        }

        void PhotoQuad(Vector3 at, float size)
        {
            var shader = Shader.Find("HypaStories/Picture");
            if (shader == null) return;
            if (_photoMaterial == null)
            {
                _photoMaterial = new Material(shader) { name = "Profile portrait", mainTexture = _photo };
                _photoMaterial.SetFloat("_Round", 0.5f);
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Portrait";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_card, false);
            go.transform.localPosition = at;
            go.transform.localScale = new Vector3(size, size, 1f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _photoMaterial;
            r.sortingOrder = 12 + Kit.OrderBoost;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ---------------------------------------------------------------- the five choices

        float _x0, _width;
        /// <summary>Each section's way of showing the current choice: a tap updates these instead of rebuilding the buttons.</summary>
        readonly List<Action> _refresh = new List<Action>();

        void Options()
        {
            if (_options != null) { _options.gameObject.SetActive(false); Destroy(_options.gameObject); }
            _refresh.Clear();
            _options = new GameObject("Choices").transform;
            _options.SetParent(_card, false);
            float top;
            if (_portrait) { _x0 = -0.43f; _width = 0.86f; top = -0.08f; }
            else { _x0 = -0.1f; _width = 0.82f; top = 0.33f; }
            var p = _draft;
            float y = Swatches(top, "SKIN TONE", ProfileCatalog.SkinTones, () => p.skin, v => p.skin = v);
            y = Tiles(y, "HAIR", ProfileCatalog.HairStyles, () => p.hairStyle, v => p.hairStyle = v);
            y = Swatches(y, "HAIR COLOUR", ProfileCatalog.HairColors, () => p.hairColor, v => p.hairColor = v);
            y = Tiles(y, "OUTFIT", ProfileCatalog.Outfits, () => p.outfit, v => p.outfit = v);
            y = Section(y, "BODY");
            float w = (_width - 0.03f) / 3f;
            var frames = new KitButton[ProfileCatalog.Frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                int index = i;
                frames[i] = Kit.Button(_options, new Vector3(_x0 + w * 0.5f + i * (w + 0.015f), y - 0.032f, 0), new Vector2(w, 0.064f),
                    ProfileCatalog.Frames[i].label, null, KitStyle.Ghost, () => Choose(() => p.frame = ProfileCatalog.Frames[index].frame), em: 0.018f, radius: 0.014f);
            }
            _refresh.Add(() =>
            {
                int frame = ProfileCatalog.NearestFrame(p.frame);
                for (int i = 0; i < frames.Length; i++) frames[i].Selected = i == frame;
            });
            // a nudge for the undecided, level with the first section title
            Kit.Button(_options, new Vector3(_x0 + _width - 0.09f, top, 0), new Vector2(0.18f, 0.05f), "Surprise me", null, KitStyle.Quiet, Randomize, em: 0.016f);
            Refresh();
        }

        void Refresh() { foreach (var r in _refresh) r(); }

        float Section(float y, string title)
        {
            Kit.Text(_options, title, 0.015f, new Vector3(_x0 + _width * 0.5f, y, 0), new Vector2(_width, 0.03f), ProfileStyle.Muted, TextAlignmentOptions.Left, KitFont.UiBold);
            return y - 0.04f;
        }

        float Swatches(float y, string title, ProfileOption[] options, Func<int> selected, Action<int> set)
        {
            y = Section(y, title);
            float step = Mathf.Min(0.08f, _width / options.Length), size = Mathf.Min(0.058f, step - 0.014f);
            float rowY = y;
            Vector3 At(int i) => new Vector3(_x0 + size * 0.5f + i * step, rowY - size * 0.5f, 0);
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var b = Kit.Button(_options, At(i), new Vector2(size, size), null, null, KitStyle.Ghost, () => Choose(() => set(index)));
                var c = options[i].Color;
                b.enabled = false; // a swatch keeps its own colour instead of the themed paint
                b.Bg.Top = Color.Lerp(c, Color.white, 0.08f); b.Bg.Bottom = Color.Lerp(c, Color.black, 0.12f);
                b.Bg.Border = new Color(1f, 1f, 1f, 0.18f); b.Bg.Glow = KitTheme.Clear; b.Bg.Apply();
            }
            // one ring, moved to the chosen swatch
            var ring = Kit.Panel(_options, "Selected", Vector3.zero, new Vector2(size + 0.016f, size + 0.016f), (size + 0.016f) * 0.5f,
                KitTheme.Clear, KitTheme.Clear, KitTheme.Ember, 11, 0f);
            ring.BorderWidth = 0.0028f; ring.Glow = new Color(1f, 0.6f, 0.3f, 0.35f); ring.Apply();
            _refresh.Add(() => ring.transform.localPosition = At(selected()) + new Vector3(0, 0, 0.002f));
            return y - size - 0.03f;
        }

        float Tiles(float y, string title, ProfileOption[] options, Func<int> selected, Action<int> set)
        {
            y = Section(y, title);
            const float gap = 0.012f, height = 0.118f;
            float w = (_width - gap * (options.Length - 1)) / options.Length;
            var buttons = new KitButton[options.Length];
            var labels = new TextMeshPro[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var at = new Vector3(_x0 + w * 0.5f + i * (w + gap), y - height * 0.5f, 0);
                var thumb = options[i].Thumb;
                var b = buttons[i] = Kit.Button(_options, at, new Vector2(w, height), thumb == null ? options[i].Label : null, null, KitStyle.Ghost,
                    () => Choose(() => set(index)), em: 0.015f, radius: 0.012f);
                if (thumb == null) continue;
                Picture(b.transform, thumb, new Vector3(0, 0.011f, -0.004f), height - 0.03f);
                labels[i] = Kit.Text(b.transform, options[i].Label, 0.013f, new Vector3(0, -height * 0.5f + 0.014f, -0.004f),
                    new Vector2(w, 0.022f), KitTheme.Dim);
                labels[i].textWrappingMode = TextWrappingModes.NoWrap;
            }
            _refresh.Add(() =>
            {
                int chosen = selected();
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].Selected = i == chosen;
                    if (labels[i] != null) labels[i].color = i == chosen ? KitTheme.Ember : KitTheme.Dim;
                }
            });
            return y - height - 0.03f;
        }

        static readonly Dictionary<Texture2D, Material> Pictures = new Dictionary<Texture2D, Material>();

        /// <summary>
        /// An option's picture in full colour (Kit icons keep only alpha). Like Kit's icons, each texture gets its own material:
        /// a texture set only through a property block stays blank on the Quest.
        /// </summary>
        static void Picture(Transform parent, Texture2D texture, Vector3 at, float size)
        {
            if (!Pictures.TryGetValue(texture, out var material) || material == null)
            {
                var shader = Shader.Find("HypaStories/Picture");
                if (shader == null) return;
                Pictures[texture] = material = new Material(shader) { name = "Picture " + texture.name, mainTexture = texture };
                material.SetFloat("_Round", 0.08f);
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Picture " + texture.name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = new Vector3(size, size, 1f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.sortingOrder = 13 + Kit.OrderBoost;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void Choose(Action change)
        {
            change();
            TakePhoto();
            Refresh();
        }

        void Randomize()
        {
            var r = new System.Random();
            var p = _draft;
            p.skin = r.Next(ProfileCatalog.SkinTones.Length);
            p.hairStyle = r.Next(ProfileCatalog.HairStyles.Length);
            p.hairColor = r.Next(ProfileCatalog.HairColors.Length);
            p.outfit = r.Next(ProfileCatalog.Outfits.Length);
            p.frame = ProfileCatalog.Frames[r.Next(ProfileCatalog.Frames.Length)].frame;
            TakePhoto();
            Refresh();
        }

        // ---------------------------------------------------------------- saving and leaving

        void Done()
        {
            EndInput();
            var error = StoryProfile.UsernameError(_draft.username);
            if (error != null)
            {
                Note(string.IsNullOrEmpty(_draft.username) ? "Choose a username to continue." : error, true);
                BeginInput(0);
                return;
            }
            try { StoryProfileStore.Save(_draft); Close(true); }
            catch (Exception) { Note("Could not save your profile. Please try again.", true); }
        }

        void Cancel()
        {
            if (_hadProfile || HypaShell.Instance != null) Close(false);
            else Note("Create your profile to enter Stories.", true);
        }

        /// <summary>The line under the name fields: a hint, or what needs fixing.</summary>
        void Note(string text, bool error = false)
        {
            if (_note == null) return;
            _note.richText = false;
            _note.color = error ? ProfileStyle.Error : ProfileStyle.Muted;
            _note.text = text ?? "Username: 3–20 letters, numbers or _ · Saved on this device";
        }

        void Close(bool saved)
        {
            if (_closing) return;
            _closing = true; EndInput(); Restore();
            gameObject.SetActive(false);
            _finished?.Invoke(saved);
            Destroy(gameObject);
        }

        void Restore()
        {
            if (_camera != null) { _camera.backgroundColor = _oldBackground; _camera.clearFlags = _oldClear; _camera.cullingMask = _oldMask; }
            RenderSettings.ambientMode = _oldAmbientMode; RenderSettings.ambientLight = _oldAmbient;
            if (PinchPointer.Instance != null) PinchPointer.Instance.MaxDistance = _reach;
            if (_studio != null) Destroy(_studio.gameObject);
            if (_photo != null) { _photo.Release(); Destroy(_photo); }
            if (_photoMaterial != null) Destroy(_photoMaterial);
        }

        // ---------------------------------------------------------------- text entry

        void BeginInput(int field)
        {
            if (_field == field) return;
            EndInput(); _field = field;
            if (TouchScreenKeyboard.isSupported && !UnityEngine.XR.XRSettings.isDeviceActive)
                _nativeKeyboard = TouchScreenKeyboard.Open(Value, TouchScreenKeyboardType.Default, false, false, false, false,
                    field == 0 ? "Username" : "Display name", field == 0 ? 20 : 24);
            else Keyboard();
            UpdateNames();
        }

        string Value { get => _field == 0 ? _draft.username : _draft.displayName; set { if (_field == 0) _draft.username = value; else _draft.displayName = value; } }

        void Keyboard()
        {
            if (_card == null) return;
            _keyboard = new GameObject("Profile keyboard").transform; _keyboard.SetParent(_card, false);
            // in front of the card's lower half; a collider blocks pinches through to the card
            _keyboard.localPosition = new Vector3(_portrait ? 0f : 0.3f, -_size.y * 0.5f + 0.2f, -0.15f);
            var order = Kit.OrderBoost; Kit.OrderBoost = 20;
            try
            {
                Kit.Panel(_keyboard, "Keyboard", Vector3.zero, new Vector2(0.84f, 0.5f), 0.03f, new Color(0.11f, 0.1f, 0.09f), new Color(0.07f, 0.064f, 0.058f), KitTheme.Hairline, 10, 0.6f);
                var block = _keyboard.gameObject.AddComponent<BoxCollider>(); block.size = new Vector3(0.84f, 0.5f, 0.02f);
                _keyboard.gameObject.AddComponent<PinchTarget>();
                _keyboardValue = Kit.Text(_keyboard, "", 0.022f, new Vector3(0, 0.2f, -0.04f), new Vector2(0.74f, 0.04f), KitTheme.Paper);
                string[] rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm_" };
                for (int row = 0; row < rows.Length; row++)
                    for (int col = 0; col < rows[row].Length; col++)
                    {
                        char ch = rows[row][col];
                        Kit.Button(_keyboard, new Vector3((col - (rows[row].Length - 1) * 0.5f) * 0.076f, 0.135f - row * 0.072f, -0.01f), new Vector2(0.066f, 0.062f),
                            ch.ToString(), null, KitStyle.Ghost, () => Append(ch), em: 0.021f, radius: 0.01f);
                    }
                Kit.Button(_keyboard, new Vector3(-0.22f, -0.175f, -0.01f), new Vector2(0.26f, 0.062f), "Space", null, KitStyle.Ghost, () => Append(' '), em: 0.019f, radius: 0.01f);
                Kit.Button(_keyboard, new Vector3(0.04f, -0.175f, -0.01f), new Vector2(0.21f, 0.062f), "Delete", null, KitStyle.Ghost, Delete, em: 0.019f, radius: 0.01f);
                Kit.Button(_keyboard, new Vector3(0.28f, -0.175f, -0.01f), new Vector2(0.19f, 0.062f), "Done", null, KitStyle.Primary, EndInput, em: 0.019f, radius: 0.01f);
            }
            finally { Kit.OrderBoost = order; }
        }

        void Append(char ch)
        {
            if (_field < 0 || char.IsControl(ch) || ch == '<' || ch == '>') return;
            if (_field == 0 && !(ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || ch >= '0' && ch <= '9' || ch == '_')) return;
            if (Value.Length < (_field == 0 ? 20 : 24)) Value += ch;
            if (_field == 0) Note(null);
            UpdateNames();
        }

        void Delete() { if (_field >= 0 && Value.Length > 0) Value = Value.Substring(0, Value.Length - 1); UpdateNames(); }

        void EndInput()
        {
            if (_nativeKeyboard != null) { _nativeKeyboard.active = false; _nativeKeyboard = null; }
            if (_keyboard != null) { _keyboard.gameObject.SetActive(false); Destroy(_keyboard.gameObject); _keyboard = null; }
            _field = -1; UpdateNames();
        }

        void UpdateNames()
        {
            if (_draft == null) return;
            if (_keyboardValue != null && _field >= 0) { _keyboardValue.richText = false; _keyboardValue.text = Value + " |"; }
            if (_username != null)
            {
                _username.SetLabel(string.IsNullOrEmpty(_draft.username) ? "Choose a username" : "@" + _draft.username + (_field == 0 ? " |" : ""));
                _username.Label.richText = false; _username.Selected = _field == 0;
            }
            if (_displayName != null)
            {
                _displayName.SetLabel(string.IsNullOrEmpty(_draft.displayName) ? "Display name (optional)" : _draft.displayName + (_field == 1 ? " |" : ""));
                _displayName.Label.richText = false; _displayName.Selected = _field == 1;
            }
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            if (_closing) return;
            bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
            if (xr != _room) { SetMode(xr); return; }
            if (!_room && (UnityEngine.Screen.width != _lastWidth || UnityEngine.Screen.height != _lastHeight)) Layout();
            if (_retake > 0) { _retake--; TakePhoto(); }
            if (_field < 0 && Input.GetKeyDown(KeyCode.Escape)) Cancel();
            if (_field < 0) return;
            if (_nativeKeyboard != null)
            {
                string input = _nativeKeyboard.text;
                Value = "";
                foreach (char ch in input) Append(ch);
                if (_nativeKeyboard.status != TouchScreenKeyboard.Status.Visible) EndInput();
            }
            else foreach (char ch in Input.inputString)
            {
                if (ch == '\b') Delete();
                else if (ch == '\n' || ch == '\r') { EndInput(); break; }
                else Append(ch);
            }
        }

        void OnDestroy() { if (!_closing) { EndInput(); Restore(); } }
    }
}
