using UnityEngine;

namespace HypaFirebase.Rest
{
    /// <summary>Keeps the anonymous user's refresh token between launches, so the same user comes back later.</summary>
    public interface IRefreshTokenStore
    {
        string Load();
        void Save(string refreshToken);
        void Clear();
    }

    /// <summary>PlayerPrefs-backed store. The refresh token never goes in a log or in gameplay data.</summary>
    public sealed class PlayerPrefsRefreshTokenStore : IRefreshTokenStore
    {
        const string Key = "hypa.firebase.refreshToken";
        public string Load() => PlayerPrefs.GetString(Key, null);
        public void Save(string refreshToken) { PlayerPrefs.SetString(Key, refreshToken); PlayerPrefs.Save(); }
        public void Clear() { PlayerPrefs.DeleteKey(Key); PlayerPrefs.Save(); }
    }
}
