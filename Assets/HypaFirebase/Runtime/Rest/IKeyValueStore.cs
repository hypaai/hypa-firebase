using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace HypaFirebase.Rest
{
    /// <summary>Small local storage for the REST classes (the offline copy of progress, the refresh token).</summary>
    public interface IKeyValueStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    /// <summary>
    /// PlayerPrefs-backed, and safe to call from any thread. PlayerPrefs may only be used on Unity's main thread, but the REST
    /// classes finish their requests on a thread-pool thread. So the keys it is created with are read once, when it is created,
    /// and every read after that comes from that copy. A write updates the copy at once and is posted back to the main thread.
    /// Create it on the main thread, naming every key it will hold. Nothing here logs a value.
    /// </summary>
    public sealed class PlayerPrefsKeyValueStore : IKeyValueStore
    {
        readonly object _lock = new object();
        readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        readonly SynchronizationContext _main = SynchronizationContext.Current;

        public PlayerPrefsKeyValueStore(params string[] keys)
        {
            foreach (var key in keys) _cache[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        }

        public string Get(string key)
        {
            lock (_lock) return _cache.TryGetValue(key, out var value) ? value : null;
        }

        public void Set(string key, string value)
        {
            lock (_lock) _cache[key] = value;
            Write(() => { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); });
        }

        public void Remove(string key)
        {
            lock (_lock) _cache[key] = null;
            Write(() => { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); });
        }

        void Write(Action action)
        {
            if (_main == null || SynchronizationContext.Current == _main) action();
            else _main.Post(_ => action(), null);
        }
    }
}
