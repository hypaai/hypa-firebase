using System.Linq;
using NUnit.Framework;
using HypaCore;
using UnityEditor;
using UnityEngine;

namespace HypaStories.Tests
{
    public sealed class SpiritBoxingTests
    {
        static void Until(SpiritBoxingRules rules, SpiritFightPhase phase, bool defend = true)
        {
            for (var i = 0; i < 2000 && rules.Phase != phase; i++) rules.Tick(0.05f, defend, false);
            Assert.AreEqual(phase, rules.Phase);
        }

        /// <summary>Punches the current spirit down: every counter window, as many punches as it allows.</summary>
        static void Clear(SpiritBoxingRules rules)
        {
            while (rules.Phase != SpiritFightPhase.LevelComplete && rules.Phase != SpiritFightPhase.Won)
            {
                Until(rules, SpiritFightPhase.Counter);
                while (rules.Phase == SpiritFightPhase.Counter) rules.Punch();
            }
        }

        [Test] public void AWrongHeadDuringYourTurnStrikesYouInstead()
        {
            var rules = new SpiritBoxingRules();
            rules.Start();
            Until(rules, SpiritFightPhase.Counter);
            Assert.IsTrue(rules.Suffer());
            Assert.AreEqual(SpiritBoxingRules.MaxGuard - 1, rules.Guard);
            Assert.AreEqual(0, rules.Hits);
            Assert.AreEqual(SpiritFightPhase.Counter, rules.Phase);
            for (var i = 0; i < SpiritBoxingRules.MaxGuard; i++) rules.Suffer();
            Assert.AreEqual(SpiritFightPhase.Lost, rules.Phase);
            Assert.IsFalse(rules.Suffer());
        }

        [Test] public void EveryPunchMarksAnotherStandingHead_AndAHeadGoesDownAfterItsShare()
        {
            var rules = new SpiritBoxingRules(3);
            rules.Start();
            Clear(rules); Until(rules, SpiritFightPhase.Approach);          // the one-headed spirit
            Clear(rules); Until(rules, SpiritFightPhase.Approach);          // two heads
            Assert.AreEqual(3, rules.Heads);
            Assert.AreEqual(3, rules.HitsPerHead);
            var hits = new int[rules.Heads];
            while (rules.Phase != SpiritFightPhase.LevelComplete)
            {
                Until(rules, SpiritFightPhase.Counter);
                while (rules.Phase == SpiritFightPhase.Counter)
                {
                    var mark = rules.Mark;
                    Assert.IsTrue(rules.Standing(mark), "only a standing head is marked");
                    var standing = 0;
                    for (var h = 0; h < rules.Heads; h++) if (rules.Standing(h)) standing++;
                    Assert.IsTrue(rules.Punch());
                    hits[mark]++;
                    Assert.AreEqual(hits[mark] < rules.HitsPerHead, rules.Standing(mark), "a head goes down after its share, not before");
                    if (rules.Phase == SpiritFightPhase.Counter && standing > 1 && rules.Standing(mark))
                        Assert.AreNotEqual(mark, rules.Mark, "another head after each punch while there is one");
                }
            }
            CollectionAssert.AreEqual(new[] { 3, 3, 3 }, hits);
            Assert.AreEqual(9, rules.Hits);
        }

        [Test] public void FourSpirits_EndInAFourteenHitFinal_AndNeverAcceptExtraPunches()
        {
            var rules = new SpiritBoxingRules();
            Assert.IsFalse(rules.Punch());
            for (var level = 0; level < SpiritBoxingRules.LevelCount; level++)
            {
                Assert.AreEqual(level, rules.Level);
                Assert.AreEqual(new[] { 8, 8, 9, 14 }[level], rules.NeededHits);
                Assert.AreEqual(new[] { 3, 4, 4, 5 }[level], rules.Combo, "one punch more than the spirit throws");
                if (level == 0) { Assert.AreEqual(SpiritFightPhase.Ready, rules.Phase); rules.Start(); }
                else Assert.AreEqual(SpiritFightPhase.Approach, rules.Phase, "the next spirit comes in without a button");
                while (rules.Hits < rules.NeededHits)
                {
                    Until(rules, SpiritFightPhase.Counter);
                    var landed = 0;
                    while (rules.Punch()) landed++;
                    Assert.That(landed, Is.InRange(1, rules.Combo), "no punch lands past the combo, a downed head or the last target");
                }
                Assert.AreEqual(SpiritBoxingRules.MaxGuard, rules.Guard);
                Assert.AreEqual(level == 3 ? SpiritFightPhase.Won : SpiritFightPhase.LevelComplete, rules.Phase);
                if (level < 3) Until(rules, SpiritFightPhase.Approach);
            }
            for (var i = 0; i < 200; i++) rules.Tick(0.05f, true, false);
            Assert.AreEqual(3, rules.Level);
            Assert.AreEqual(SpiritFightPhase.Won, rules.Phase, "the final spirit waits for Play again");
        }

