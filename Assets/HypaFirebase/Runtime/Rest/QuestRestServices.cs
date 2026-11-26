using System.Net.Http;
using System.Threading.Tasks;
using HypaCore.Hybrid;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// The services for Quest when the Firebase SDK does not run there: auth, App Check, progress, profile, cloud Gemini (Firebase AI Logic)
    /// route stats and Remote Config over REST. Room sync stays on the Fakes until its own implementation exists.
    /// Use it with <see cref="HybridServices.UpgradeAsync"/>:
    /// <c>_ = HybridServices.UpgradeAsync(() =&gt; QuestRestServices.CreateAsync(config));</c>
    /// Call it from the main thread, so the local storage can be set up there.
    /// </summary>
    public static class QuestRestServices
    {
        public static async Task<HybridServices> CreateAsync(FirebaseRestConfig config, HttpMessageHandler handler = null)
        {
            var store = new PlayerPrefsKeyValueStore(PlayerPrefsRefreshTokenStore.Key, RestProgressStore.LocalKey, RestProgressStore.DirtyKey,
                RestRemoteFlags.FidKey, RestRemoteFlags.RefreshKey, RestRemoteFlags.ValuesKey, RestProfileStore.LocalKey, RestProfileStore.DirtyKey);
            var session = new RestAuthSession(config.ApiKey, new PlayerPrefsRefreshTokenStore(store), handler);
            var appCheck = new RestAppCheck(config.ApiKey, config.ProjectId, config.AppId, config.AppCheckDebugToken, handler);
            var auth = new RestHypaAuth(session, appCheck);
            await auth.SignInAnonymouslyAsync();
            return new HybridServices
            {
                Auth = auth,
                Progress = new RestProgressStore(config, auth, store, handler),
                Profiles = new RestProfileStore(config, auth, store, handler),
                Flags = new RestRemoteFlags(config, store, handler),
                Cloud = new RestCloudModel(config, auth, () => HybridServices.Current.Flags.Get(RemoteConfigKeys.CloudModel, null), handler),
                RouteLog = new RestRouteLog(config, auth, handler),
            };
        }
    }
}
