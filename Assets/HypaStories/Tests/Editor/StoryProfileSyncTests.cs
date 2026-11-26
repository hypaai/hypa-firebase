using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaStories.Tests
{
    public class StoryProfileSyncTests
    {
        static StoryProfile Local(long savedAt = 100) => new StoryProfile
        {
            localId = "11111111-1111-1111-1111-111111111111", username = "ade", displayName = "Ade", skin = 3, hairStyle = 2, height = 0.8f,
            createdAt = 50, savedAt = savedAt,
        };

        [Test] public void TheCharacter_GoesUpAndComesBackTheSame()
        {
            var user = StoryProfileSync.ToUser(Local(), "quest");

            Assert.AreEqual("ade", user.username);
            Assert.AreEqual("Ade", user.displayName);
            Assert.AreEqual(StoryProfile.CurrentVersion, user.appearanceVersion);
            Assert.AreEqual(100, user.lastSeenAt);
            Assert.AreEqual(50, user.createdAt);
            Assert.AreEqual("quest", user.lastDevice);
            StringAssert.DoesNotContain("11111111", user.appearance, "the device's own id stays on the device");
            Assert.LessOrEqual(user.appearance.Length, 4000, "fits the rules' limit");

            var back = StoryProfileSync.FromUser(user, "22222222-2222-2222-2222-222222222222");
            Assert.AreEqual("ade", back.username);
            Assert.AreEqual(3, back.skin);
            Assert.AreEqual(2, back.hairStyle);
            Assert.AreEqual(0.8f, back.height, 0.0001f);
            Assert.AreEqual("22222222-2222-2222-2222-222222222222", back.localId);
            Assert.AreEqual(100, back.savedAt);
        }

        [Test] public void ABrokenOrFutureProfile_IsIgnored()
        {
            Assert.IsNull(StoryProfileSync.FromUser(null, null));
            Assert.IsNull(StoryProfileSync.FromUser(new UserProfile { username = "ade", appearance = "", appearanceVersion = 2 }, null));
            var future = StoryProfileSync.ToUser(Local(), "web"); future.appearanceVersion = StoryProfile.CurrentVersion + 1;
            Assert.IsNull(StoryProfileSync.FromUser(future, null));
            var badName = StoryProfileSync.ToUser(Local(), "web"); badName.username = "1x";
            Assert.IsNull(StoryProfileSync.FromUser(badName, null));
        }

        [Test] public void TheNewerSide_Wins()
        {
            var remote = StoryProfileSync.ToUser(Local(200), "android");
            Assert.AreEqual(ProfileSyncAction.Pull, StoryProfileSync.Decide(Local(100), remote), "another device saved later");
            Assert.AreEqual(ProfileSyncAction.Push, StoryProfileSync.Decide(Local(300), remote), "this device saved later");
            Assert.AreEqual(ProfileSyncAction.None, StoryProfileSync.Decide(Local(200), remote));
        }

        [Test] public void FirstTimeOnEitherSide()
        {
            Assert.AreEqual(ProfileSyncAction.Pull, StoryProfileSync.Decide(null, StoryProfileSync.ToUser(Local(), "web")), "a new device of a known player");
            Assert.AreEqual(ProfileSyncAction.Push, StoryProfileSync.Decide(Local(), null), "nothing online yet");
            Assert.AreEqual(ProfileSyncAction.None, StoryProfileSync.Decide(null, null));
            Assert.AreEqual(ProfileSyncAction.Push, StoryProfileSync.Decide(Local(0), new UserProfile { username = "ade" }), "an unusable online copy is replaced");
        }
    }
}
