using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The other player at the fire (Story Circle, G09): the storyteller's stand-in body in that player's colour, seated on a log
    /// beside the fire, with a name tag. The head follows the player's head (on a phone or the web, where the camera looks), and on
    /// Quest the hands follow theirs, played back smoothly by <see cref="PoseTrack"/>. Poses are in the camp's space, where a
    /// seated player faces the fire along +z, so a head turned towards their fire turns towards this one.
    /// </summary>
    public sealed class CirclePeer : MonoBehaviour
    {
        /// <summary>Where the other player sits, in the camp's space (the fire is at the origin, you sit at <see cref="CampWorld.StorySeat"/>).</summary>
        public static readonly Vector3 Spot = new Vector3(-1.9f, 0f, 1.1f);
        /// <summary>One colour per seat.</summary>
        public static readonly Color[] SeatColours = { new Color(0.22f, 0.42f, 0.66f), new Color(0.5f, 0.3f, 0.62f) };

        const float SeatTop = 0.36f;
        static readonly Vector3 HeadRest = new Vector3(0f, SeatTop + 0.78f, 0f);

        public readonly PoseTrack Track = new PoseTrack();
        public Transform Head { get; private set; }
        Transform _left, _right, _tag;
        TextMeshPro _name;
        Material _cloth;
        Color _colour;
        bool _away;

        public static CirclePeer Create(Transform world, Material lit, Color colour, string name)
        {
            var go = new GameObject("Circle Player");
            go.transform.SetParent(world, false);
            go.transform.localPosition = Spot;
            var toFire = -new Vector3(Spot.x, 0f, Spot.z);
            go.transform.localRotation = Quaternion.LookRotation(toFire, Vector3.up);
            var peer = go.AddComponent<CirclePeer>();
            peer.Build(lit, colour, name);
            return peer;
        }

        void Build(Material lit, Color colour, string playerName)
        {
            _colour = colour;
            var cloth = _cloth = new Material(lit) { color = colour };
            var skin = new Material(lit) { color = new Color(0.45f, 0.3f, 0.22f) };
            var wood = new Material(lit) { color = new Color(0.23f, 0.16f, 0.11f) };
            GameObject Part(PrimitiveType type, string n, Transform parent, Vector3 pos, Vector3 scale, Material mat, Quaternion? rot = null)
            {
                var g = GameObject.CreatePrimitive(type);
                g.name = n;
                Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);
                g.transform.localPosition = pos; g.transform.localScale = scale;
                if (rot.HasValue) g.transform.localRotation = rot.Value;
                g.GetComponent<Renderer>().sharedMaterial = mat;
                return g;
            }
            // the same stand-in body as the storyteller's (Storyteller.BuildFigure), in this player's colour
            Part(PrimitiveType.Cylinder, "Log", transform, new Vector3(0, SeatTop / 2f, -0.05f), new Vector3(SeatTop, 0.85f, SeatTop), wood, Quaternion.Euler(0, 0, 90));
            Part(PrimitiveType.Capsule, "Body", transform, new Vector3(0, SeatTop + 0.33f, 0), new Vector3(0.38f, 0.33f, 0.26f), cloth);
            Part(PrimitiveType.Cube, "Thighs", transform, new Vector3(0, SeatTop + 0.06f, 0.2f), new Vector3(0.34f, 0.13f, 0.42f), cloth);
            Part(PrimitiveType.Cube, "Shins", transform, new Vector3(0, SeatTop / 2f, 0.38f), new Vector3(0.3f, SeatTop, 0.12f), cloth);
            Head = new GameObject("Head").transform;
            Head.SetParent(transform, false);
            Head.localPosition = HeadRest;
            Part(PrimitiveType.Sphere, "Skull", Head, Vector3.zero, new Vector3(0.2f, 0.24f, 0.21f), skin);
            Part(PrimitiveType.Cube, "Nose", Head, new Vector3(0f, 0f, 0.11f), new Vector3(0.03f, 0.04f, 0.03f), skin); // shows which way they look
            _left = Part(PrimitiveType.Sphere, "Left Hand", transform, Vector3.zero, Vector3.one * 0.08f, skin).transform;
            _right = Part(PrimitiveType.Sphere, "Right Hand", transform, Vector3.zero, Vector3.one * 0.08f, skin).transform;
            _left.gameObject.SetActive(false); _right.gameObject.SetActive(false);

            _tag = new GameObject("Name Tag").transform;
            _tag.SetParent(transform, false);
            _tag.localPosition = HeadRest + Vector3.up * 0.28f;
            _name = Kit.Text(_tag, "", 0.07f, Vector3.zero, new Vector2(1.4f, 0.12f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.UiBold);
            SetPlayer(playerName, false);
        }

        /// <summary>The name on the tag, and whether the player has dropped out (the figure greys while they are away).</summary>
        public void SetPlayer(string playerName, bool away)
        {
            _away = away;
            _name.text = string.IsNullOrEmpty(playerName) ? "Friend" : playerName;
            if (away) _name.text += "  (away)";
            _cloth.color = away ? Color.Lerp(_colour, Color.gray, 0.7f) : _colour;
        }

        /// <summary>Plays the pose back at <paramref name="serverNowMs"/> and turns the name tag to <paramref name="viewer"/>.</summary>
        public void Tick(long serverNowMs, Transform viewer)
        {
            if (!_away && Track.Sample(serverNowMs, out var head, out var left, out var right) && head.Tracked)
            {
                // a head that nods and turns, kept within what a neck does
                var e = head.Rotation.eulerAngles;
                float Wrap(float a) => a > 180f ? a - 360f : a;
                Head.localRotation = Quaternion.Euler(Mathf.Clamp(Wrap(e.x), -50f, 50f), Mathf.Clamp(Wrap(e.y), -80f, 80f), Mathf.Clamp(Wrap(e.z), -30f, 30f));
                Hand(_left, left, head);
                Hand(_right, right, head);
            }
            if (viewer != null) StoryUi.Face(_tag, viewer.position);
        }

        void Hand(Transform hand, PosePart part, PosePart head)
        {
            hand.gameObject.SetActive(part.Tracked);
            if (!part.Tracked) return;
            // where the hand is from their head, placed from this figure's head (bodies at a fire are about the same size)
            var offset = part.Position - head.Position;
            if (offset.sqrMagnitude > 1.2f * 1.2f) offset = offset.normalized * 1.2f;
            hand.localPosition = HeadRest + offset;
        }
    }
}
