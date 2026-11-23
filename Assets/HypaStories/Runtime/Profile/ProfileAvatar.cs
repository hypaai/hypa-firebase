using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HypaStories
{
    /// <summary>
    /// The profile character: a rigged human built from the MakeHuman CC0 system assets (Resources/HypaAvatar, made by
    /// tools/avatar/build_avatar.py). Every part (body, eyes, brows, lashes, each hair style, outfit and pair of shoes) is its own
    /// skinned mesh on one skeleton, and every part carries the same face and body blend shapes, so clothes and hair follow the
    /// sliders. Arms are lowered from the bind pose here; breathing, a small weight shift and blinking keep it alive.
    /// </summary>
    public sealed class ProfileAvatar : MonoBehaviour
    {
        public const string ModelPath = "HypaAvatar/hypa_avatar";
        const string TexturePath = "HypaAvatar/Textures/";

        /// <summary>Breathing, weight shift and blinking, re-posed and re-baked every frame. Off for a still (the profile photo):
        /// then nothing runs per frame, and <see cref="SnapPose"/> poses and bakes once.</summary>
        public bool Animate = true;

        /// <summary>Draw re-baked plain meshes instead of GPU skinning (see <see cref="Storyteller.BakeSkinning"/>: skinned meshes didn't draw in the XR view).</summary>
        public bool BakeSkinning = true;

        /// <summary>Unscaled standing height of the model, in model units (metres).</summary>
        public float Height { get; private set; } = 1.7f;
        /// <summary>Eye and chest heights at the current height setting.</summary>
        public float EyeHeight => _eye * _heightScale;
        public float ChestHeight => _chest * _heightScale;

        static readonly (string slider, string low, string high)[] Shapes =
        {
            ("frame", "frame_feminine", "frame_masculine"), ("weight", "weight_low", "weight_high"), ("muscle", "muscle_low", "muscle_high"),
            ("headShape", "head_round", "head_square"), ("jaw", "jaw_narrow", "jaw_wide"), ("chin", "chin_soft", "chin_strong"),
            ("cheeks", "cheeks_soft", "cheeks_high"), ("noseWidth", "nose_narrow", "nose_wide"), ("noseLength", "nose_short", "nose_long"),
            ("lips", "lips_thin", "lips_full"), ("eyeSize", "eyes_small", "eyes_large"),
        };

        sealed class Part
        {
            public SkinnedMeshRenderer Skin;
            public Material Material;
            public Mesh Baked;
            public MeshRenderer Draw;
            public Dictionary<string, (int index, float full)> Shapes = new Dictionary<string, (int, float)>();
        }

        readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        readonly List<Material> _materials = new List<Material>();
        readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        readonly Dictionary<Transform, Quaternion> _rest = new Dictionary<Transform, Quaternion>();
        readonly List<(Transform bone, Transform next, float curl, float side)> _fingers = new List<(Transform, Transform, float, float)>();
        readonly Dictionary<int, int[]> _covered = new Dictionary<int, int[]>();
        Mesh _bodyMesh;
        int[] _bodyTriangles, _vertexBits;
        int _shownMask = -1;
        Transform _model, _hips, _spine, _chestBone, _neck, _head, _armL, _armR, _foreL, _foreR, _handL, _handR;
        Shader _shader;
        float _eye = 1.6f, _chest = 1.3f, _heightScale = 1f, _blinkAt, _blink;
        Vector4 _clip = new Vector4(0, 0, 0, 1);
        bool _posed;

        static readonly int ColorId = Shader.PropertyToID("_Color"), TintId = Shader.PropertyToID("_TintAmount"), ClipId = Shader.PropertyToID("_ClipPlane");

        /// <summary>Loads the model. False when it isn't in this build (nothing is drawn).</summary>
        public bool Build()
        {
            var prefab = Resources.Load<GameObject>(ModelPath);
            _shader = Shader.Find("HypaStories/Avatar");
            if (prefab == null || _shader == null)
            {
                Debug.LogWarning("Hypa Stories: the profile character (Resources/" + ModelPath + ") or its shader is missing.");
                return false;
            }
            _model = Instantiate(prefab, transform).transform;
            _model.name = "Character model";
            _model.localPosition = Vector3.zero; _model.localRotation = Quaternion.identity;
            foreach (var r in _model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var part = new Part { Skin = r };
                r.updateWhenOffscreen = true;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                part.Material = new Material(_shader) { name = r.name };
                _materials.Add(part.Material);
                r.sharedMaterial = part.Material;
                var mesh = r.sharedMesh;
                if (mesh != null)
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                        part.Shapes[mesh.GetBlendShapeName(i)] = (i, mesh.GetBlendShapeFrameWeight(i, mesh.GetBlendShapeFrameCount(i) - 1));
                if (BakeSkinning)
                {
                    var go = new GameObject(r.name + " (baked)");
                    go.transform.SetParent(r.transform, false);
                    part.Baked = new Mesh { name = r.name + " baked" };
                    part.Baked.MarkDynamic();
                    go.AddComponent<MeshFilter>().sharedMesh = part.Baked;
                    part.Draw = go.AddComponent<MeshRenderer>();
                    part.Draw.sharedMaterial = part.Material;
                    part.Draw.shadowCastingMode = ShadowCastingMode.Off;
                    part.Draw.receiveShadows = false;
                    r.enabled = false;
                }
                _parts[r.name] = part;
            }
            FindBones();
            ReadOutfitMasks();
            return true;
        }

        /// <summary>
        /// The body's vertex colour holds one bit per garment (outfits in catalogue order, then shoes): the skin it covers.
        /// Covered triangles are left out so skin never shows through clothes.
        /// </summary>
        void ReadOutfitMasks()
        {
            if (!_parts.TryGetValue("body", out var body) || body.Skin.sharedMesh == null) return;
            var source = body.Skin.sharedMesh;
            var colors = source.colors;
            if (colors == null || colors.Length != source.vertexCount || source.subMeshCount != 1) return;
            _vertexBits = new int[colors.Length];
            for (int i = 0; i < colors.Length; i++)
                _vertexBits[i] = Mathf.RoundToInt(colors[i].r * 255f) | Mathf.RoundToInt(colors[i].g * 255f) << 8 | Mathf.RoundToInt(colors[i].b * 255f) << 16;
            _bodyMesh = Instantiate(source);
            _bodyMesh.name = source.name + " (fitted)";
            _bodyTriangles = source.triangles;
            body.Skin.sharedMesh = _bodyMesh;
        }

        void CoverBody(int outfit, int shoes)
        {
            if (_bodyMesh == null) return;
            int mask = (outfit >= 0 ? 1 << outfit : 0) | (shoes >= 0 ? 1 << (ProfileCatalog.Outfits.Length + shoes) : 0);
            if (mask == _shownMask) return;
            _shownMask = mask;
            if (!_covered.TryGetValue(mask, out var triangles))
            {
                var kept = new List<int>(_bodyTriangles.Length);
                for (int i = 0; i < _bodyTriangles.Length; i += 3)
                {
                    int a = _bodyTriangles[i], b = _bodyTriangles[i + 1], c = _bodyTriangles[i + 2];
                    if ((_vertexBits[a] & _vertexBits[b] & _vertexBits[c] & mask) != 0) continue;
                    kept.Add(a); kept.Add(b); kept.Add(c);
                }
                _covered[mask] = triangles = kept.ToArray();
            }
            _bodyMesh.SetTriangles(triangles, 0, false);
        }

        void FindBones()
        {
            var bones = new Dictionary<string, Transform>();
            foreach (var t in _model.GetComponentsInChildren<Transform>(true)) if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            Transform B(params string[] names) { foreach (var n in names) if (bones.TryGetValue(n, out var t)) return t; return null; }
            _hips = B("Hips", "pelvis");
            _spine = B("Spine1", "spine_02", "Spine");
            _chestBone = B("Spine2", "spine_03");
            _neck = B("Neck", "neck_01");
            _head = B("Head", "head");
            _armL = B("LeftArm", "upperarm_l"); _armR = B("RightArm", "upperarm_r");
            _foreL = B("LeftForeArm", "lowerarm_l"); _foreR = B("RightForeArm", "lowerarm_r");
            _handL = B("LeftHand", "hand_l"); _handR = B("RightHand", "hand_r");
            foreach (var t in new[] { _hips, _spine, _chestBone, _neck, _head, _armL, _armR, _foreL, _foreR })
                if (t != null) _rest[t] = t.localRotation;
            // relaxed fingers: each joint curls a little more toward the pinky
            string[] fingers = { "Index", "Middle", "Ring", "Pinky" };
            foreach (var (side, sign) in new[] { ("Left", 1f), ("Right", -1f) })
                for (int f = 0; f < fingers.Length; f++)
                    for (int j = 1; j <= 3; j++)
                    {
                        var bone = B(side + "Hand" + fingers[f] + j);
                        var next = j < 3 ? B(side + "Hand" + fingers[f] + (j + 1)) : null;
                        if (bone == null) continue;
                        _rest[bone] = bone.localRotation;
                        _fingers.Add((bone, next, (j == 1 ? 10f : j == 2 ? 22f : 14f) + f * 4f, sign));
                    }
            // heights from the bind pose, before anything moves
            float Y(Transform t, float fallback) => t == null ? fallback : _model.InverseTransformPoint(t.position).y;
            _eye = Y(_head, 1.5f) + 0.1f;
            _chest = Y(_chestBone, 1.3f);
            Height = _eye + 0.12f;
        }

        public void Apply(StoryProfile profile)
        {
            if (_model == null) return;
            var p = profile.Copy(); p.Normalize();
            _heightScale = Mathf.Lerp(0.93f, 1.07f, p.height);
            _model.localScale = Vector3.one * _heightScale;

            Show("brow_", ProfileCatalog.Brows[p.brows].Id);
            Show("hair_", ProfileCatalog.HairStyles[p.hairStyle].Id);
            Show("outfit_", ProfileCatalog.Outfits[p.outfit].Id);
            Show("shoes_", ProfileCatalog.Shoes[p.shoes].Id);
            CoverBody(ProfileCatalog.Outfits[p.outfit].Id != null ? p.outfit : -1, ProfileCatalog.Shoes[p.shoes].Id != null ? p.shoes : -1);

            var tone = ProfileCatalog.SkinTones[p.skin];
            var hair = ProfileCatalog.HairColors[p.hairColor].Color;
            var cloth = ProfileCatalog.OutfitColors[p.outfitColor];
            foreach (var pair in _parts)
            {
                var name = pair.Key; var m = pair.Value.Material;
                if (name == "body")
                {
                    Surface(m, "skin_" + tone.Band, tone.Color * 1.25f, 1f, 0f, 2, wrap: 0.45f, spec: 0.16f, gloss: 0.32f, scatter: new Color(0.62f, 0.2f, 0.12f, 0.75f));
                    Normal(m, "skin_normal");
                }
                // the eye's outer cornea shell is transparent in the texture
                else if (name == "eyes") Surface(m, ProfileCatalog.EyeColors[p.eyeColor].Id, Color.white, 0f, 0.5f, 2, wrap: 0.2f, spec: 0.9f, gloss: 0.92f);
                else if (name == "eyelashes") Surface(m, "eyelashes", new Color(0.08f, 0.06f, 0.05f), 1f, 0.3f, 0, wrap: 0.5f, spec: 0f, gloss: 0.1f);
                else if (name.StartsWith("brow_")) Surface(m, name, Color.Lerp(hair, Color.black, 0.35f) * 1.3f, 1f, 0.3f, 0, wrap: 0.5f, spec: 0.05f, gloss: 0.2f);
                else if (name.StartsWith("hair_")) Surface(m, name, hair * 1.35f, 1f, 0.38f, 0, wrap: 0.55f, spec: 0.28f, gloss: 0.42f, scatter: new Color(0.3f, 0.15f, 0.08f, 0.4f));
                else if (name.StartsWith("outfit_"))
                    Surface(m, cloth.Original ? name : name + "_grey", cloth.Original ? Color.white : cloth.Color * 1.45f, cloth.Original ? 0f : 1f, 0.3f, 0, wrap: 0.35f, spec: 0.08f, gloss: 0.18f);
                else if (name.StartsWith("shoes_")) Surface(m, name, Color.white, 0f, 0.3f, 2, wrap: 0.3f, spec: 0.35f, gloss: 0.5f);
                m.SetVector(ClipId, _clip);
            }

            foreach (var (slider, low, high) in Shapes)
            {
                float v = Slider(p, slider);
                SetShape(low, Mathf.Clamp01((0.5f - v) * 2f));
                SetShape(high, Mathf.Clamp01((v - 0.5f) * 2f));
            }
            Bake();
        }

        static float Slider(StoryProfile p, string name) => name switch
        {
            "frame" => p.frame, "weight" => p.weight, "muscle" => p.muscle, "headShape" => p.headShape, "jaw" => p.jaw, "chin" => p.chin,
            "cheeks" => p.cheeks, "noseWidth" => p.noseWidth, "noseLength" => p.noseLength, "lips" => p.lips, "eyeSize" => p.eyeSize, _ => 0.5f,
        };

        /// <summary>Of every part whose name starts with <paramref name="prefix"/>, only prefix + id is shown (none for a null id).</summary>
        void Show(string prefix, string id)
        {
            foreach (var pair in _parts)
            {
                if (!pair.Key.StartsWith(prefix)) continue;
                bool on = id != null && pair.Key == prefix + id;
                pair.Value.Skin.gameObject.SetActive(on);
            }
        }

        void SetShape(string shape, float amount)
        {
            foreach (var part in _parts.Values)
                if (part.Shapes.TryGetValue(shape, out var s)) part.Skin.SetBlendShapeWeight(s.index, amount * s.full);
        }

        void Surface(Material m, string texture, Color tint, float tintAmount, float cutoff, int cull, float wrap, float spec, float gloss, Color scatter = default)
        {
            m.mainTexture = Texture(texture) ?? Texture2D.whiteTexture;
            m.SetColor(ColorId, tint);
            m.SetFloat(TintId, tintAmount);
            m.SetFloat("_Cutoff", cutoff);
            m.SetFloat("_Cull", cull);
            m.SetFloat("_Wrap", wrap);
            m.SetFloat("_Spec", spec);
            m.SetFloat("_Gloss", gloss);
            m.SetColor("_Scatter", scatter);
            m.renderQueue = cutoff > 0 ? (int)RenderQueue.AlphaTest + (texture.StartsWith("hair_") ? 10 : 0) : (int)RenderQueue.Geometry;
        }

        void Normal(Material m, string texture) { var t = Texture(texture); if (t != null) m.SetTexture("_BumpMap", t); }

        Texture2D Texture(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!_textures.TryGetValue(name, out var t)) _textures[name] = t = Resources.Load<Texture2D>(TexturePath + name);
            return t;
        }

        /// <summary>Hides everything on the far side of <paramref name="plane"/> (the stage's foot in a close-up).</summary>
        public void SetClip(Plane plane, bool on)
        {
            var v = on ? new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance) : new Vector4(0, 0, 0, 1);
            if (v == _clip) return;
            _clip = v;
            foreach (var m in _materials) m.SetVector(ClipId, v);
        }

        /// <summary>Lowers the arms from the bind pose at once (the first frame shouldn't show a T).</summary>
        public void SnapPose() { Pose(0f); Bake(); }

        void LateUpdate()
        {
            if (_model == null || !Animate) return;
            Pose(Time.unscaledTime);
            Bake();
        }

        /// <summary>
        /// Rotations are applied in model space around fixed axes, so they don't depend on the rig's bone axes:
        /// arms hang about 10 degrees out from the body, elbows soften, the chest breathes, the head drifts a little.
        /// </summary>
        void Pose(float t)
        {
            foreach (var pair in _rest) pair.Key.localRotation = pair.Value;
            var up = _model.up; var right = _model.right; var forward = _model.forward;
            Hang(_armL, _foreL, _handL, 1f); Hang(_armR, _foreR, _handR, -1f);
            void Hang(Transform arm, Transform fore, Transform hand, float side)
            {
                if (arm == null || fore == null) return;
                var dir = (fore.position - arm.position).normalized;
                // follow the bone to tell which side it's on (glTFast mirrors X on import)
                float sign = Mathf.Sign(Vector3.Dot(dir, right));
                if (sign == 0) sign = side;
                var want = (right * sign * Mathf.Sin(10f * Mathf.Deg2Rad) - up * Mathf.Cos(10f * Mathf.Deg2Rad)).normalized;
                arm.rotation = Quaternion.FromToRotation(dir, want) * arm.rotation;
                if (hand == null) return;
                // soft elbows: the forearm leans a little toward the front (glTF's front, +Z, is kept on import)
                var foreDir = (hand.position - fore.position).normalized;
                fore.rotation = Quaternion.FromToRotation(foreDir, (foreDir + forward * 0.22f).normalized) * fore.rotation;
            }
            // the palms face the thighs once the arms hang: curl each finger segment toward the body's middle
            foreach (var (bone, next, curl, side) in _fingers)
            {
                var tip = next != null ? next.position : bone.position + (bone.position - bone.parent.position);
                var dir = (tip - bone.position).normalized;
                float sign = Mathf.Sign(Vector3.Dot(bone.position - _model.position, right));
                if (sign == 0) sign = side;
                var palm = -right * sign;
                var axis = Vector3.Cross(dir, palm);
                if (axis.sqrMagnitude < 1e-6f) continue;
                bone.rotation = Quaternion.AngleAxis(curl, axis.normalized) * bone.rotation;
            }
            float breath = Mathf.Sin(t * 1.5f);
            if (_chestBone != null) _chestBone.rotation = Quaternion.AngleAxis(breath * 0.9f, right) * _chestBone.rotation;
            if (_spine != null) _spine.rotation = Quaternion.AngleAxis(Mathf.Sin(t * 0.37f) * 0.8f, forward) * _spine.rotation;
            if (_hips != null) _hips.rotation = Quaternion.AngleAxis(Mathf.Sin(t * 0.37f + 1f) * 0.6f, forward) * _hips.rotation;
            if (_head != null)
                _head.rotation = Quaternion.AngleAxis(Mathf.Sin(t * 0.29f) * 3.5f, up) * Quaternion.AngleAxis(Mathf.Sin(t * 0.41f) * 1.2f - 1f, right) * _head.rotation;

            // blink every few seconds
            if (t > _blinkAt) { _blink = 1f; _blinkAt = t + Random.Range(2.2f, 5.5f); }
            _blink = Mathf.MoveTowards(_blink, 0f, Time.unscaledDeltaTime * 7f);
            SetShape("blink", 1f - Mathf.Abs(_blink * 2f - 1f));
            _posed = true;
        }

        void Bake()
        {
            if (!BakeSkinning || !_posed) return;
            foreach (var part in _parts.Values)
                // useScale: the stage scales the character, and the baked mesh's own transform applies that scale once already
                if (part.Baked != null && part.Skin.gameObject.activeInHierarchy) part.Skin.BakeMesh(part.Baked, true);
        }

        void OnDestroy()
        {
            foreach (var m in _materials) if (m != null) Destroy(m);
            foreach (var part in _parts.Values) if (part.Baked != null) Destroy(part.Baked);
            if (_bodyMesh != null) Destroy(_bodyMesh);
        }
    }
}
