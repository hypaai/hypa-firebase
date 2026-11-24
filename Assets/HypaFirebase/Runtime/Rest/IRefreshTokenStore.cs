namespace HypaFirebase.Rest
{
    /// <summary>Keeps the anonymous user's refresh token between launches, so the same user comes back later.</summary>
    public interface IRefreshTokenStore
    {
        string Load();
        void Save(string refreshToken);
        void Clear();
    }

    /// <summary>
    /// PlayerPrefs-backed store, safe to use from the thread that finishes a request (see <see cref="PlayerPrefsKeyValueStore"/>).
    /// Create it on the main thread. The refresh token never goes in a log or in gameplay data.
    /// </summary>
    public sealed class PlayerPrefsRefreshTokenStore : IRefreshTokenStore
    {
        public const string Key = "hypa.firebase.refreshToken";
        readonly IKeyValueStore _store;

        public PlayerPrefsRefreshTokenStore() : this(new PlayerPrefsKeyValueStore(Key)) { }
        public PlayerPrefsRefreshTokenStore(IKeyValueStore store) { _store = store; }

        public string Load() => _store.Get(Key);
        public void Save(string refreshToken) => _store.Set(Key, refreshToken);
        public void Clear() => _store.Remove(Key);
    }
}
