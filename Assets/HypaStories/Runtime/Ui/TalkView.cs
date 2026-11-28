using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The panel while you talk with the old woman (G10): who answers (the route chip, "Hypa · Yorùbá"), a ring that pulses
    /// while she listens and swells with your voice, the last few lines of both sides as bubbles (accessibility: in the language
    /// spoken), End, and her offer to walk you to the fire.
    /// </summary>
    public sealed class TalkView
    {
        const float W = 0.6f, H = 0.44f, BubbleW = 0.44f;
        const int MaxBubbles = 4;

        public readonly Transform Root;
        public readonly FollowView Follow;
        public readonly KitGroup Fade;
        readonly TextMeshPro _chip, _state;
        readonly Transform _pulse;
        readonly KitPanel _ring, _ringCore;
        readonly KitIcon _ringIcon;
        readonly Transform _bubbles;
        readonly KitButton _offer, _done;
        readonly List<(TalkLine line, KitPanel bg, TextMeshPro text)> _shown = new List<(TalkLine, KitPanel, TextMeshPro)>();
        Action _offerPress;
        TalkState _talk = TalkState.Off;
        float _t;
        bool _micOff;

        public TalkView(Transform uiRoot, Action end, Action done)
        {
            Root = new GameObject("Talk").transform;
            Root.SetParent(uiRoot, false);
            Follow = Root.gameObject.AddComponent<FollowView>();
            // lower than her face, as the hut's story panel: she stays in view over its top edge
            Follow.Distance = 0.9f; Follow.Height = -0.55f; Follow.Threshold = 30f;
            Kit.Glass(Root, "Panel", new Vector3(0f, 0f, 0.006f), new Vector2(W, H), 0.04f);

            var kicker = Kit.Text(Root, "IN THE HUT", 0.0115f, new Vector3(-W / 2f + 0.16f, H / 2f - 0.036f, 0f), new Vector2(0.26f, 0.02f), KitTheme.Ember, TextAlignmentOptions.Left, KitFont.UiBold);
            kicker.characterSpacing = 14f;
            Kit.Text(Root, "Talking with her", 0.024f, new Vector3(-W / 2f + 0.16f, H / 2f - 0.064f, 0f), new Vector2(0.26f, 0.035f), KitTheme.Paper, TextAlignmentOptions.Left, KitFont.Serif, 12, false);
            _chip = Kit.Text(Root, "", 0.0115f, new Vector3(W / 2f - 0.16f, H / 2f - 0.04f, 0f), new Vector2(0.24f, 0.02f), KitTheme.Dim, TextAlignmentOptions.Right, KitFont.UiBold);
            Kit.Button(Root, new Vector3(W / 2f - 0.075f, H / 2f - 0.072f, 0f), new Vector2(0.1f, 0.042f), "End", "x", KitStyle.Ghost, end, null, 0.016f);
            Kit.Panel(Root, "Rule", new Vector3(0f, H / 2f - 0.105f, 0.002f), new Vector2(W - 0.08f, 0.0012f), 0.0006f,
                KitTheme.Hairline, KitTheme.Hairline, KitTheme.Clear, 11, 0f);

            _bubbles = new GameObject("Bubbles").transform;
            _bubbles.SetParent(Root, false);

            // the listening ring: pulses while she waits, swells as you speak, still while she talks. It pulses through a parent:
            // a panel's own scale is its size
            var ringAt = new Vector3(-W / 2f + 0.07f, -H / 2f + 0.06f, 0f);
            _pulse = new GameObject("Ring Pulse").transform;
            _pulse.SetParent(Root, false);
            _pulse.localPosition = ringAt + new Vector3(0f, 0f, 0.001f);
            _ring = Kit.Panel(_pulse, "Ring", Vector3.zero, new Vector2(0.07f, 0.07f), 0.035f,
                new Color(1f, 0.66f, 0.32f, 0.25f), new Color(0.86f, 0.42f, 0.16f, 0.2f), KitTheme.Ember, 11, 0f);
            _ringCore = Kit.Panel(Root, "Ring Core", ringAt, new Vector2(0.05f, 0.05f), 0.025f, KitTheme.Ember, KitTheme.EmberDeep, KitTheme.Clear, 12, 0f);
            _ringIcon = Kit.Icon(Root, "mic", ringAt + new Vector3(0f, 0f, -0.002f), 0.024f, KitTheme.Ink, 13);
            _state = Kit.Text(Root, "", 0.015f, new Vector3(0f, -H / 2f + 0.06f, 0f), new Vector2(0.38f, 0.04f), KitTheme.Dim, TextAlignmentOptions.Left, KitFont.UiBold);
            // you are done talking: she answers now, without waiting for the quiet
            _done = Kit.Button(Root, new Vector3(W / 2f - 0.09f, -H / 2f + 0.06f, 0f), new Vector2(0.13f, 0.048f), "Done", "check", KitStyle.Primary, done, null, 0.016f);
            _done.gameObject.SetActive(false);
            _offer = Kit.Button(Root, new Vector3(0f, -H / 2f + 0.125f, -0.006f), new Vector2(0.42f, 0.046f), "Walk to the fire", "flame", KitStyle.Primary, () => _offerPress?.Invoke(), null, 0.015f);
            _offer.gameObject.SetActive(false);

            Fade = Root.gameObject.AddComponent<KitGroup>();
            Fade.Refresh();
        }

        /// <summary>A new conversation: no bubbles, no offer.</summary>
        public void Reset()
        {
            foreach (var s in _shown) UnityEngine.Object.Destroy(s.bg.gameObject.transform.parent.gameObject);
            _shown.Clear();
            Offer(null, null);
            _micOff = false;
            SetState(TalkState.Connecting);
            SetChip("");
        }

        public void SetChip(string chip) => _chip.text = chip ?? "";

        /// <summary>Whether Done can end the player's turn (not in the browser's Live session).</summary>
        public bool CanEndTurn = true;

        public void SetState(TalkState s)
        {
            _talk = s;
            _done.gameObject.SetActive(s == TalkState.Hearing && CanEndTurn);
            _state.text = s == TalkState.Connecting ? "Connecting…" : s == TalkState.Listening ? (_micOff ? "She can’t hear you: no microphone" : "She’s listening") : s == TalkState.Hearing ? "Listening to you…"
                : s == TalkState.Thinking ? "Thinking…" : s == TalkState.Speaking ? "She’s speaking" : s == TalkState.Held ? "Telling a story…" : s == TalkState.Ended ? "Goodbye" : "";
        }

        public void SetNotice(string text)
        {
            if (!string.IsNullOrEmpty(text)) _state.text = text;
        }

        /// <summary>Her offer to walk you to the fire for a story (null hides it).</summary>
        public void Offer(string title, Action go)
        {
            _offerPress = go;
            var on = go != null;
            _offer.gameObject.SetActive(on);
            if (on) _offer.SetLabel("Walk to the fire · " + title);
            Layout();
        }

        /// <summary>A bubble appeared or grew.</summary>
        public void ShowLine(TalkLine line)
        {
            if (line == null) return;
            var i = _shown.FindIndex(s => s.line == line);
            if (i < 0)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) return; // shown once there are words
                var node = new GameObject("Bubble " + line.Id).transform;
                node.SetParent(_bubbles, false);
                var bg = Kit.Panel(node, "bg", new Vector3(0f, 0f, 0.003f), new Vector2(0.1f, 0.04f), 0.018f,
                    line.Hers ? new Color(1f, 0.66f, 0.32f, 0.16f) : KitTheme.GhostTop, line.Hers ? new Color(0.86f, 0.42f, 0.16f, 0.12f) : KitTheme.GhostBottom, KitTheme.Hairline, 11, 0f);
                var text = Kit.Text(node, "", 0.0165f, Vector3.zero, new Vector2(BubbleW - 0.03f, 0.1f), line.Hers ? KitTheme.Paper : KitTheme.Dim,
                    TextAlignmentOptions.Left, line.Hers ? KitFont.Serif : KitFont.Ui);
                text.richText = false;
                _shown.Add((line, bg, text));
                while (_shown.Count > MaxBubbles)
                {
                    UnityEngine.Object.Destroy(_shown[0].bg.transform.parent.gameObject);
                    _shown.RemoveAt(0);
                }
                i = _shown.Count - 1;
            }
            _shown[i].text.text = line.Text;
            Layout();
        }

        /// <summary>Newest at the bottom, just above the ring; hers on the left, yours on the right.</summary>
        void Layout()
        {
            var y = -H / 2f + (_offer.gameObject.activeSelf ? 0.165f : 0.115f);
            for (var i = _shown.Count - 1; i >= 0; i--)
            {
                var (line, bg, text) = _shown[i];
                var size = text.GetPreferredValues(text.text, BubbleW - 0.03f, 0f);
                var w = Mathf.Clamp(size.x + 0.03f, 0.08f, BubbleW);
                var h = Mathf.Max(0.036f, size.y + 0.018f);
                if (y + h > H / 2f - 0.115f) { bg.transform.parent.gameObject.SetActive(false); continue; } // no room: the oldest go first
                bg.transform.parent.gameObject.SetActive(true);
                var x = line.Hers ? -W / 2f + 0.04f + w / 2f : W / 2f - 0.04f - w / 2f;
                bg.transform.parent.localPosition = new Vector3(x, y + h / 2f, 0f);
                bg.Resize(new Vector2(w, h));
                text.rectTransform.sizeDelta = new Vector2(w - 0.03f, h);
                y += h + 0.012f;
            }
        }

        /// <summary>No microphone (refused, missing, or still being allowed): the state says so instead of "She’s listening".</summary>
        public void SetMicOff(bool off)
        {
            if (_micOff == off) return;
            _micOff = off;
            if (_talk == TalkState.Listening) SetState(_talk);
        }

        public void Update(float dt, float micLevel, float voiceLevel)
        {
            _t += dt;
            // the ring
            var listening = _talk == TalkState.Listening || _talk == TalkState.Hearing;
            var pulse = listening ? 1f + 0.08f * Mathf.Sin(_t * 3.2f) + micLevel * 0.45f : _talk == TalkState.Speaking ? 1f + voiceLevel * 0.15f : 0.92f;
            _pulse.localScale = new Vector3(pulse, pulse, 1f);
            _ring.Alpha = listening && !_micOff ? 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(_t * 1.6f)) : 0.35f;
            _ringCore.Alpha = listening && !_micOff ? 1f : 0.45f;
            _ringIcon.Alpha = listening && !_micOff ? 1f : 0.5f;
        }
    }
}
