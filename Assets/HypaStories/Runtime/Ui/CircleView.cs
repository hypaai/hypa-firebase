using System;
using HypaCore.Hybrid;
using TMPro;
using UnityEngine;

namespace HypaStories
{
    /// <summary>
    /// The Story Circle lobby (G09): start a circle, or join one by its 5-letter code. The code is typed on its own letter pad (the
    /// 20 letters codes use), so it works the same with a pinch on Quest, a tap on a phone and a click on the web, with no keyboard.
    /// </summary>
    public sealed class CircleView
    {
        public readonly Transform Root;
        public readonly KitGroup Group;
        public readonly FollowView Follow;
        readonly Transform _menu, _pad;
        readonly TextMeshPro _status, _code;
        readonly Action _create, _close;
        readonly Action<string> _join;
        string _typed = "";

        public bool Shown => Root.gameObject.activeSelf;

        public CircleView(Transform uiRoot, Action create, Action<string> join, Action close)
        {
            _create = create; _join = join; _close = close;
            Root = new GameObject("Story Circle").transform;
            Root.SetParent(uiRoot, false);
            Kit.Glass(Root, "Card", Vector3.zero, new Vector2(0.46f, 0.42f));
            Kit.Text(Root, "Story Circle", 0.026f, new Vector3(0f, 0.175f, 0f), new Vector2(0.36f, 0.04f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.SerifItalic);
            Kit.Button(Root, new Vector3(0.195f, 0.175f, 0f), new Vector2(0.04f, 0.04f), null, "x", KitStyle.Quiet, () => _close());
            _status = Kit.Text(Root, "", 0.015f, new Vector3(0f, -0.188f, 0f), new Vector2(0.42f, 0.03f), KitTheme.Dim);

            _menu = new GameObject("Menu").transform;
            _menu.SetParent(Root, false);
            Kit.Text(_menu, "Two people at one fire hear the same story at the same moment, and either of you can raise a hand to ask.",
                0.016f, new Vector3(0f, 0.095f, 0f), new Vector2(0.38f, 0.07f), KitTheme.Dim);
            Kit.Button(_menu, new Vector3(0f, 0.005f, 0f), new Vector2(0.3f, 0.06f), "Start a circle", "flame", KitStyle.Primary, () => _create());
            Kit.Button(_menu, new Vector3(0f, -0.075f, 0f), new Vector2(0.3f, 0.06f), "Join with a code", "users", KitStyle.Ghost, () => ShowPad());

            _pad = new GameObject("Code Pad").transform;
            _pad.SetParent(Root, false);
            _code = Kit.Text(_pad, "", 0.034f, new Vector3(0f, 0.125f, 0f), new Vector2(0.4f, 0.05f), KitTheme.Paper, TextAlignmentOptions.Center, KitFont.UiBold);
            const float gx = 0.072f, gy = 0.05f;
            for (var i = 0; i < RoomCode.Alphabet.Length; i++)
            {
                var letter = RoomCode.Alphabet[i];
                var row = i / 5; var col = i % 5;
                Kit.Button(_pad, new Vector3((col - 2) * gx, 0.072f - row * gy, 0f), new Vector2(0.064f, 0.044f), letter.ToString(), null, KitStyle.Ghost, () => Type(letter), em: 0.02f);
            }
            Kit.Button(_pad, new Vector3(-0.135f, -0.14f, 0f), new Vector2(0.11f, 0.044f), "Back", "arrow-left", KitStyle.Quiet, ShowMenu, em: 0.016f);
            Kit.Button(_pad, new Vector3(0f, -0.14f, 0f), new Vector2(0.11f, 0.044f), "Delete", null, KitStyle.Ghost, Delete, em: 0.016f);
            Kit.Button(_pad, new Vector3(0.135f, -0.14f, 0f), new Vector2(0.11f, 0.044f), "Join", "check", KitStyle.Primary, Submit, em: 0.016f);

            Follow = Root.gameObject.AddComponent<FollowView>();
            // above the campfire's row (SplashView: 0.95 m away, 0.34 m below eye level, its "Pinch the fire, or" line just over it):
            // the same distance, raised so the card's bottom edge clears that line and the row stays visible underneath
            Follow.Distance = 0.95f; Follow.Height = 0.0f;
            Group = Root.gameObject.AddComponent<KitGroup>();
            Group.Refresh();
            ShowMenu();
            Root.gameObject.SetActive(false);
        }

        public void Show(bool on)
        {
            Root.gameObject.SetActive(on);
            if (!on) return;
            Follow.Snap();
            Group.Refresh();
            Group.Set(1f);
        }

        public void ShowMenu()
        {
            _menu.gameObject.SetActive(true);
            _pad.gameObject.SetActive(false);
            SetStatus("");
        }

        /// <summary>The letter pad, with <paramref name="code"/> already typed (from a share link).</summary>
        public void ShowPad(string code = "")
        {
            _menu.gameObject.SetActive(false);
            _pad.gameObject.SetActive(true);
            _typed = (code ?? "").ToUpperInvariant();
            if (_typed.Length > RoomCode.Length) _typed = _typed.Substring(0, RoomCode.Length);
            ShowTyped();
            SetStatus("");
        }

        public void SetStatus(string text) => _status.text = text ?? "";

        void Type(char letter)
        {
            if (_typed.Length >= RoomCode.Length) return;
            _typed += letter;
            ShowTyped();
        }

        void Delete()
        {
            if (_typed.Length == 0) return;
            _typed = _typed.Substring(0, _typed.Length - 1);
            ShowTyped();
        }

        void Submit()
        {
            var code = RoomCode.Normalize(_typed);
            if (code == null) { SetStatus("A code is 5 letters"); return; }
            _join(code);
        }

        void ShowTyped()
        {
            var shown = new char[RoomCode.Length * 2 - 1];
            for (var i = 0; i < RoomCode.Length; i++)
            {
                shown[i * 2] = i < _typed.Length ? _typed[i] : '_';
                if (i < RoomCode.Length - 1) shown[i * 2 + 1] = ' ';
            }
            _code.text = new string(shown);
        }
    }
}
