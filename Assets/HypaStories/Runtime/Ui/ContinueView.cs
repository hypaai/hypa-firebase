using System;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// "Continue <i>Seven Hot Stones</i> where you left off on your phone?" (HYP-203 item 3): the question, Continue, and Not now.
    /// Shown in front of the player at the campfire, like the Story Circle card.
    /// </summary>
    public sealed class ContinueView
    {
        public readonly Transform Root;
        readonly KitGroup _group;
        readonly FollowView _follow;
        readonly TextMeshPro _question;

        public bool Shown => Root.gameObject.activeSelf;

        public ContinueView(Transform uiRoot, Action yes, Action no)
        {
            Root = new GameObject("Continue").transform;
            Root.SetParent(uiRoot, false);
            Kit.Glass(Root, "Card", Vector3.zero, new Vector2(0.46f, 0.24f));
            _question = Kit.Text(Root, "", 0.022f, new Vector3(0f, 0.045f, 0f), new Vector2(0.4f, 0.1f), KitTheme.Paper,
                TextAlignmentOptions.Center, KitFont.Serif);
            Kit.Button(Root, new Vector3(-0.08f, -0.06f, 0f), new Vector2(0.15f, 0.05f), "Continue", "check", KitStyle.Primary, () => yes());
            Kit.Button(Root, new Vector3(0.08f, -0.06f, 0f), new Vector2(0.15f, 0.05f), "Not now", null, KitStyle.Ghost, () => no());
            _follow = Root.gameObject.AddComponent<FollowView>();
            _follow.Distance = 0.95f; _follow.Height = 0.0f; // where the Story Circle card sits
            _group = Root.gameObject.AddComponent<KitGroup>();
            _group.Refresh();
            Root.gameObject.SetActive(false);
        }

        public void Show(string question)
        {
            _question.text = question;
            Root.gameObject.SetActive(true);
            _follow.Snap();
            _group.Refresh();
            _group.Set(1f);
        }

        public void Hide() => Root.gameObject.SetActive(false);
    }
}
