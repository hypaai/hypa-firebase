using System;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaStories
{
    public enum ProfileSyncAction { None, Pull, Push }

    public static class StoryProfileSync
    {
        static bool _started;
        static SynchronizationContext _main;

        public static event Action<StoryProfile> Updated;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _main = SynchronizationContext.Current;
            StoryProfileStore.Saved += profile => _ = PushAsync(profile);
            HybridServices.Upgraded += () => OnMain(() => _ = PullAsync());
            _ = PullAsync();
        }

        public static UserProfile ToUser(StoryProfile profile, string device)
        {
            var look = profile.Copy();
            look.localId = null;
            look.username = "";
            look.displayName = "";
            return new UserProfile
            {
                username = profile.username,
                displayName = profile.displayName,
                appearance = JsonUtility.ToJson(look),
                appearanceVersion = StoryProfile.CurrentVersion,
                createdAt = profile.createdAt,
                lastDevice = device,
                lastSeenAt = profile.savedAt,
            };
        }

        public static StoryProfile FromUser(UserProfile user, string localId)
        {
            if (user == null || string.IsNullOrEmpty(user.appearance)) return null;
            if (user.appearanceVersion < 1 || user.appearanceVersion > StoryProfile.CurrentVersion) return null;
            StoryProfile profile;
            try { profile = JsonUtility.FromJson<StoryProfile>(user.appearance); }
            catch (ArgumentException) { return null; }
            if (profile == null) return null;
            profile.username = user.username ?? "";
            profile.displayName = user.displayName ?? "";
            if (StoryProfile.UsernameError(profile.username) != null) return null;
            profile.localId = localId;
            profile.createdAt = user.createdAt;
            profile.savedAt = user.lastSeenAt;
            profile.Migrate();
            profile.Normalize();
            return profile;
        }

        public static ProfileSyncAction Decide(StoryProfile local, UserProfile remote)
        {
            var usable = remote != null && FromUser(remote, null) != null;
            if (local == null) return usable ? ProfileSyncAction.Pull : ProfileSyncAction.None;
            if (!usable) return ProfileSyncAction.Push;
            if (remote.lastSeenAt > local.savedAt) return ProfileSyncAction.Pull;
            if (local.savedAt > remote.lastSeenAt) return ProfileSyncAction.Push;
            return ProfileSyncAction.None;
        }

        public static async Task PullAsync()
        {
            UserProfile remote;
            try { remote = await HybridServices.Current.Profiles.LoadAsync(); }
            catch (Exception e) { Debug.LogWarning($"Hypa Stories: profile not loaded ({e.GetType().Name})."); return; }
            OnMain(() =>
            {
                var local = StoryProfileStore.Load();
                switch (Decide(local, remote))
                {
                    case ProfileSyncAction.Pull:
                        var pulled = FromUser(remote, local?.localId);
                        StoryProfileStore.Save(pulled, false);
                        Updated?.Invoke(StoryProfileStore.Load());
                        break;
                    case ProfileSyncAction.Push:
                        _ = PushAsync(local);
                        break;
                }
            });
        }

        static async Task PushAsync(StoryProfile profile)
        {
            try { await HybridServices.Current.Profiles.SaveAsync(ToUser(profile, HybridStoryQa.Device())); }
            catch (Exception e) { Debug.LogWarning($"Hypa Stories: profile not saved online ({e.GetType().Name})."); }
        }

        static void OnMain(Action action)
        {
            if (_main == null || SynchronizationContext.Current == _main) action();
            else _main.Post(_ => action(), null);
        }
    }
}
