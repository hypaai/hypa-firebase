using System;
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
        public IRemoteFlags Flags { get; set; } = new FakeRemoteFlags();
        public IRoomSync Rooms { get; set; } = new FakeRoomSync();
        public IOnDeviceModel OnDevice { get; set; } = new FakeOnDeviceModel();
        public ICloudModel Cloud { get; set; } = new FakeCloudModel();

        /// <summary>Set by the Native implementation (G02). Returns null if Firebase cannot start on this device.</summary>
        public static Func<HybridServices> NativeFactory { get; set; }
        /// <summary>Set by the Web implementation (G03).</summary>
        public static Func<HybridServices> WebFactory { get; set; }

        static HybridServices _current;
        public static HybridServices Current => _current ?? (_current = Create());

        /// <summary>For tests.</summary>
        public static void Override(HybridServices services) => _current = services;

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
