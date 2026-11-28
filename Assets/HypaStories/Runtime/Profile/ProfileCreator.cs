using System;
using System.Collections.Generic;
using HypaCore;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    public sealed class ProfileCreator : MonoBehaviour
    {
        public const string EditRoute = "stories.profile";
        const int PortraitLayer = 29;
        const float RoomDistance = 0.55f, RoomScale = 0.4f;
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
        KitButton _displayName;
        TouchScreenKeyboard _nativeKeyboard;
        bool _typing;

        Transform _studio, _figure;
        ProfileAvatar _avatar;
        Camera _photoCamera;
        RenderTexture _photo;
        Material _photoMaterial;
        const float PhotoYaw = 18f;
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
            if (!_hadProfile)
            {
                // A first profile starts finished (a random look and a friendly name), so a child can press Done straight away.
                var random = new System.Random();
                Roll(random);
                (_draft.username, _draft.displayName) = StoryProfile.FriendlyName(random);
            }
            _oldBackground = _camera.backgroundColor; _oldClear = _camera.clearFlags; _oldMask = _camera.cullingMask;
            _oldAmbient = RenderSettings.ambientLight; _oldAmbientMode = RenderSettings.ambientMode;
            _camera.cullingMask &= ~(1 << PortraitLayer);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.38f, 0.35f);
            var pointer = PinchPointer.EnsureExists(); _reach = pointer.MaxDistance; pointer.MaxDistance = 8f;
            BuildPortraitStudio();
            SetMode(UnityEngine.XR.XRSettings.isDeviceActive);
        }

        void SetMode(bool room)
        {
            _room = room;
            var follow = GetComponent<FollowView>();
            var forward = _camera.transform.forward;
            if (room)
            {
                var clear = _oldBackground; clear.a = 0f;
                _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = clear;
                forward.y = 0;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                transform.SetPositionAndRotation(_camera.transform.position + forward.normalized * RoomDistance, Quaternion.LookRotation(forward.normalized));
                if (follow == null) follow = gameObject.AddComponent<FollowView>();
                follow.Distance = RoomDistance; follow.Height = -0.05f; follow.Threshold = 40f; follow.Speed = 2.5f;
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

            float eye = built ? _avatar.EyeHeight : 1.6f;
            var target = _studio.position + new Vector3(0f, eye - 0.12f, 0f);
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
            _photoCamera.enabled = false;

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
            Graphics.Blit(shot, _photo);
            RenderTexture.ReleaseTemporary(shot);
            _studio.gameObject.SetActive(false);
        }

        void OnApplicationFocus(bool focused) { if (focused && !_closing) _retake = 1; }

        const float Pad = 0.07f, HeaderHeight = 0.17f, Photo = 0.42f, FieldHeight = 0.064f;

        void Layout()
        {
            EndInput();
            if (_card != null) { _card.gameObject.SetActive(false); Destroy(_card.gameObject); }
            _lastWidth = UnityEngine.Screen.width; _lastHeight = UnityEngine.Screen.height;
            _portrait = !_room && _camera.aspect < 0.85f;
            _size = _portrait ? new Vector2(1.0f, 1.7f) : new Vector2(1.56f, 1.0f);
            _card = new GameObject("Profile card").transform;
            _card.SetParent(transform, false);
            if (_room) _card.localScale = Vector3.one * RoomScale;
            else
            {
                float visibleHeight = 2 * 1.9f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                _card.localScale = Vector3.one * Mathf.Min(visibleHeight * 0.72f / _size.y, visibleHeight * _camera.aspect * 0.82f / _size.x);
            }
            float top = _size.y * 0.5f, left = -_size.x * 0.5f, right = -left;
            Kit.Panel(_card, "Card", new Vector3(0, 0, 0.05f), _size, 0.05f,
                new Color(0.09f, 0.083f, 0.075f, 0.97f), new Color(0.055f, 0.05f, 0.046f, 0.97f), ProfileStyle.Line, 9, 0.25f);

            float header = top - Pad - 0.03f;
            Flatten(Kit.Button(_card, new Vector3(left + Pad + 0.03f, header, 0), new Vector2(0.06f, 0.06f), null, "chevron-left", KitStyle.Quiet, Cancel));
            Kit.Text(_card, _hadProfile ? "Your profile" : "Create your profile", 0.034f, new Vector3(left + Pad + 0.08f + 0.3f, header, 0),
                new Vector2(0.6f, 0.06f), KitTheme.Paper, TextAlignmentOptions.Left, KitFont.Serif);
            Flatten(Kit.Button(_card, new Vector3(right - Pad - 0.08f, header, 0), new Vector2(0.16f, 0.06f), "Done", "check", KitStyle.Primary, Done, em: 0.019f));
            Flatten(Kit.Button(_card, new Vector3(right - Pad - 0.16f - 0.02f - 0.08f, header, 0), new Vector2(0.16f, 0.06f), "Surprise me", null, KitStyle.Quiet, Randomize, em: 0.016f));
            Kit.Panel(_card, "Divider", new Vector3(0, top - HeaderHeight, 0.01f), new Vector2(_size.x - Pad * 2, 0.0015f), 0.0007f,
                ProfileStyle.Line, ProfileStyle.Line, KitTheme.Clear, 10, 0f);

            float bodyTop = top - HeaderHeight - 0.035f;
            Vector2 photo = _portrait ? new Vector2(0f, bodyTop - Photo * 0.5f) : new Vector2(left + Pad + 0.27f, bodyTop - Photo * 0.5f - 0.01f);
            Kit.Panel(_card, "Photo edge", new Vector3(photo.x, photo.y, 0.01f), new Vector2(Photo + 0.012f, Photo + 0.012f), (Photo + 0.012f) * 0.5f,
                KitTheme.Clear, KitTheme.Clear, new Color(1f, 0.88f, 0.72f, 0.28f), 10, 0f);
            PhotoQuad(new Vector3(photo.x, photo.y, 0f), Photo);
            float fieldW = _portrait ? 0.62f : 0.54f, fieldY = photo.y - Photo * 0.5f - 0.035f - FieldHeight * 0.5f;
            _displayName = Field(new Vector3(photo.x, fieldY, 0), fieldW, BeginInput);
            _note = Kit.Text(_card, "", 0.0135f, new Vector3(photo.x, fieldY - FieldHeight * 0.5f - 0.028f, 0), new Vector2(fieldW, 0.03f), ProfileStyle.Muted, TextAlignmentOptions.Left);
            Note(null);

            if (_portrait) { _x0 = -_size.x * 0.5f + Pad; _width = _size.x - Pad * 2; _top = fieldY - FieldHeight - 0.12f; }
            else { _x0 = photo.x + 0.27f + 0.08f; _width = right - Pad - _x0; _top = bodyTop; }
            Options();
            UpdateNames();
        }

        KitButton Field(Vector3 at, float width, Action onPress)
        {
            var b = Kit.Button(_card, at, new Vector2(width, FieldHeight), " ", null, KitStyle.Ghost, onPress, em: 0.018f, radius: 0.012f);
            Flatten(b);
            b.Label.alignment = TextAlignmentOptions.Left;
            b.Label.rectTransform.sizeDelta = new Vector2(width - 0.05f, FieldHeight);
            return b;
        }

        static KitButton Flatten(KitButton b)
        {
            b.Flat = true;
            b.Bg.Shadow = 0f; b.Bg.Glow = KitTheme.Clear; b.Bg.Apply();
            return b;
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

        float _x0, _width, _top;
        readonly List<Action> _refresh = new List<Action>();
        const float LabelGap = 0.033f, SectionGap = 0.034f;

        void Options()
        {
            if (_options != null) { _options.gameObject.SetActive(false); Destroy(_options.gameObject); }
            _refresh.Clear();
            _options = new GameObject("Choices").transform;
            _options.SetParent(_card, false);
            var p = _draft;
            float y = Swatches(_top, "Skin tone", ProfileCatalog.SkinTones, () => p.skin, v => p.skin = v);
            y = Tiles(y, "Hair", ProfileCatalog.HairStyles, () => p.hairStyle, v => p.hairStyle = v);
            y = Swatches(y, "Hair colour", ProfileCatalog.HairColors, () => p.hairColor, v => p.hairColor = v);
            y = Tiles(y, "Outfit", ProfileCatalog.Outfits, () => p.outfit, v => p.outfit = v);
            Segments(y, "Body");
            Refresh();
        }

        void Refresh() { foreach (var r in _refresh) r(); }

        float Section(float y, string title)
        {
            var t = Kit.Text(_options, title.ToUpperInvariant(), 0.0145f, new Vector3(_x0 + _width * 0.5f, y, 0), new Vector2(_width, 0.03f),
                ProfileStyle.Muted, TextAlignmentOptions.Left, KitFont.UiBold);
            t.characterSpacing = 6f;
            return y - LabelGap;
        }

        float Swatches(float y, string title, ProfileOption[] options, Func<int> selected, Action<int> set)
        {
            y = Section(y, title);
            float step = Mathf.Min(0.079f, _width / options.Length), size = Mathf.Min(0.054f, step - 0.018f);
            float rowY = y;
            Vector3 At(int i) => new Vector3(_x0 + size * 0.5f + 0.006f + i * step, rowY - size * 0.5f - 0.006f, 0);
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var b = Flatten(Kit.Button(_options, At(i), new Vector2(size, size), null, null, KitStyle.Ghost, () => Choose(() => set(index))));
                var c = options[i].Color;
                b.enabled = false;
                b.Bg.Top = c; b.Bg.Bottom = Color.Lerp(c, Color.black, 0.08f);
                b.Bg.Border = new Color(1f, 1f, 1f, 0.12f); b.Bg.Apply();
            }
            var ring = Kit.Panel(_options, "Selected", Vector3.zero, new Vector2(size + 0.012f, size + 0.012f), (size + 0.012f) * 0.5f,
                KitTheme.Clear, KitTheme.Clear, KitTheme.Ember, 11, 0f);
            ring.BorderWidth = 0.0024f; ring.Apply();
            _refresh.Add(() => ring.transform.localPosition = At(selected()) + new Vector3(0, 0, 0.002f));
            return y - size - 0.012f - SectionGap;
        }

        float Tiles(float y, string title, ProfileOption[] options, Func<int> selected, Action<int> set)
        {
            y = Section(y, title);
            const float gap = 0.012f, height = 0.104f;
            float w = (_width - gap * (options.Length - 1)) / options.Length;
            var buttons = new KitButton[options.Length];
            var labels = new TextMeshPro[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var at = new Vector3(_x0 + w * 0.5f + i * (w + gap), y - height * 0.5f, 0);
                var thumb = options[i].Thumb;
                var b = buttons[i] = Flatten(Kit.Button(_options, at, new Vector2(w, height), thumb == null ? options[i].Label : null, null, KitStyle.Ghost,
                    () => Choose(() => set(index)), em: 0.014f, radius: 0.012f));
                if (thumb == null) continue;
                Picture(b.transform, thumb, new Vector3(0, 0.012f, -0.004f), height - 0.036f);
                labels[i] = Kit.Text(b.transform, options[i].Label, 0.0125f, new Vector3(0, -height * 0.5f + 0.015f, -0.004f),
                    new Vector2(w - 0.008f, 0.022f), KitTheme.Dim);
                labels[i].textWrappingMode = TextWrappingModes.NoWrap;
                labels[i].overflowMode = TextOverflowModes.Ellipsis;
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
            return y - height - SectionGap;
        }

        void Segments(float y, string title)
        {
            y = Section(y, title);
            var p = _draft;
            const float height = 0.058f, gap = 0.008f;
            float w = (_width - gap * (ProfileCatalog.Frames.Length - 1)) / ProfileCatalog.Frames.Length;
            var buttons = new KitButton[ProfileCatalog.Frames.Length];
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                buttons[i] = Flatten(Kit.Button(_options, new Vector3(_x0 + w * 0.5f + i * (w + gap), y - height * 0.5f, 0), new Vector2(w, height),
                    ProfileCatalog.Frames[i].label, null, KitStyle.Ghost, () => Choose(() => p.frame = ProfileCatalog.Frames[index].frame), em: 0.016f, radius: 0.012f));
            }
            _refresh.Add(() =>
            {
                int frame = ProfileCatalog.NearestFrame(p.frame);
                for (int i = 0; i < buttons.Length; i++) buttons[i].Selected = i == frame;
            });
        }

        static readonly Dictionary<Texture2D, Material> Pictures = new Dictionary<Texture2D, Material>();

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

        void Roll(System.Random r)
        {
            var p = _draft;
            p.skin = r.Next(ProfileCatalog.SkinTones.Length);
            p.hairStyle = r.Next(ProfileCatalog.HairStyles.Length);
            p.hairColor = r.Next(ProfileCatalog.HairColors.Length);
            p.outfit = r.Next(ProfileCatalog.Outfits.Length);
            p.frame = ProfileCatalog.Frames[r.Next(ProfileCatalog.Frames.Length)].frame;
        }

        void Randomize()
        {
            Roll(new System.Random());
            TakePhoto();
            Refresh();
        }

        void Done()
        {
            EndInput();
            try { StoryProfileStore.Save(_draft); Close(true); }
            catch (Exception) { Note("Could not save your profile. Please try again.", true); }
        }

        void Cancel()
        {
            if (_hadProfile || HypaShell.Instance != null) Close(false);
            else Note("Create your profile to enter Stories.", true);
        }

        void Note(string text, bool error = false)
        {
            if (_note == null) return;
            _note.richText = false;
            _note.color = error ? ProfileStyle.Error : ProfileStyle.Muted;
            _note.text = text ?? "Saved on this device";
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

        void BeginInput()
        {
            if (_typing) return;
            _typing = true;
            bool web = Application.platform == RuntimePlatform.WebGLPlayer;
            // Web uses physical typing when available, or the system keyboard on touch devices.
            bool useSystemKeyboard = TouchScreenKeyboard.isSupported && (web
                ? !TouchScreenKeyboard.isInPlaceEditingAllowed
                : Application.isMobilePlatform && !UnityEngine.XR.XRSettings.isDeviceActive);
            if (useSystemKeyboard)
                _nativeKeyboard = TouchScreenKeyboard.Open(Value, TouchScreenKeyboardType.Default, false, false, false, false,
                    "Your name", NameLength);
            else if (!web) Keyboard();
            UpdateNames();
        }

        const int NameLength = 24;
        string Value { get => _draft.displayName; set => _draft.displayName = value; }

        void Keyboard()
        {
            if (_card == null) return;
            _keyboard = new GameObject("Profile keyboard").transform; _keyboard.SetParent(_card, false);
            _keyboard.localPosition = new Vector3(_portrait ? 0f : 0.3f, -_size.y * 0.5f + 0.26f, -0.04f);
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
                        Flatten(Kit.Button(_keyboard, new Vector3((col - (rows[row].Length - 1) * 0.5f) * 0.076f, 0.135f - row * 0.072f, -0.01f), new Vector2(0.066f, 0.062f),
                            ch.ToString(), null, KitStyle.Ghost, () => Append(ch), em: 0.021f, radius: 0.01f));
                    }
                Flatten(Kit.Button(_keyboard, new Vector3(-0.22f, -0.175f, -0.01f), new Vector2(0.26f, 0.062f), "Space", null, KitStyle.Ghost, () => Append(' '), em: 0.019f, radius: 0.01f));
                Flatten(Kit.Button(_keyboard, new Vector3(0.04f, -0.175f, -0.01f), new Vector2(0.21f, 0.062f), "Delete", null, KitStyle.Ghost, Delete, em: 0.019f, radius: 0.01f));
                Flatten(Kit.Button(_keyboard, new Vector3(0.28f, -0.175f, -0.01f), new Vector2(0.19f, 0.062f), "Done", null, KitStyle.Primary, EndInput, em: 0.019f, radius: 0.01f));
            }
            finally { Kit.OrderBoost = order; }
        }

        void Append(char ch)
        {
            if (!_typing || char.IsControl(ch) || ch == '<' || ch == '>') return;
            if (Value.Length < NameLength) Value += ch;
            UpdateNames();
        }

        void Delete() { if (_typing && Value.Length > 0) Value = Value.Substring(0, Value.Length - 1); UpdateNames(); }

        void EndInput()
        {
            if (_nativeKeyboard != null) { _nativeKeyboard.active = false; _nativeKeyboard = null; }
            if (_keyboard != null) { _keyboard.gameObject.SetActive(false); Destroy(_keyboard.gameObject); _keyboard = null; }
            _typing = false; UpdateNames();
        }

        void UpdateNames()
        {
            if (_draft == null) return;
            if (_keyboardValue != null && _typing) { _keyboardValue.richText = false; _keyboardValue.text = Value + "|"; }
            if (_displayName != null)
            {
                bool empty = string.IsNullOrEmpty(_draft.displayName);
                _displayName.SetLabel(_typing ? _draft.displayName + "|" : empty ? "Your name" : _draft.displayName);
                _displayName.Label.richText = false; _displayName.Selected = _typing;
                _displayName.Label.alpha = empty && !_typing ? 0.45f : 1f;
            }
        }

        void Update()
        {
            if (_closing) return;
            bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
            if (xr != _room) { SetMode(xr); return; }
            if (!_room && (UnityEngine.Screen.width != _lastWidth || UnityEngine.Screen.height != _lastHeight)) Layout();
            if (_retake > 0) { _retake--; TakePhoto(); }
            if (!_typing && Input.GetKeyDown(KeyCode.Escape)) Cancel();
            if (!_typing) return;
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
