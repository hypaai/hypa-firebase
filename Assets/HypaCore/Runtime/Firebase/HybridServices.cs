using System;
using System.Threading.Tasks;
using UnityEngine;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// The set of services the app uses, picked once at start-up (docs/contracts.md, section 1):
    /// WebGL build: Web. Android, iOS and Quest: Native. Editor: Fake, unless "Use real Firebase" is ticked.
    /// G02 registers <see cref="NativeFactory"/> and G03 registers <see cref="WebFactory"/>. Until a factory is registered
    /// the Fakes are used, so the app always runs.
    /// </summary>
    public sealed class HybridServices
    {
        /// <summary>Editor menu setting. Ignored in builds.</summary>
        public const string UseRealFirebaseKey = "hypa.useRealFirebase";

        public IHypaAuth Auth { get; set; } = new FakeAuth();
        public IProgressStore Progress { get; set; } = new FakeProgressStore();
        public IProfileStore Profiles { get; set; } = new FakeProfileStore();
        public IRemoteFlags Flags { get; set; } = new FakeRemoteFlags();
        public IRoomSync Rooms { get; set; } = new FakeRoomSync();
        public IOnDeviceModel OnDevice { get; set; } = OnDeviceFactory?.Invoke() ?? new NoOnDeviceModel();
        public ICloudModel Cloud { get; set; } = new FakeCloudModel();

        /// <summary>Set by the Native implementation (G02). Returns null if Firebase cannot start on this device.</summary>
        public static Func<HybridServices> NativeFactory { get; set; }
        /// <summary>Set by the Web implementation (G03).</summary>
        public static Func<HybridServices> WebFactory { get; set; }
        /// <summary>
        /// The platform's on-device model (G06 Android, G07 iOS), used by every new service set, the Fakes included: it needs no
        /// Firebase, so it works before and without the native services. Unset: <see cref="NoOnDeviceModel"/>.
        /// </summary>
        public static Func<IOnDeviceModel> OnDeviceFactory { get; set; }

        static HybridServices _current;
        public static HybridServices Current => _current ?? (_current = Create());

        /// <summary>For tests.</summary>
        public static void Override(HybridServices services) => _current = services;

        /// <summary>Raised after <see cref="UpgradeAsync"/> has switched <see cref="Current"/> to the real services.</summary>
        public static event Action Upgraded;

        /// <summary>
        /// Starts the real services in the background without blocking the story. The app keeps playing with the Fakes; when the
        /// real ones are ready they replace the Fakes in <see cref="Current"/> (so always read services through <see cref="Current"/>,
        /// never keep your own reference), progress and a profile saved meanwhile are sent if newer than the server's, and
        /// <see cref="Upgraded"/> is raised. If starting fails the Fakes stay and nothing is thrown.
        /// </summary>
        public static async Task UpgradeAsync(Func<Task<HybridServices>> start)
        {
            var local = Current;
            try
            {
                var real = await start();
                if (real == null) return;

                var fakeProgress = local.Progress;
                var pending = await fakeProgress.LoadAsync();
                var remote = await real.Progress.LoadAsync();
                if (pending != null && (remote == null || pending.updatedAt > remote.updatedAt)) await real.Progress.SaveAsync(pending);

                var fakeProfile = await local.Profiles.LoadAsync();
                var remoteProfile = await real.Profiles.LoadAsync();
                if (fakeProfile != null && (remoteProfile == null || fakeProfile.lastSeenAt > remoteProfile.lastSeenAt)) await real.Profiles.SaveAsync(fakeProfile);

                local.Auth = real.Auth;
                local.Progress = real.Progress;
                local.Profiles = real.Profiles;
                local.Flags = real.Flags;
                local.Rooms = real.Rooms;
                local.OnDevice = real.OnDevice;
                local.Cloud = real.Cloud;

                // The player may have saved to the Fakes while the server was being read. Send that too.
                var late = await fakeProgress.LoadAsync();
                if (late != null && late != pending && (remote == null || late.updatedAt > remote.updatedAt)) await real.Progress.SaveAsync(late);

                Upgraded?.Invoke();
            }
            catch (Exception e)
            {
                // The type only: a message could carry details we never want in a log.
                Debug.LogWarning($"Hypa: Firebase did not start ({e.GetType().Name}). Staying on local data.");
            }
        }

        static HybridServices Create()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return WebFactory?.Invoke() ?? new HybridServices();
#elif UNITY_EDITOR
            return PlayerPrefs.GetInt(UseRealFirebaseKey, 0) == 1 ? (NativeFactory?.Invoke() ?? new HybridServices()) : new HybridServices();
#else
            return NativeFactory?.Invoke() ?? new HybridServices();
#endif
        }
    }
}
