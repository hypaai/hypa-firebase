#if HYPA_FLAT
using HypaCore;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// Phone and browser builds (no headset): walk with WASD or the arrow keys, or the on-screen stick (a thumb on the left of the
    /// screen); look around by dragging anywhere else, or with the right mouse button. Walking feeds <see cref="StoryInput.Move"/>
    /// like the Quest thumbstick, so the camp and the places move the same way. Looking turns the camera itself. A short tap or a
    /// press that starts on a panel stays a tap or a pinch, never a look.
    /// </summary>
    public sealed class ScreenControls : MonoBehaviour
    {
        const float LookDegreesPerPixel = 0.18f, DragStartPixels = 12f, MaxPitch = 70f;
        const float StickZone = 0.4f;      // left share of the screen (lower half) where a thumb grabs the stick
        const float StickRadiusInches = 0.45f;

        Transform _cam;
        float _yaw, _pitch;
        Vector2 _stick;
        int _stickFinger = -1, _lookFinger = -1;
        Vector2 _stickOrigin, _pressAt, _last;
        bool _looking, _blocked;
        Texture2D _ring;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            var go = new GameObject(nameof(ScreenControls));
            DontDestroyOnLoad(go);
            var controls = go.AddComponent<ScreenControls>();
            StoryInput.Move = () => Vector2.ClampMagnitude(controls._stick + Keys(), 1f);
        }

        static Vector2 Keys()
        {
            var v = Vector2.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v.y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v.y -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) v.x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) v.x -= 1f;
            return v;
        }

        float StickRadius => (Screen.dpi > 0 ? Screen.dpi : 160f) * StickRadiusInches;

        void Update()
        {
            var cam = Camera.main != null ? Camera.main.transform : null;
            if (cam == null) return;
            if (cam != _cam)
            {
                _cam = cam;
                var e = cam.eulerAngles;
                _yaw = e.y; _pitch = e.x > 180f ? e.x - 360f : e.x;
            }
            if (Input.touchCount > 0) Touches(); else Mouse();
            _cam.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        void Touches()
        {
            for (var i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                switch (t.phase)
                {
                    case TouchPhase.Began:
                        if (_stickFinger < 0 && t.position.x < Screen.width * StickZone && t.position.y < Screen.height * 0.5f)
                        { _stickFinger = t.fingerId; _stickOrigin = t.position; }
                        else if (_lookFinger < 0) { _lookFinger = t.fingerId; Press(t.position); }
                        break;
                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:
                        if (t.fingerId == _stickFinger) _stick = Vector2.ClampMagnitude((t.position - _stickOrigin) / StickRadius, 1f);
                        else if (t.fingerId == _lookFinger) Drag(t.position, false);
                        break;
                    default: // ended or cancelled
                        if (t.fingerId == _stickFinger) { _stickFinger = -1; _stick = Vector2.zero; }
                        if (t.fingerId == _lookFinger) _lookFinger = -1;
                        break;
                }
            }
        }

        void Mouse()
        {
            _stickFinger = _lookFinger = -1;
            _stick = Vector2.zero;
            var pos = (Vector2)Input.mousePosition;
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) Press(pos);
            if (Input.GetMouseButton(1)) Drag(pos, true);
            else if (Input.GetMouseButton(0)) Drag(pos, false);
        }

        void Press(Vector2 at)
        {
            _pressAt = _last = at;
            _looking = false;
            var pointer = PinchPointer.Instance;
            _blocked = pointer != null && pointer.Hover != null; // a press on a panel or button is a pinch, not a look
        }

        void Drag(Vector2 at, bool always)
        {
            if (!_looking && (always || (!_blocked && (at - _pressAt).magnitude > DragStartPixels))) { _looking = true; _last = at; }
            if (!_looking) return;
            var d = at - _last;
            _last = at;
            _yaw += d.x * LookDegreesPerPixel;
            _pitch = Mathf.Clamp(_pitch - d.y * LookDegreesPerPixel, -MaxPitch, MaxPitch);
        }

        void OnGUI()
        {
            if (_stickFinger < 0) return;
            if (_ring == null) _ring = Ring(64);
            var r = StickRadius;
            Circle(_stickOrigin, r, new Color(1f, 1f, 1f, 0.18f));
            Circle(_stickOrigin + _stick * r, r * 0.42f, new Color(1f, 1f, 1f, 0.45f));
        }

        void Circle(Vector2 screenPos, float radius, Color color)
        {
            var c = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(screenPos.x - radius, Screen.height - screenPos.y - radius, radius * 2f, radius * 2f), _ring);
            GUI.color = c;
        }

        /// <summary>A soft white disc, drawn tinted for the stick's base and knob.</summary>
        static Texture2D Ring(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var half = size * 0.5f;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var d = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / half;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) * 6f)));
                }
            tex.Apply();
            return tex;
        }
    }
}
#endif
