using System;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The race's screens (Story Circle, G11): the 3-2-1 count, a small live bar ("Ada 12 · You 9") above the game, and the result
    /// card ("Ada wins: 41 s") with Rematch and Back to the fire. Each part follows the view.
    /// </summary>
    public sealed class RaceView
    {
        readonly Transform _count, _bar, _card;
        readonly TextMeshPro _countText, _barText, _title, _sub;
        readonly KitGroup _countGroup, _barGroup, _cardGroup;
        readonly FollowView _cardFollow, _countFollow;

        public RaceView(Transform uiRoot, Action rematch, Action back)
        {
            _count = new GameObject("Race Count").transform;
            _count.SetParent(uiRoot, false);
            _countText = Kit.Text(_count, "", 0.16f, Vector3.zero, new Vector2(0.6f, 0.25f), KitTheme.Ember, TextAlignmentOptions.Center, KitFont.UiBold);
            _countFollow = _count.gameObject.AddComponent<FollowView>();
            _countFollow.Distance = 1.3f; _countFollow.Height = 0.05f;
            _countGroup = _count.gameObject.AddComponent<KitGroup>();
            _countGroup.Refresh();

            _bar = new GameObject("Race Bar").transform;
            _bar.SetParent(uiRoot, false);
            Kit.Glass(_bar, "Pill", Vector3.zero, new Vector2(0.34f, 0.045f), 0.0225f);
            _barText = Kit.Text(_bar, "", 0.018f, Vector3.zero, new Vector2(0.32f, 0.04f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.UiBold);
            var bf = _bar.gameObject.AddComponent<FollowView>();
            bf.Distance = 1.0f; bf.Height = 0.24f;
            _barGroup = _bar.gameObject.AddComponent<KitGroup>();
            _barGroup.Refresh();

            _card = new GameObject("Race Result").transform;
            _card.SetParent(uiRoot, false);
            Kit.Glass(_card, "Card", Vector3.zero, new Vector2(0.42f, 0.22f));
            _title = Kit.Text(_card, "", 0.03f, new Vector3(0f, 0.06f, 0f), new Vector2(0.38f, 0.05f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.SerifItalic);
            _sub = Kit.Text(_card, "", 0.016f, new Vector3(0f, 0.012f, 0f), new Vector2(0.38f, 0.03f), KitTheme.Dim);
            Kit.Button(_card, new Vector3(-0.095f, -0.055f, 0f), new Vector2(0.17f, 0.052f), "Rematch", "rotate-ccw", KitStyle.Primary, rematch, em: 0.018f);
            Kit.Button(_card, new Vector3(0.095f, -0.055f, 0f), new Vector2(0.17f, 0.052f), "Back to the fire", "flame", KitStyle.Ghost, back, em: 0.016f);
            _cardFollow = _card.gameObject.AddComponent<FollowView>();
            _cardFollow.Distance = 0.9f; _cardFollow.Height = -0.05f;
            _cardGroup = _card.gameObject.AddComponent<KitGroup>();
            _cardGroup.Refresh();

            Hide();
        }

        /// <summary>"3", "2", "1", "Go!", or null to hide.</summary>
        public void SetCount(string text)
        {
            var on = !string.IsNullOrEmpty(text);
            if (on && !_count.gameObject.activeSelf) { _count.gameObject.SetActive(true); _countFollow.Snap(); _countGroup.Refresh(); _countGroup.Set(1f); }
            if (!on) _count.gameObject.SetActive(false);
            _countText.text = text ?? "";
        }

        /// <summary>The live bar's line, or null to hide it.</summary>
        public void SetBar(string text)
        {
            var on = !string.IsNullOrEmpty(text);
            if (on && !_bar.gameObject.activeSelf) { _bar.gameObject.SetActive(true); _barGroup.Refresh(); _barGroup.Set(1f); }
            if (!on) _bar.gameObject.SetActive(false);
            _barText.text = text ?? "";
        }

        public void ShowResult(string title, string sub)
        {
            _title.text = title;
            _sub.text = sub ?? "";
            _card.gameObject.SetActive(true);
            _cardFollow.Snap();
            _cardGroup.Refresh();
            _cardGroup.Set(1f);
        }

        public void HideResult() => _card.gameObject.SetActive(false);

        public void Hide()
        {
            SetCount(null);
            SetBar(null);
            HideResult();
        }
    }
}
