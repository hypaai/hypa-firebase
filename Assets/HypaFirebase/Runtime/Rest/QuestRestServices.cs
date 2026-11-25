using System.Net.Http;
using System.Threading.Tasks;
using HypaCore.Hybrid;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// The services for Quest when the Firebase SDK does not run there: auth, App Check and progress over REST.
    /// Everything else (flags, room sync, models) stays on the Fakes until its own implementation exists.
    /// Use it with <see cref="HybridServices.UpgradeAsync"/>:
    /// <c>_ = HybridServices.UpgradeAsync(() =&gt; QuestRestServices.CreateAsync(config));</c>
    /// Call it from the main thread, so the local storage can be set up there.
    /// </summary>
    public static class QuestRestServices
    {
        public static async Task<HybridServices> CreateAsync(FirebaseRestConfig config, HttpMessageHandler handler = null)
        {
            var store = new PlayerPrefsKeyValueStore(PlayerPrefsRefreshTokenStore.Key, RestProgressStore.LocalKey, RestProgressStore.DirtyKey);
            var session = new RestAuthSession(config.ApiKey, new PlayerPrefsRefreshTokenStore(store), handler);
            var appCheck = new RestAppCheck(config.ApiKey, config.ProjectId, config.AppId, config.AppCheckDebugToken, handler);
            var auth = new RestHypaAuth(session, appCheck);
            await auth.SignInAnonymouslyAsync();
            return new HybridServices { Auth = auth, Progress = new RestProgressStore(config, auth, store, handler), RouteLog = new RestRouteLog(config, auth, handler) };
        }
    }
}
