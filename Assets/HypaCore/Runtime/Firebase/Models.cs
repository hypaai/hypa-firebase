using System;

namespace HypaCore.Hybrid
{
    /// <summary>Which path answered (G05 decides, everyone reads it). Shown in the UI as a chip.</summary>
    public enum Route { OnDevice, CloudGemini, Hypa, Offline }

    /// <summary>
    /// Which path answered and why. <see cref="Model"/> is what the chip names: "Gemini Nano", "Gemini", the language for Hypa
    /// ("Yorùbá"), or "on device" when offline. <see cref="Reason"/> is for logs and the debug panel, not the chip.
    /// </summary>
    public sealed record RouteDecision(Route Route, string Model, string Reason, int LatencyMs)
    {
        /// <summary>The UI chip: "On device · Gemini Nano · 0.4 s", "Cloud · Gemini · 1.2 s", "Hypa · Yorùbá · 2.1 s", "Offline · on device".</summary>
        public string Chip()
        {
            var label = Route switch { Route.OnDevice => "On device", Route.CloudGemini => "Cloud", Route.Hypa => "Hypa", _ => "Offline" };
            var chip = string.IsNullOrEmpty(Model) ? label : label + " · " + Model;
            return LatencyMs > 0 ? chip + " · " + (LatencyMs / 1000f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s" : chip;
        }
    }

    /// <summary>Firestore <c>users/{uid}/progress/current</c> (docs/contracts.md, section 3).</summary>
    [Serializable]
    public sealed class Progress
    {
        public string storyId;
        public string lineId;
        public int lineIndex;
        public string language;
        /// <summary>"camp", "hut" or "village".</summary>
        public string place;
        /// <summary>Unix milliseconds.</summary>
        public long updatedAt;
        public string device;
    }

    /// <summary>
    /// Firestore <c>users/{uid}</c> (docs/contracts.md, section 3). The account's profile: who the player is and what their character
    /// looks like. <see cref="appearance"/> is the app's own JSON (Stories writes its character there), versioned by
    /// <see cref="appearanceVersion"/>, so the platform layer never needs to know a game's character model.
    /// </summary>
    [Serializable]
    public sealed class UserProfile
    {
        /// <summary>3–20 letters, digits or underscores, starting with a letter, lower case. Not unique: it is a display handle.</summary>
        public string username;
        public string displayName;
        public string language;
        public string appearance;
        public int appearanceVersion;
        /// <summary>Unix ms.</summary>
        public long createdAt;
        public string lastDevice;
        /// <summary>Unix ms.</summary>
        public long lastSeenAt;
    }

    /// <summary>Firestore paths (docs/contracts.md, section 3).</summary>
    public static class FirestorePaths
    {
        public static string User(string uid) => "users/" + uid;
        public static string Progress(string uid) => "users/" + uid + "/progress/current";
        public static string Story(string uid, string storyId) => "users/" + uid + "/stories/" + storyId;
        public static string Word(string uid, string wordId) => "users/" + uid + "/words/" + wordId;
    }

    /// <summary>Remote Config keys and their defaults (docs/contracts.md, section 5).</summary>
    public static class RemoteConfigKeys
    {
        public const string RoutePreferOnDevice = "route_prefer_on_device";
        public const string RouteOnDeviceLangs = "route_on_device_langs";
        public const string RouteHypaLangs = "route_hypa_langs";
        public const string RouteMaxOnDeviceTokens = "route_max_on_device_tokens";
        public const string CloudModel = "cloud_model";
        public const string LiveModel = "live_model";
        public const string LiveLangs = "live_langs";
        public const string ProxyDailySecondsPerUser = "proxy_daily_seconds_per_user";

