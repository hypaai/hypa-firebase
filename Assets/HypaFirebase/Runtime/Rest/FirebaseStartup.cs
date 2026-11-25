using System;
using System.IO;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaFirebase.Rest
{
    public static class FirebaseStartup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _ = FetchFlags();
#else
#if UNITY_EDITOR
            if (PlayerPrefs.GetInt(HybridServices.UseRealFirebaseKey, 0) != 1) return;
#endif
            if (HybridServices.NativeFactory != null)
            {
                _ = FetchFlags();
                return;
            }
            var config = LoadConfig(Application.persistentDataPath);
            if (config == null)
            {
                Debug.Log($"Hypa: no {QuestRestSpike.ConfigFile} in the app's data folder; playing on local data (docs/quest-path.md).");
                return;
            }
            HybridServices.Upgraded += () => _ = FetchFlags();
            _ = HybridServices.UpgradeAsync(() => QuestRestServices.CreateAsync(config));
#endif
        }

        public static FirebaseRestConfig LoadConfig(string folder)
        {
            var path = Path.Combine(folder, QuestRestSpike.ConfigFile);
            if (!File.Exists(path)) return null;
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                var config = new FirebaseRestConfig((string)json["apiKey"], (string)json["projectId"], (string)json["appId"], (string)json["appCheckDebugToken"], (string)json["databaseUrl"]);
                return string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ProjectId) || string.IsNullOrEmpty(config.AppId) ? null : config;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Hypa: {QuestRestSpike.ConfigFile} could not be read ({e.GetType().Name}).");
                return null;
            }
        }

        static async Task FetchFlags()
        {
            try { await HybridServices.Current.Flags.FetchAsync(); }
            catch (Exception e) { Debug.LogWarning($"Hypa: Remote Config not fetched ({e.GetType().Name}); using defaults."); }
        }
    }
}
