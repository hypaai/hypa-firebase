using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaCore.Hybrid.Web
{
    /// <summary>
    /// The Web implementation of the G01 services (WebGL builds), on the Firebase JS SDK through <see cref="WebBridge"/>.
    /// The web config comes from <c>firebaseWeb</c> in <c>hypa_client_config.json</c> (public values, not secrets). While it
    /// is blank the factory returns null and the app plays on the Fakes. Rooms stay on the Fake until G09 defines them.
    /// On localhost the build uses the Firebase Emulator Suite instead (project <see cref="EmulatorProject"/>); add
    /// <c>?firebase=live</c> to the URL to use the real project there.
    /// </summary>
    public static class WebServices
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            HybridServices.WebFactory = Create;
#endif
        }

        /// <summary>The emulator project. A "demo-" id never reaches a real Firebase project and needs no login.</summary>
        public const string EmulatorProject = "demo-hypa";

        static HybridServices Create()
        {
            JObject config;
            if (UsesEmulators(Application.absoluteURL))
            {
                Debug.Log($"Hypa: using the Firebase emulators ({EmulatorProject}). Add ?firebase=live to the URL for the real project.");
                config = EmulatorConfig();
            }
            else
            {
                var resource = Resources.Load<TextAsset>("hypa_client_config");
                config = Config(resource != null ? JObject.Parse(resource.text) : null);
            }
            if (config == null)
            {
                Debug.Log("Hypa: the Firebase web config is blank; playing on local data.");
                return null;
            }
            WebBridge.Install();
            // Firebase starts in the background. Calls made before it is ready wait in JS; the story never waits on it.
            _ = Init(config);
            var flags = new WebRemoteFlags();
            return new HybridServices
            {
                Auth = new WebAuth(),
                Progress = new WebProgressStore(),
                Flags = flags,
                OnDevice = new ChromeNanoModel(),
                Cloud = new WebCloudModel(flags),
                RouteLog = new WebRouteLog(),
            };
        }

        static async Task Init(JObject config)
        {
            try { await WebBridge.CallAsync("init", config); }
            catch (WebBridgeException e) { Debug.LogWarning($"Hypa: Firebase did not start ({e.Code}). Playing on local data."); }
        }

        /// <summary>True for a page on localhost or 127.0.0.1, unless its URL asks for <c>firebase=live</c>.</summary>
        public static bool UsesEmulators(string pageUrl)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return false;
            if (uri.Host != "localhost" && uri.Host != "127.0.0.1") return false;
            return uri.Query.IndexOf("firebase=live", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Init arguments for the emulators: placeholder app values (the emulators accept any) and no App Check.</summary>
        public static JObject EmulatorConfig() => new JObject
        {
            ["firebase"] = new JObject { ["apiKey"] = "demo-key", ["projectId"] = EmulatorProject, ["appId"] = "demo-app" },
            ["emulators"] = true,
        };

        /// <summary>
        /// The bridge's init arguments from the client config: { firebase: { apiKey, authDomain, projectId, appId, ... },
        /// recaptchaEnterpriseSiteKey }. Null unless apiKey, projectId and appId are all set.
        /// </summary>
        public static JObject Config(JObject clientConfig)
        {
            if (!(clientConfig?["firebaseWeb"] is JObject web)) return null;
            var firebase = new JObject();
            foreach (var field in web.Properties())
            {
                var value = (string)field.Value;
                if (field.Name != "recaptchaEnterpriseSiteKey" && !string.IsNullOrWhiteSpace(value)) firebase[field.Name] = value.Trim();
            }
            foreach (var required in new[] { "apiKey", "projectId", "appId" })
                if (firebase[required] == null) return null;
            return new JObject { ["firebase"] = firebase, ["recaptchaEnterpriseSiteKey"] = ((string)web["recaptchaEnterpriseSiteKey"])?.Trim() ?? "" };
        }
    }

    public sealed class WebAuth : IHypaAuth
    {
        public string Uid { get; private set; }
        public event Action<string> UidChanged;

        public WebAuth() => WebBridge.Event += (name, value) => { if (name == "uid") SetUid((string)value); };

        void SetUid(string uid)
        {
            if (uid == Uid) return;
            Uid = uid;
            UidChanged?.Invoke(uid);
        }

        public async Task<string> SignInAnonymouslyAsync()
        {
            var uid = (string)await WebBridge.CallAsync("signIn");
            SetUid(uid);
            return uid;
        }

        /// <summary>A Google popup; call it from a button press so the browser allows the popup.</summary>
        public Task LinkGoogleAsync() => WebBridge.CallAsync("link", new JObject { ["provider"] = "google.com" });
        public Task LinkAppleAsync() => WebBridge.CallAsync("link", new JObject { ["provider"] = "apple.com" });
        public async Task<string> IdTokenAsync() => (string)await WebBridge.CallAsync("idToken");
        public async Task<string> AppCheckTokenAsync() => (string)await WebBridge.CallAsync("appCheckToken");
    }

    /// <summary>
    /// <c>users/{uid}/progress/current</c> in Firestore, with the IndexedDB cache on. <see cref="SaveAsync"/> completes once the
    /// write is in the local cache (offline too); before the first sign-in it holds the newest save and writes it after.
    /// </summary>
    public sealed class WebProgressStore : IProgressStore
    {
        public event Action<Progress> ChangedElsewhere;

        public WebProgressStore() => WebBridge.Event += (name, value) =>
        {
            if (name == "progress" && value is JObject doc) ChangedElsewhere?.Invoke(doc.ToObject<Progress>());
        };

        public async Task<Progress> LoadAsync() => (await WebBridge.CallAsync("loadProgress") as JObject)?.ToObject<Progress>();

        public Task SaveAsync(Progress p) => WebBridge.CallAsync("saveProgress", JObject.FromObject(p));
    }

    public sealed class WebRemoteFlags : IRemoteFlags
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public async Task FetchAsync()
        {
            try
            {
                if (!(await WebBridge.CallAsync("fetchFlags") is JObject values)) return;
                _values.Clear();
                foreach (var v in values.Properties()) _values[v.Name] = (string)v.Value;
            }
            catch (WebBridgeException e) { Debug.LogWarning($"Hypa: Remote Config not fetched ({e.Code}); using defaults."); }
        }

        /// <summary>Same rule as the Fake: a fetched value, else the contract default, else the fallback.</summary>
        public string Get(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : (RemoteConfigKeys.Default(key) ?? fallback);
    }

    /// <summary>Cloud Gemini through Firebase AI Logic (Gemini Developer API). The model is Remote Config <c>cloud_model</c>.</summary>
    public sealed class WebCloudModel : ICloudModel
    {
        readonly IRemoteFlags _flags;
        public WebCloudModel(IRemoteFlags flags) => _flags = flags;

        JObject Args(string prompt) => new JObject { ["side"] = "cloud", ["model"] = _flags.Get(RemoteConfigKeys.CloudModel, null), ["prompt"] = prompt };

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct) => (string)await WebBridge.CallAsync("generate", Args(prompt), ct);

        public async Task<string> GenerateAsync(string prompt, IReadOnlyList<CloudTool> tools, CancellationToken ct)
        {
            if (tools == null || tools.Count == 0) return await GenerateAsync(prompt, ct);
            var contents = new JArray { new JObject { ["role"] = "user", ["parts"] = new JArray { new JObject { ["text"] = prompt } } } };
            var declarations = CloudToolCalls.Declarations(tools);
            for (var round = 0; ; round++)
            {
                var last = round >= CloudToolCalls.MaxRounds;
                var args = new JObject { ["model"] = _flags.Get(RemoteConfigKeys.CloudModel, null), ["contents"] = contents, ["tools"] = last ? new JArray() : declarations };
                var reply = await WebBridge.CallAsync("generateTurn", args, ct) as JObject;
                var calls = reply?["calls"] as JArray;
                if (last || calls == null || calls.Count == 0) return (string)reply?["text"] ?? "";
                contents.Add(reply["content"]);
                var answers = new JArray();
                foreach (var call in calls) answers.Add(await CloudToolCalls.RunAsync(tools, call as JObject, ct));
                contents.Add(new JObject { ["role"] = "function", ["parts"] = answers });
            }
        }

        public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var chunk in WebBridge.StreamAsync("stream", Args(prompt), ct)) yield return (string)chunk;
        }
    }

    public sealed class WebRouteLog : IRouteLog
    {
        static readonly Newtonsoft.Json.JsonSerializer NoNulls = Newtonsoft.Json.JsonSerializer.Create(
            new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });

        public void Log(RouteEvent e)
        {
            if (e != null) _ = Send(JObject.FromObject(e, NoNulls));
        }

        static async Task Send(JObject e)
        {
            try { await WebBridge.CallAsync("logRoute", e); }
            catch (WebBridgeException x) { Debug.LogWarning($"Hypa: route event not saved ({x.Code})."); }
        }
    }

    /// <summary>
    /// Gemini Nano in desktop Chrome through Firebase AI Logic's on-device mode. Answers only ever come from the device: when the
    /// model is not ready the call fails at once (code "unavailable") and G05's router uses the cloud, so a story never waits for
    /// the first download. <see cref="DownloadAsync"/> starts that download from Settings.
    /// </summary>
    public sealed class ChromeNanoModel : IOnDeviceModel
    {
        public const string StatusAvailable = "available", StatusDownloadable = "downloadable", StatusDownloading = "downloading", StatusUnavailable = "unavailable";

        string _status = StatusUnavailable;
        /// <summary>The last known status. Refreshed by <see cref="StatusAsync"/>.</summary>
        public string Status => _status;
        public bool Available => _status == StatusAvailable;
        /// <summary>0 to 1 while downloading.</summary>
        public float DownloadProgress { get; private set; }
        /// <summary>Raised when <see cref="Status"/> or <see cref="DownloadProgress"/> changes.</summary>
        public event Action Changed;

        public ChromeNanoModel() { _ = StatusAsync(); }

        public async Task<string> StatusAsync()
        {
            string status;
            try { status = (string)await WebBridge.CallAsync("nanoStatus"); }
            catch (WebBridgeException) { status = StatusUnavailable; }
            SetStatus(status);
            return _status;
        }

        void SetStatus(string status)
        {
            if (status != StatusAvailable && status != StatusDownloadable && status != StatusDownloading) status = StatusUnavailable;
            if (status == _status) return;
            _status = status;
            Changed?.Invoke();
        }

        /// <summary>Downloads the model (minutes, first time only). Call it from a button press: Chrome needs a user gesture.</summary>
        public async Task DownloadAsync(CancellationToken ct = default)
        {
            await foreach (var fraction in WebBridge.StreamAsync("nanoDownload", null, ct))
            {
                DownloadProgress = Mathf.Clamp01((float)fraction);
                _status = StatusDownloading;
                Changed?.Invoke();
            }
            await StatusAsync();
        }

        static JObject Args(string prompt) => new JObject { ["side"] = "device", ["prompt"] = prompt };

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct) => (string)await WebBridge.CallAsync("generate", Args(prompt), ct);

        public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var chunk in WebBridge.StreamAsync("stream", Args(prompt), ct)) yield return (string)chunk;
        }
    }
}
