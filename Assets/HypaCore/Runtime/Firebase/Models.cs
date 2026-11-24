using System;

namespace HypaCore.Hybrid
{
    /// <summary>Which path answered (G05 decides, everyone reads it). Shown in the UI as a chip.</summary>
    public enum Route { OnDevice, CloudGemini, Hypa, Offline }

    /// <summary>For example: "On device · Gemini Nano · 0.4 s", "Cloud · Gemini · 1.2 s", "Hypa · Yorùbá · 2.1 s", "Offline · on device".</summary>
    public sealed record RouteDecision(Route Route, string Model, string Reason, int LatencyMs);

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
                case ProxyDailySecondsPerUser: return "900";
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

        // Error codes in { "error": { "code", "message" } }.
        public const string ErrorQuota = "quota";
        public const string ErrorUnauthenticated = "unauthenticated";
        public const string ErrorAppCheck = "appcheck";
        public const string ErrorUpstream = "upstream";
        public const string ErrorBadRequest = "bad_request";
    }

    [Serializable] public sealed class TtsRequest { public string text; public string language; public string voice; public string model; }
    [Serializable] public sealed class TtsResponse { public string audioBase64; public string mime; public string model; public string voice; }
    [Serializable] public sealed class AsrRequest { public string audioBase64; public string mime; public string language; public string model; }
    [Serializable] public sealed class AsrResponse { public string text; public string model; }
    [Serializable] public sealed class ChatMessage { public string role; public string content; }
    [Serializable] public sealed class ChatRequest { public string system; public ChatMessage[] messages; public string language; public string model; }
    [Serializable] public sealed class ChatResponse { public string text; public string model; }
    [Serializable] public sealed class TranslateRequest { public string text; public string from; public string to; public string model; }
    [Serializable] public sealed class TranslateResponse { public string text; public string model; }
    [Serializable] public sealed class ProxyErrorBody { public string code; public string message; }
}
