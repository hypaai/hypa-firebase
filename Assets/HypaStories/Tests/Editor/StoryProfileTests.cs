using NUnit.Framework;

namespace HypaStories.Tests
{
    public class StoryProfileTests
    {
        [TestCase("ab")]
        [TestCase("9first")]
        [TestCase("two names")]
        [TestCase("<b>name</b>")]
        [TestCase("abcdefghijklmnopqrstu")]
        public void Username_RejectsInvalidHandles(string name)
            => Assert.IsNotNull(StoryProfile.UsernameError(name));

        [TestCase("Timi_01")]
        [TestCase("abc")]
        [TestCase("  storyteller  ")]
        public void Username_AcceptsValidHandles(string name)
            => Assert.IsNull(StoryProfile.UsernameError(name));

        [Test] public void Normalize_PreservesIdentityAndBoundsAppearance()
        {
            var p = new StoryProfile { localId = "existing-id", username = "  Timi_01 ", displayName = " ", skin = 999, hairStyle = -1, outfit = 100, jaw = 4f, lips = -2f, weight = float.NaN };
            p.Normalize();
            Assert.AreEqual("timi_01", p.username);
            Assert.AreEqual("timi_01", p.displayName);
            Assert.AreEqual("existing-id", p.localId);
            Assert.AreEqual(ProfileCatalog.SkinTones.Length - 1, p.skin);
            Assert.AreEqual(0, p.hairStyle);
            Assert.AreEqual(ProfileCatalog.Outfits.Length - 1, p.outfit);
            Assert.AreEqual(1f, p.jaw);
            Assert.AreEqual(0f, p.lips);
            Assert.AreEqual(0.5f, p.weight);
        }

        [Test] public void Migrate_Version1KeepsIdentityAndMapsLooks()
        {
            const string v1 = "{\"version\":1,\"localId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"username\":\"timi\",\"displayName\":\"Timi\"," +
                "\"skin\":7,\"hairStyle\":0,\"hairColor\":2,\"eyeColor\":1,\"faceShape\":2,\"outfit\":1,\"outfitColor\":3,\"accessory\":1,\"build\":0}";
            var p = UnityEngine.JsonUtility.FromJson<StoryProfile>(v1);
            p.Migrate(); p.Normalize();
            Assert.AreEqual(StoryProfile.CurrentVersion, p.version);
            Assert.AreEqual("timi", p.username);
            Assert.AreEqual("Timi", p.displayName);
            Assert.AreEqual("0f8fad5b-d9cb-469f-a165-70867728950e", p.localId);
            Assert.AreEqual(ProfileCatalog.SkinTones.Length - 1, p.skin, "the deepest v1 tone stays the deepest");
            Assert.AreEqual("afro01", ProfileCatalog.HairStyles[p.hairStyle].Id, "v1 coils become the afro");
            Assert.Greater(p.headShape, 0.5f, "v1 'defined' face is squarer than average");
            Assert.Less(p.weight, 0.5f, "v1 slim build is slimmer than average");
            Assert.AreEqual(2, p.hairColor);
        }

        [Test] public void Migrate_LeavesCurrentVersionAlone()
        {
            var p = new StoryProfile { skin = 3, hairStyle = 5, weight = 0.8f };
            p.Migrate();
            Assert.AreEqual(3, p.skin);
            Assert.AreEqual(5, p.hairStyle);
            Assert.AreEqual(0.8f, p.weight);
        }

        [Test] public void Catalog_OptionsHaveUniqueIds()
        {
            void Unique(ProfileOption[] options)
            {
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var o in options)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(o.Label));
                    if (o.Id != null) Assert.IsTrue(seen.Add(o.Id), "duplicate id " + o.Id);
                }
            }
            Unique(ProfileCatalog.EyeColors); Unique(ProfileCatalog.Brows); Unique(ProfileCatalog.HairStyles);
            Unique(ProfileCatalog.Outfits); Unique(ProfileCatalog.Shoes);
            // the body's vertex colour has 24 mask bits: one per outfit and pair of shoes
            Assert.LessOrEqual(ProfileCatalog.Outfits.Length + ProfileCatalog.Shoes.Length, 24);
        }

        [Test] public void Copy_ChangesDoNotModifySavedDraft()
        {
            var original = new StoryProfile { username = "timi", localId = "unchanged", hairStyle = 2 };
            var draft = original.Copy(); draft.hairStyle = 4; draft.username = "another";
            Assert.AreEqual(2, original.hairStyle);
            Assert.AreEqual("timi", original.username);
            Assert.AreEqual("unchanged", draft.localId);
        }
    }
}
