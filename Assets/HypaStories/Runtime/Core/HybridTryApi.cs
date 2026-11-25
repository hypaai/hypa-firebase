using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using Debug = UnityEngine.Debug;

namespace HypaStories
{
    public sealed class HybridTryApi : ITryApi
    {
        readonly ITryApi _inner;
        readonly Func<HybridServices> _services;
        readonly Func<HypaProxyClient> _makeProxy;
        readonly Func<bool> _online;
        readonly Action _unreachable;
        readonly Func<AiMode> _mode;
        readonly Func<bool> _share;
        readonly Func<double> _clockMs;
        HypaProxyClient _proxy;

        public RouteDecision LastRoute { get; private set; }

        public HybridTryApi(ITryApi inner, Func<HybridServices> services = null, Func<HypaProxyClient> proxy = null, Func<bool> online = null,
            Action unreachable = null, Func<AiMode> mode = null, Func<bool> shareStats = null, Func<double> clockMs = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _services = services ?? (() => HybridServices.Current);
            _makeProxy = proxy ?? (() => HypaProxyClient.FromConfig());
            _online = online ?? (() => NetworkStatus.Online);
            _unreachable = unreachable ?? (() => NetworkStatus.MarkUnreachable());
            _mode = mode ?? (() => HypaSettings.AiMode);
            _share = shareStats ?? (() => HypaSettings.ShareRouteStats);
            var watch = Stopwatch.StartNew();
            _clockMs = clockMs ?? (() => watch.Elapsed.TotalMilliseconds);
        }

        public Task<IReadOnlyList<HypaLanguage>> LanguagesAsync(CancellationToken ct) => _inner.LanguagesAsync(ct);
        public Task<HypaDictionaryEntry> LookUpAsync(string word, string language, CancellationToken ct) => _inner.LookUpAsync(word, language, ct);
        public Task SpeakAsync(string text, string language, string voice, CancellationToken ct) => _inner.SpeakAsync(text, language, voice, ct);
        public Task<string> ExplainAsync(string sourceText, string targetText, string sourceLang, string targetLang, CancellationToken ct)
            => _inner.ExplainAsync(sourceText, targetText, sourceLang, targetLang, ct);
        public bool IsPlaying => _inner.IsPlaying;
        public void StopSpeaking() => _inner.StopSpeaking();

        public static string Code(string name) => StoryLanguages.Find(name)?.Code ?? (name ?? "").Trim().ToLowerInvariant();

        public async Task<HypaTranslation> TranslateAsync(string text, string targetLang, string sourceLang, CancellationToken ct)
        {
            var svc = _services();
            string status;
            try { status = await svc.OnDevice.StatusAsync(); }
            catch (Exception) { status = "unavailable"; }
            var ctx = RouteContext.FromFlags(svc.Flags, _online(), status, _mode());
            var ask = new RouteAsk
            {
                Kind = RouteKind.Translate, Language = Code(targetLang), SourceLanguage = string.IsNullOrEmpty(sourceLang) ? null : Code(sourceLang),
                LanguageName = StoryLanguages.Find(targetLang)?.Name ?? targetLang,
            };
            var route = HybridRouter.Decide(ask, ctx);
            var started = _clockMs();
            Route? from = null;
            try
            {
                HypaTranslation result;
                try { result = await Run(route, text, targetLang, sourceLang, ctx, svc, ct); }
                catch (Exception e) when (!(e is OperationCanceledException) && route.Route == Route.OnDevice && ctx.Online)
                {
                    Debug.LogWarning($"Hypa Stories: the device couldn't translate ({e.Message}); Hypa will.");
                    from = route.Route;
                    route = new RouteDecision(Route.Hypa, ask.LanguageName, "The device's model couldn't translate", 0);
                    result = await Run(route, text, targetLang, sourceLang, ctx, svc, ct);
                }
                LastRoute = route with { LatencyMs = (int)(_clockMs() - started) };
                Log(svc, route, ask.Language, from, true);
                return result;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                if (NetworkStatus.IsNetworkError(e)) _unreachable();
                LastRoute = null;
                Log(svc, route, ask.Language, from, false);
                throw;
            }
        }

        async Task<HypaTranslation> Run(RouteDecision route, string text, string targetLang, string sourceLang, RouteContext ctx, HybridServices svc, CancellationToken ct)
        {
            if (route.Route == Route.OnDevice || (route.Route == Route.Offline && route.Model == HybridRouter.OnDevice))
            {
                var reply = await svc.OnDevice.GenerateAsync(
                    $"Translate this {sourceLang} text into {targetLang}. Reply with the translation only, nothing else.\n\n{text}", ct);
                var clean = (reply ?? "").Trim().Trim('"', '“', '”');
                if (clean.Length == 0) throw new HypaException(HypaErrorKind.InvalidResponse, "an empty translation");
                return new HypaTranslation { Text = clean, Model = ctx.OnDeviceName };
            }
            if (route.Route == Route.Hypa)
            {
                var r = await Proxy().TranslateAsync(new TranslateRequest { text = text, targetLang = targetLang, sourceLang = string.IsNullOrEmpty(sourceLang) ? null : sourceLang }, ct);
                return new HypaTranslation { Text = r?.translation, DetectedLanguage = string.IsNullOrEmpty(sourceLang) ? r?.language : null, Model = r?.route?.model };
            }
            throw new HypaException(HypaErrorKind.Offline, route.Reason);
        }

        HypaProxyClient Proxy()
        {
            if (_proxy != null) return _proxy;
            try { return _proxy = _makeProxy(); }
            catch (ArgumentException e) { throw new HypaException(HypaErrorKind.MissingConfiguration, e.Message); }
        }

        void Log(HybridServices svc, RouteDecision route, string language, Route? from, bool ok)
        {
            if (!_share()) return;
            try
            {
                svc.RouteLog?.Log(new RouteEvent
                {
                    kind = RouteNames.Translate, route = RouteNames.Of(route.Route), model = route.Model, language = language,
                    latencyMs = LastRoute?.LatencyMs ?? 0, ok = ok, fallbackFrom = from.HasValue ? RouteNames.Of(from.Value) : null,
                    device = HybridStoryQa.Device(), at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't log the route: " + e.Message); }
        }
    }
}