        [Test] public void LaterSpiritsShuffleTheirPattern_AndTellFasterEachRound()
        {
            var rules = new SpiritBoxingRules(7); rules.Start();
            Clear(rules);
            Until(rules, SpiritFightPhase.Approach);
            var first = rules.TellDuration;
            var seen = new System.Collections.Generic.HashSet<string>();
            for (var round = 0; round < 6; round++)
            {
                var pattern = "";
                for (var beat = 0; beat < 3; beat++) { Until(rules, SpiritFightPhase.Tell); pattern += rules.Attack; Until(rules, SpiritFightPhase.Strike); }
                seen.Add(pattern);
                Until(rules, SpiritFightPhase.Counter);
            }
            Assert.Greater(seen.Count, 1, "the second spirit does not repeat one fixed order");
            Assert.Less(rules.TellDuration, first);
            Assert.GreaterOrEqual(rules.TellDuration, 0.6f);
        }

        [Test] public void MissedDefensesLoseTheRound_AndTheSameSpiritComesBackByItself()
        {
            var rules = new SpiritBoxingRules(); rules.Start();
            Clear(rules);
            Until(rules, SpiritFightPhase.Approach);
            Until(rules, SpiritFightPhase.Lost, false);
            Assert.AreEqual(0, rules.Guard);
            Until(rules, SpiritFightPhase.Approach);
            Assert.AreEqual(1, rules.Level);
            Assert.AreEqual(0, rules.Hits);
            Assert.AreEqual(SpiritBoxingRules.MaxGuard, rules.Guard);
        }

        [Test] public void TrackingAndFocusPauseFreezeTheAttackAndCounterWindows()
        {
            var rules = new SpiritBoxingRules(); rules.Start();
            Until(rules, SpiritFightPhase.Strike);
            var elapsed = rules.Elapsed;
            for (var i = 0; i < 200; i++) rules.Tick(1f, false, true);
            Assert.AreEqual(SpiritFightPhase.Strike, rules.Phase);
            Assert.AreEqual(elapsed, rules.Elapsed);
            Assert.AreEqual(SpiritBoxingRules.MaxGuard, rules.Guard);
            Until(rules, SpiritFightPhase.Counter);
            rules.Tick(10f, false, true);
            Assert.AreEqual(SpiritFightPhase.Counter, rules.Phase);
            Assert.IsTrue(rules.Punch());
        }

        [Test] public void DuckAndSideGuardsRequireTheIndicatedHeadRelativePosition()
        {
            var left = new Vector3(-0.18f, -0.1f, 0.2f);
            var down = new Vector3(0.1f, -0.6f, 0.2f);
            Assert.IsTrue(SpiritBoxingRules.Defending(SpiritAttack.Left, left, down, 0f));
            Assert.IsFalse(SpiritBoxingRules.Defending(SpiritAttack.Right, left, down, 0f));
            Assert.IsFalse(SpiritBoxingRules.Defending(SpiritAttack.Sweep, left, down, 0.05f));
            Assert.IsTrue(SpiritBoxingRules.Defending(SpiritAttack.Sweep, left, down, 0.15f));
            Assert.IsTrue(SpiritBoxingRules.Defending(SpiritAttack.Right, new Vector3(0.18f, -0.1f, 0.2f), down, 0f), "either hand can cover a side");
        }

