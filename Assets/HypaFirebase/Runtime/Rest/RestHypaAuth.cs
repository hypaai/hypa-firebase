using System;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
#if UNITY_EDITOR
using HypaCore;
using UnityEngine;
#endif

namespace HypaFirebase.Rest
{
    /// <summary>
    /// <see cref="IHypaAuth"/> for Quest, over REST: anonymous sign-in and App Check with the debug provider.
    /// Linking Google or Apple is not available here (it needs a browser or the platform's sign-in), so it throws
    /// <see cref="NotSupportedException"/>. Quest users stay anonymous.
    /// </summary>
    public sealed class RestHypaAuth : IHypaAuth
    {
        readonly RestAuthSession _session;
        readonly RestAppCheck _appCheck;

        public RestHypaAuth(RestAuthSession session, RestAppCheck appCheck)
        {
            _session = session;
            _appCheck = appCheck;
            _session.Changed += () => UidChanged?.Invoke(_session.UserId);
        }

        public string Uid => _session.UserId;
        public event Action<string> UidChanged;

        public async Task<string> SignInAnonymouslyAsync()
        {
            await _session.SignInAnonymouslyAsync(CancellationToken.None).ConfigureAwait(false);
            return _session.UserId;
        }

        public Task LinkGoogleAsync() => throw new NotSupportedException("Google sign-in is not available over REST (Quest).");
        public Task LinkAppleAsync() => throw new NotSupportedException("Apple sign-in is not available over REST (Quest).");

        public Task<string> IdTokenAsync() => _session.GetIdTokenAsync(false, CancellationToken.None);
        public Task<string> AppCheckTokenAsync() => _appCheck != null
            ? _appCheck.GetTokenAsync(false, CancellationToken.None)
            : Task.FromResult<string>(null);

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ConnectEditorToAuthEmulator()
        {
            var config = HypaConfig.Current;
            if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var proxy) || !proxy.IsLoopback ||
                !Uri.TryCreate(config.AuthEmulatorUrl, UriKind.Absolute, out var auth) || !auth.IsLoopback)
                return;

            var session = new RestAuthSession("fake-api-key", new EditorRefreshTokenStore(), emulatorOrigin: auth);
            HybridServices.Current.Auth = new RestHypaAuth(session, null);
        }

        sealed class EditorRefreshTokenStore : IRefreshTokenStore
        {
            string _token;
            public string Load() => _token;
            public void Save(string refreshToken) => _token = refreshToken;
            public void Clear() => _token = null;
        }
#endif
    }
}
