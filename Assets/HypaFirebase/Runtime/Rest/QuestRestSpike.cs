using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaFirebase.Rest
{
    /// <summary>
    /// Put this on any object in a scene and run it on a Quest to test the REST path (docs/quest-path.md).
    /// It reads <c>hypa.firebase.local.json</c> from the app's data folder (never from the project), runs
    /// <see cref="QuestRestSpikeRunner"/> and writes the report to the log.
    /// </summary>
    public sealed class QuestRestSpike : MonoBehaviour
    {
        public const string ConfigFile = "hypa.firebase.local.json";

        async void Start()
        {
            var path = Path.Combine(Application.persistentDataPath, ConfigFile);
            if (!File.Exists(path))
            {
                Debug.LogError($"QuestRestSpike: put {ConfigFile} in {Application.persistentDataPath} (see docs/quest-path.md).");
                return;
            }

            var json = JObject.Parse(File.ReadAllText(path));
            var config = new FirebaseRestConfig((string)json["apiKey"], (string)json["projectId"], (string)json["appId"], (string)json["appCheckDebugToken"], (string)json["databaseUrl"]);
            var store = new PlayerPrefsKeyValueStore(PlayerPrefsRefreshTokenStore.Key, RestProgressStore.LocalKey, RestProgressStore.DirtyKey);

            var steps = await new QuestRestSpikeRunner(config, store).RunAsync(CancellationToken.None);
            Debug.Log(QuestRestSpikeRunner.Format(steps));
        }
    }
}