        [Test] public void SweptPunchesHit_WhileRestingHandsBackwardMotionAndTrackingJumpsDoNot()
        {
            var target = new Vector3(0.1f, -0.22f, 0.53f);
            Assert.IsTrue(SpiritBoxingRules.HitsTarget(target - Vector3.forward * 0.25f, target + Vector3.forward * 0.1f, target, 0.08f));
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(target, target, target, 0.02f));
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(target + Vector3.forward * 0.1f, target - Vector3.forward * 0.25f, target, 0.08f));
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(Vector3.zero, target, target, 0.5f));
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(Vector3.zero, Vector3.forward, target, 0f));
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(target - Vector3.forward, target, target, 0.01f), "tracking teleport");
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(target - Vector3.forward * 0.1f, target, target, 0.1f, headMotion: Vector3.forward * 0.1f), "moving the whole body is not a jab");
            Assert.IsFalse(SpiritBoxingRules.HitsTarget(target, target, target, 0.05f, headMotion: Vector3.back * 0.1f), "moving the head away from a resting hand is not a jab");
        }

        [Test] public void VictoryCrowd_GathersRoundTheArenaCircle()
        {
            var near = Village.Around(new Vector3(0f, 0f, 5.6f), 1.9f, 4.2f);
            var far = Village.Around(new Vector3(0f, 0f, 12.5f), 1.9f, 4.2f);
            var side = Village.Around(new Vector3(6.5f, 0f, 0f), 1.9f, 4.2f);
            Assert.AreEqual(1.9f, near.magnitude, 0.02f, "they dance just outside the stone circle");
            Assert.AreEqual(4.2f, far.magnitude, 0.02f, "they walk in from the spirit trees");
            Assert.Greater(near.z, 0f);
            Assert.That(side.magnitude, Is.GreaterThan(1.9f).And.LessThan(4.2f));
            Assert.AreEqual(0f, near.x, 0.02f);
        }

        [Test] public void Joromi_IsAnElderCard_WithFourMinutesAndCompleteExistingLanguages()
        {
            var story = StoryLibrary.Find(SpiritBoxingGame.StoryId);
            Assert.Contains(story, StoryLibrary.Elder);
            Assert.IsFalse(StoryLibrary.Campfire.Contains(story));
            Assert.AreEqual(44, story.Timeline.Captions.Length);
            Assert.AreEqual(627, story.Timeline.Captions.Sum(c => c.Text.Split(' ').Length));
            Assert.AreEqual(253f, story.Timeline.Duration);
            Assert.That(story.Timeline.Duration, Is.InRange(235f, 270f));
            Assert.AreEqual(story.Timeline.Captions.Length, story.Timeline.Captions.Select(c => c.Id).Distinct().Count());
            foreach (var language in StoryLanguages.All)
            {
                foreach (var line in story.Timeline.Captions)
                {
                    Assert.IsTrue(line.Has(language.Code), language.Code + ": " + line.Id);
                    Assert.AreEqual(line.Ask.Length, line.AskFor(language.Code).Length);
                    if (language.Code != "en" && line.Ask.Length > 0) Assert.IsTrue(line.AskTr.ContainsKey(language.Code));
                }
                Assert.AreEqual(3, story.WordsFor(language.Code).Length);
                foreach (var label in story.GameLabels)
                {
                    if (language.Code != "en") Assert.IsTrue(story.GameLabelsTr[language.Code].ContainsKey(label.Key));
                    var translated = story.GameText(label.Key, language.Code);
                    Assert.IsNotEmpty(translated);
                    Assert.DoesNotThrow(() => string.Format(translated, 1, 4));
                }
            }
        }

        [Test] public void SpiritGame_BuildsLocalizedControlsAndSevenFinalMasks_AndDeactivatesArenaOnExit()
        {
            var root = new GameObject("Spirit game lifecycle test");
            var assets = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<StoryAssets>("Assets/HypaStories/StoryAssets.asset"));
            assets.icons = new Texture2D[0];
            var ambient = RenderSettings.ambientLight;
            var ambientMode = RenderSettings.ambientMode;
            var fog = RenderSettings.fog;
            var otherCameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude)
                .Where(c => c.enabled).ToArray();
            foreach (var other in otherCameras) other.enabled = false;
            try
            {
                Kit.Init(assets);
                var camera = new GameObject("Spirit game verification camera").AddComponent<Camera>();
                camera.transform.SetParent(root.transform); camera.tag = "MainCamera";
                camera.transform.position = new Vector3(0f, 1.3f, 0f);
                camera.nearClipPlane = 0.01f; camera.farClipPlane = 20f;
                camera.fieldOfView = 80f; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.025f, 0.055f, 0.07f);
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                Assert.AreSame(camera, Camera.main);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.white * 0.8f; RenderSettings.fog = false;
                var camp = root.AddComponent<CampWorld>();
                var inRoom = false;
                var game = root.AddComponent<SpiritBoxingGame>();
                game.Init(camp, assets, root.AddComponent<StoryAudio>(), root.transform, () => { }, () => inRoom, () => { });
                var story = StoryLibrary.Find(SpiritBoxingGame.StoryId);
                game.Begin(story, StoryLanguages.Find("fr"));
                var sky = root.transform.Find("Joromi Spirit Arena/Spirit sky").gameObject;
                Assert.IsTrue(sky.activeSelf, "the forest clearing round the fight");
                inRoom = true;
                typeof(SpiritBoxingGame).GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(game, null);
                Assert.IsFalse(sky.activeSelf, "in your room the spirits fight in the real room");
                Assert.IsTrue(root.GetComponentsInChildren<KitButton>(true).Any(b => b.Label != null && b.Label.text == "Back to the forest"));
                inRoom = false;
                typeof(SpiritBoxingGame).GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(game, null);
                Assert.IsTrue(sky.activeSelf);
                var buttons = root.GetComponentsInChildren<KitButton>(true);
                Assert.IsTrue(buttons.Any(b => b.Label != null && b.Label.text == story.GameText("start", "fr")));
                Assert.IsTrue(buttons.Any(b => b.Label != null && b.Label.text == story.GameText("back", "fr")));
                game.Rules.Start();
                for (var level = 0; level < 3; level++)
                {
                    Clear(game.Rules);
                    Until(game.Rules, SpiritFightPhase.Approach);
                }
                typeof(SpiritBoxingGame).GetMethod("BuildSpirit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(game, null);
                Assert.AreEqual(7, root.GetComponentsInChildren<MeshFilter>(true).Count(m => m.name.StartsWith("Spirit mask")));
                Assert.AreEqual(2, root.GetComponentsInChildren<MeshFilter>(true).Count(m => m.name.EndsWith(" attack")), "two floating hands");
                Assert.AreEqual(0, root.GetComponentsInChildren<Transform>(true).Count(t => t.name.EndsWith(" arm")), "no arms");
                Until(game.Rules, SpiritFightPhase.Counter);
                typeof(SpiritBoxingGame).GetMethod("DrawSpirit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(game, null);
                typeof(SpiritBoxingGame).GetMethod("DrawHud", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(game, null);
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    var target = new RenderTexture(1000, 800, 24);
                    var pixels = new Texture2D(1000, 800, TextureFormat.RGB24, false);
                    var previous = RenderTexture.active;
                    try
                    {
                        camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                        pixels.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0); pixels.Apply();
                        System.IO.Directory.CreateDirectory("Logs");
                        System.IO.File.WriteAllBytes("Logs/Joromi-Spirit-Practice.png", pixels.EncodeToPNG());
                    }
                    finally
                    {
                        camera.targetTexture = null; RenderTexture.active = previous;
                        UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
                    }
                }
                game.End();
                Assert.IsFalse(game.Active);
                Assert.IsFalse(root.transform.Find("Joromi Spirit Arena").gameObject.activeSelf);
                Assert.IsFalse(root.transform.Find("Spirit Boxing HUD").gameObject.activeSelf);
                game.Begin(story, StoryLanguages.Find("en"));
                Assert.AreEqual(SpiritFightPhase.Ready, game.Rules.Phase);
                Assert.AreEqual(0, game.Rules.Level);
                Assert.AreEqual(1, root.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Joromi Spirit Arena"));
                game.End();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(assets);
                foreach (var other in otherCameras) if (other != null) other.enabled = true;
                RenderSettings.ambientLight = ambient; RenderSettings.ambientMode = ambientMode; RenderSettings.fog = fog;
            }
        }
    }
}