        /// <summary>The default for a key. <c>live_model</c> has none until G10 picks the model name from the Firebase docs.</summary>
        public static string Default(string key)
        {
            switch (key)
            {
                case RoutePreferOnDevice: return "true";
                case RouteOnDeviceLangs: return "en,fr,es";
                case RouteHypaLangs: return "yo,ha,ig,pcm,sw";
                case RouteMaxOnDeviceTokens: return "5000";
                case CloudModel: return "gemini-2.5-flash-lite";
                case LiveLangs: return "en,fr,es,sw";
                case ProxyDailySecondsPerUser: return "600";
                default: return null;
            }
        }
    }

    /// <summary>The Hypa proxy (G04 builds it, G05, G10 and G12 call it). Docs/contracts.md, section 6.</summary>
    public static class ProxyApi
    {
        public const string Tts = "/hypa/tts";
        public const string Asr = "/hypa/asr";
        public const string Chat = "/hypa/chat";
        public const string Translate = "/hypa/translate";
        public const string AuthorizationHeader = "Authorization";   // "Bearer <Firebase ID token>"
        public const string AppCheckHeader = "X-Firebase-AppCheck";  // "<App Check token>"

        // Error codes in { "error": { "code", "message", ... } }.
        public const string ErrorQuota = "quota";
        public const string ErrorUnauthenticated = "unauthenticated";
        public const string ErrorAppCheck = "app_check";
        public const string ErrorUpstream = "upstream";
        public const string ErrorBadRequest = "bad_request";
        public const string ErrorTooLarge = "too_large";
        public const string ErrorNotFound = "not_found";
        public const string ErrorMethodNotAllowed = "method_not_allowed";
        public const string ErrorInternal = "internal";
    }

    /// <summary>Which provider, model and voice Hypa used, for the route chip.</summary>
    [Serializable] public sealed class ProxyRoute { public string provider; public string model; public string voice; }

    /// <summary>The caller's use of the proxy today. <see cref="resetsAt"/> is ISO 8601 (UTC midnight).</summary>
    [Serializable] public sealed class ProxyUsage { public int secondsUsed; public int secondsLimit; public int requestsUsed; public int requestsLimit; public string resetsAt; }

    /// <summary><see cref="voice"/> works only with the <see cref="model"/> that owns it. <see cref="inline"/>: return base64, not a link.</summary>
    [Serializable] public sealed class TtsRequest { public string text; public string language; public string voice; public string model; public bool? inline; }
    /// <summary>Either <see cref="audioUrl"/> or <see cref="audioBase64"/> (always base64 when the request was <c>inline</c>).</summary>
    [Serializable] public sealed class TtsResponse { public string audioUrl; public string audioBase64; public string mime; public int sampleRate; public ProxyRoute route; public ProxyUsage usage; }
    /// <summary>Base64 audio, at most 1 MiB and 30 s.</summary>
    [Serializable] public sealed class AsrRequest { public string audio; public string sourceLang; public double? durationSeconds; }
    [Serializable] public sealed class AsrResponse { public string transcript; public string language; public double audioSeconds; public ProxyRoute route; public ProxyUsage usage; }
    /// <summary><see cref="role"/>: "system", "user" or "assistant".</summary>
    [Serializable] public sealed class ChatMessage { public string role; public string content; }
    /// <summary><see cref="model"/>: "auto" (the default) or "provider/model".</summary>
    [Serializable] public sealed class ChatRequest { public ChatMessage[] messages; public string model; public int? maxTokens; }
    [Serializable] public sealed class ChatResponse { public string content; public ProxyRoute route; public ProxyUsage usage; }
    [Serializable] public sealed class TranslateRequest { public string text; public string targetLang; public string sourceLang; }
    [Serializable] public sealed class TranslateResponse { public string translation; public string language; public ProxyRoute route; public ProxyUsage usage; }
    /// <summary><see cref="scope"/> for quota, <see cref="reason"/> and <see cref="retryable"/> for upstream.</summary>
    [Serializable] public sealed class ProxyErrorBody { public string code; public string message; public string scope; public string reason; public bool? retryable; public int? retryAfterSeconds; }
}
