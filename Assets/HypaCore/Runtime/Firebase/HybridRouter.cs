using System;
using System.Collections.Generic;
using System.Linq;

namespace HypaCore.Hybrid
{
    public enum RouteKind { Answer, Translate }

    public sealed class RouteAsk
    {
        public RouteKind Kind = RouteKind.Answer;
        public string Language;
        public string SourceLanguage;
        public string LanguageName;
        public int PromptTokens;
        public bool NeedsTools;
    }

    public sealed class RouteContext
    {
        public bool Online = true;
        public string OnDeviceStatus = "unavailable";
        public string OnDeviceName = "Gemini Nano";
        public AiMode Mode = AiMode.Hybrid;
        public bool PreferOnDevice = true;
        public ISet<string> OnDeviceLangs = Set(RemoteConfigKeys.Default(RemoteConfigKeys.RouteOnDeviceLangs));
        public ISet<string> HypaLangs = Set(RemoteConfigKeys.Default(RemoteConfigKeys.RouteHypaLangs));
        public int MaxOnDeviceTokens = 5000;

        public bool OnDeviceReady => OnDeviceStatus == "available";

        public static RouteContext FromFlags(IRemoteFlags flags, bool online, string onDeviceStatus, AiMode mode)
        {
            string Get(string key) => flags?.Get(key, RemoteConfigKeys.Default(key)) ?? RemoteConfigKeys.Default(key);
            return new RouteContext
            {
                Online = online,
                OnDeviceStatus = onDeviceStatus ?? "unavailable",
                Mode = mode,
                PreferOnDevice = !string.Equals(Get(RemoteConfigKeys.RoutePreferOnDevice), "false", StringComparison.OrdinalIgnoreCase),
                OnDeviceLangs = Set(Get(RemoteConfigKeys.RouteOnDeviceLangs)),
                HypaLangs = Set(Get(RemoteConfigKeys.RouteHypaLangs)),
                MaxOnDeviceTokens = int.TryParse(Get(RemoteConfigKeys.RouteMaxOnDeviceTokens), out var max) && max > 0 ? max : 5000,
            };
        }

        public static ISet<string> Set(string csv) => new HashSet<string>(
            (csv ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0));
    }

    public static class HybridRouter
    {
        public const string SavedAnswers = "saved answers", OnDevice = "on device", Cloud = "Gemini";
        public const string Downloading = "On-device model downloading (first time only)";

        public static int Tokens(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

        public static RouteDecision Decide(RouteAsk ask, RouteContext ctx)
        {
            if (ask == null) throw new ArgumentNullException(nameof(ask));
            ctx ??= new RouteContext();
            return ask.Kind == RouteKind.Translate ? Translate(ask, ctx) : Answer(ask, ctx);
        }

        static RouteDecision Answer(RouteAsk ask, RouteContext ctx)
        {
            var lang = Code(ask.Language);
            var name = string.IsNullOrEmpty(ask.LanguageName) ? lang : ask.LanguageName;
            var deviceLang = ctx.OnDeviceLangs.Contains(lang);
            var fits = ask.PromptTokens <= ctx.MaxOnDeviceTokens;

            if (!ctx.Online)
                return ctx.Mode != AiMode.CloudOnly && ctx.OnDeviceReady
                    ? new RouteDecision(Route.Offline, OnDevice, "Offline, so the model on this device answers", 0)
                    : new RouteDecision(Route.Offline, SavedAnswers, ctx.Mode == AiMode.CloudOnly ? "Offline, and Settings say cloud only" : "Offline, and no model on this device", 0);

            if (ctx.Mode == AiMode.OnDeviceOnly)
            {
                if (ctx.OnDeviceReady) return new RouteDecision(Route.OnDevice, ctx.OnDeviceName, $"{name}, on device only (Settings)", 0);
                return new RouteDecision(Route.Offline, SavedAnswers, ctx.OnDeviceStatus == "downloading" ? Downloading : "On device only (Settings), and no model on this device", 0);
            }

            if (ctx.HypaLangs.Contains(lang)) return new RouteDecision(Route.Hypa, name, $"{name}: Hypa speaks it best", 0);

            if (ctx.Mode == AiMode.CloudOnly) return new RouteDecision(Route.CloudGemini, Cloud, $"{name}, cloud only (Settings)", 0);

            if (ctx.PreferOnDevice && deviceLang && fits && !ask.NeedsTools && ctx.OnDeviceReady)
                return new RouteDecision(Route.OnDevice, ctx.OnDeviceName, $"{name}, model on this device, short question", 0);

            string why;
            if (ask.NeedsTools) why = $"{name}, a word to look up: only the cloud can call Hypa's tools";
            else if (!ctx.PreferOnDevice) why = $"{name}, cloud preferred (Remote Config)";
            else if (!deviceLang) why = $"{name} isn't one of the on-device languages";
            else if (ctx.OnDeviceStatus == "downloading") why = Downloading;
            else if (ctx.OnDeviceStatus == "downloadable") why = $"{name}, the on-device model is not downloaded yet (Home)";
            else if (!ctx.OnDeviceReady) why = $"{name}, no model on this device";
            else why = $"{name}, the story so far is too long for the device";
            return new RouteDecision(Route.CloudGemini, Cloud, why, 0);
        }

        static RouteDecision Translate(RouteAsk ask, RouteContext ctx)
        {
            var to = Code(ask.Language);
            var from = string.IsNullOrEmpty(ask.SourceLanguage) ? null : Code(ask.SourceLanguage);
            var name = string.IsNullOrEmpty(ask.LanguageName) ? to : ask.LanguageName;
            var devicePair = from != null && ctx.OnDeviceLangs.Contains(from) && ctx.OnDeviceLangs.Contains(to);
            var hypaPair = ctx.HypaLangs.Contains(to) || (from != null && ctx.HypaLangs.Contains(from));

            if (!ctx.Online)
                return ctx.Mode != AiMode.CloudOnly && devicePair && ctx.OnDeviceReady
                    ? new RouteDecision(Route.Offline, OnDevice, "Offline, so the model on this device translates", 0)
                    : new RouteDecision(Route.Offline, null, devicePair ? "Offline, and no model on this device" : "Offline: this pair needs Hypa", 0);

            if (ctx.Mode == AiMode.OnDeviceOnly)
                return devicePair && ctx.OnDeviceReady
                    ? new RouteDecision(Route.OnDevice, ctx.OnDeviceName, "On device only (Settings)", 0)
                    : new RouteDecision(Route.Offline, null, devicePair ? "On device only (Settings), and no model on this device" : "On device only (Settings): this pair needs Hypa", 0);

            if (hypaPair || ctx.Mode == AiMode.CloudOnly || from == null)
                return new RouteDecision(Route.Hypa, name, hypaPair ? $"{name}: Hypa translates it best" : from == null ? "Detecting the language needs Hypa" : "Cloud only (Settings)", 0);

            if (devicePair && ctx.PreferOnDevice && ctx.OnDeviceReady)
                return new RouteDecision(Route.OnDevice, ctx.OnDeviceName, "Both languages work on this device", 0);

            return new RouteDecision(Route.Hypa, name, devicePair
                ? ctx.OnDeviceStatus == "downloading" ? Downloading : ctx.OnDeviceStatus == "downloadable" ? "The on-device model is not downloaded yet (Home)" : "No model on this device"
                : "This pair isn't one of the on-device languages", 0);
        }

        static string Code(string language) => (language ?? "").Trim().ToLowerInvariant();
    }
}
