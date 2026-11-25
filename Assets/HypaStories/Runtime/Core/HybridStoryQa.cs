using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HypaCore;
using HypaCore.Hybrid;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HypaStories
{
    public sealed class HybridStoryQa : IStoryQa
    {
        const int CloudMaxTokens = 30000, HypaMaxTokens = 1024;
        public int DeadlineMs = 12000;
        public string Mode => "hybrid";

        public RouteDecision Last { get; private set; }
        public event Action<RouteDecision> Routed;
        public event Action<string> Notice;

        readonly Func<HybridServices> _services;
        readonly Func<HypaProxyClient> _makeProxy;
        readonly Func<bool> _online;
        readonly Action _unreachable;
        readonly Func<AiMode> _mode;
        readonly Func<bool> _share;
        readonly Func<double> _clockMs;
        readonly Func<string, string> _title;
        HypaProxyClient _proxy;

        public HybridStoryQa(Func<HybridServices> services = null, Func<HypaProxyClient> proxy = null, Func<bool> online = null,
            Action unreachable = null, Func<AiMode> mode = null, Func<bool> shareStats = null, Func<double> clockMs = null,
            Func<string, string> title = null)
        {
            _services = services ?? (() => HybridServices.Current);
            _makeProxy = proxy ?? (() => HypaProxyClient.FromConfig());
            _online = online ?? (() => NetworkStatus.Online);
            _unreachable = unreachable ?? (() => NetworkStatus.MarkUnreachable());
            _mode = mode ?? (() => HypaSettings.AiMode);
            _share = shareStats ?? (() => HypaSettings.ShareRouteStats);
            var watch = Stopwatch.StartNew();
            _clockMs = clockMs ?? (() => watch.Elapsed.TotalMilliseconds);
            _title = title ?? (id => StoryLibrary.Find(id)?.Title);
        }

        public async Task AnswerAsync(QaRequest req, Action<string, int, bool> onSentence, CancellationToken ct)
        {
            var svc = _services();
            var lang = req.Language ?? StoryLanguages.All[0];
            var title = _title(req.StoryId);
            string status;
            try { status = await svc.OnDevice.StatusAsync(); }
            catch (Exception) { status = "unavailable"; }
            var ctx = RouteContext.FromFlags(svc.Flags, _online(), status, _mode());
            var ask = new RouteAsk
            {
                Language = lang.Code, LanguageName = lang.Name, NeedsTools = ToolQuestion.Matches(req.Question),
                PromptTokens = HybridRouter.Tokens(StoryPrompt.Build(req, title).Text),
            };
            var route = HybridRouter.Decide(ask, ctx);
            if (route.Reason == HybridRouter.Downloading) Notice?.Invoke(StoryLanguages.Lines(lang.Code).Preparing);
            var started = _clockMs();
            var said = 0;
            Route? from = null;
            var tried = new HashSet<string>();

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(DeadlineMs);
                void Say(string text, int i, bool last)
                {
                    if (said == 0)
                    {
                        deadline.CancelAfter(Timeout.Infinite);
                        Last = route with { LatencyMs = (int)(_clockMs() - started) };
                        Routed?.Invoke(Last);
                    }
                    said++;
                    onSentence(text, i, last);
                }

                while (true)
                {
                    tried.Add(Key(route));
                    try
                    {
                        await Run(route, req, title, lang, ask, ctx, svc, Say, deadline.Token);
                        Log(svc, route, lang, from, true, started);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (said == 0)
                    {
                        Log(svc, route, lang, from, false, started);
                        throw new TimeoutException($"no answer within {DeadlineMs / 1000} s ({route.Chip()})");
                    }
                    catch (Exception e) when (said > 0)
                    {
                        Debug.LogWarning($"Hypa Stories: {route.Chip()} stopped mid-answer ({e.Message}).");
                        Log(svc, route, lang, from, true, started);
                        return;
                    }
                    catch (Exception e)
                    {
                        if (NetworkStatus.IsNetworkError(e)) { _unreachable(); ctx.Online = false; }
                        var next = Next(route, ctx, e);
                        Debug.LogWarning($"Hypa Stories: {route.Chip()} couldn't answer ({e.Message}); {(next == null ? "no other way to answer" : "trying " + next.Chip())}.");
                        if (next == null || tried.Contains(Key(next)))
                        {
                            Log(svc, route, lang, from, false, started);
                            throw;
                        }
                        from ??= route.Route;
                        route = next;
                    }
                }
            }
        }

        static string Key(RouteDecision d) => d.Route + "/" + d.Model;
        static bool OnTheDevice(RouteDecision d) => d.Route == Route.OnDevice || (d.Route == Route.Offline && d.Model == HybridRouter.OnDevice);

        public static RouteDecision Next(RouteDecision failed, RouteContext ctx, Exception error)
        {
            var cloud = ctx.Online && ctx.Mode != AiMode.OnDeviceOnly;
            var device = ctx.Mode != AiMode.CloudOnly && ctx.OnDeviceReady;
            var saved = new RouteDecision(Route.Offline, HybridRouter.SavedAnswers, ctx.Online ? "Nothing else could answer" : "Offline, and no model on this device", 0);
            if (failed.Route == Route.Hypa)
                return cloud ? new RouteDecision(Route.CloudGemini, HybridRouter.Cloud, "Hypa couldn't answer, so Gemini did", 0)
                    : device ? new RouteDecision(Route.Offline, HybridRouter.OnDevice, "Offline, so the model on this device answers", 0) : saved;
            if (OnTheDevice(failed))
                return cloud && failed.Route == Route.OnDevice
                    ? new RouteDecision(Route.CloudGemini, HybridRouter.Cloud, error is WrongLanguageException ? "The device answered in the wrong language" : "The device's model couldn't answer", 0)
                    : saved;
            if (failed.Route == Route.CloudGemini)
                return device
                    ? ctx.Online ? new RouteDecision(Route.OnDevice, ctx.OnDeviceName, "Gemini couldn't answer, so the device did", 0)
                        : new RouteDecision(Route.Offline, HybridRouter.OnDevice, "Offline, so the model on this device answers", 0)
                    : saved;
            return null;
        }

        async Task Run(RouteDecision route, QaRequest req, string title, StoryLanguage lang, RouteAsk ask, RouteContext ctx,
            HybridServices svc, Action<string, int, bool> say, CancellationToken ct)
        {
            var offline = route.Route == Route.Offline && !ctx.Online ? StoryLanguages.Lines(lang.Code).BackOnline : null;
            if (OnTheDevice(route))
            {
                var prompt = StoryPrompt.Build(req, title, ctx.MaxOnDeviceTokens);
                Func<string, bool> sameLanguage = s => !ReplyLanguage.Mismatch(s, lang.Code);
                await Stream(svc.OnDevice.StreamAsync(prompt.Text, ct), say, sameLanguage, offline, ct);
                return;
            }
            switch (route.Route)
            {
                case Route.CloudGemini:
                {
                    var prompt = StoryPrompt.Build(req, title, CloudMaxTokens, ctx.HypaLangs.Contains(lang.Code));
                    if (ask.NeedsTools) Speak(await svc.Cloud.GenerateAsync(prompt.Text, Tools(), ct), say, null);
                    else await Stream(svc.Cloud.StreamAsync(prompt.Text, ct), say, null, null, ct);
                    return;
                }
                case Route.Hypa:
                {
                    var prompt = StoryPrompt.Build(req, title, CloudMaxTokens);
                    var reply = await Proxy().ChatAsync(new ChatRequest
                    {
                        messages = new[] { new ChatMessage { role = "system", content = prompt.System }, new ChatMessage { role = "user", content = prompt.User } },
                        maxTokens = HypaMaxTokens,
                    }, ct);
                    Speak(reply?.content, say, null);
                    return;
                }
                default:
                {
                    var saved = StubStoryQa.AnswerFor(req.Question, req.LineIndex, lang.Code, req.StoryId);
                    Speak(string.Join(" ", saved), say, offline);
                    return;
                }
            }
        }

        static async Task Stream(IAsyncEnumerable<string> chunks, Action<string, int, bool> say, Func<string, bool> accept, string tail, CancellationToken ct)
        {
            var sentences = new SentenceStream(say, accept);
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                sentences.Push(chunk);
                if (sentences.Enough) break;
            }
            sentences.Finish(tail);
            if (sentences.Count == 0) throw new HypaException(HypaErrorKind.InvalidResponse, "an empty answer");
        }

        static void Speak(string reply, Action<string, int, bool> say, string tail)
        {
            var sentences = ChatStoryQa.Sentences(reply);
            if (sentences.Count == 0) throw new HypaException(HypaErrorKind.InvalidResponse, "an empty answer");
            if (!string.IsNullOrEmpty(tail)) sentences.Add(tail);
            for (var i = 0; i < sentences.Count; i++) say(sentences[i], i, i == sentences.Count - 1);
        }

        HypaProxyClient Proxy()
        {
            if (_proxy != null) return _proxy;
            try { return _proxy = _makeProxy(); }
            catch (ArgumentException e) { throw new HypaException(HypaErrorKind.MissingConfiguration, e.Message); }
        }

        public IReadOnlyList<CloudTool> Tools() => new[]
        {
            new CloudTool
            {
                Name = "translate", Description = "Translate text with Hypa, which knows Nigerian and other African languages best.",
                Parameters = new Dictionary<string, string> { ["text"] = "The text to translate.", ["to"] = "The language to translate into, e.g. Yoruba." },
                Run = (a, ct) => Tool(async () => (await Proxy().TranslateAsync(new TranslateRequest { text = Arg(a, "text"), targetLang = Arg(a, "to") }, ct)).translation),
            },
            new CloudTool
            {
                Name = "define", Description = "What a word means in English, from Hypa.",
                Parameters = new Dictionary<string, string> { ["word"] = "The word.", ["language"] = "The word's language, e.g. Yoruba." },
                Run = (a, ct) => Tool(async () =>
                {
                    var r = await Proxy().TranslateAsync(new TranslateRequest { text = Arg(a, "word"), sourceLang = Arg(a, "language"), targetLang = "English" }, ct);
                    return $"“{Arg(a, "word")}” ({Arg(a, "language")}) means “{r.translation}” in English.";
                }),
            },
        };

        static string Arg(IReadOnlyDictionary<string, string> args, string key) => args != null && args.TryGetValue(key, out var v) ? v ?? "" : "";

        static async Task<string> Tool(Func<Task<string>> run)
        {
            try { return await run(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Debug.LogWarning("Hypa Stories: a Hypa tool failed: " + e.Message);
                return "Hypa couldn't look that up just now.";
            }
        }

        void Log(HybridServices svc, RouteDecision route, StoryLanguage lang, Route? from, bool ok, double started)
        {
            if (!_share()) return;
            try
            {
                svc.RouteLog?.Log(new RouteEvent
                {
                    kind = RouteNames.Answer, route = RouteNames.Of(route.Route), model = route.Model, language = lang.Code,
                    latencyMs = ok && Last != null ? Last.LatencyMs : (int)(_clockMs() - started), ok = ok,
                    fallbackFrom = from.HasValue ? RouteNames.Of(from.Value) : null, device = Device(),
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
            }
            catch (Exception e) { Debug.LogWarning("Hypa Stories: couldn't log the route: " + e.Message); }
        }

        public static string Device()
        {
            if (Application.isEditor) return Platforms.Editor;
            switch (Application.platform)
            {
                case RuntimePlatform.WebGLPlayer: return Platforms.Web;
                case RuntimePlatform.IPhonePlayer: return Platforms.Ios;
                case RuntimePlatform.Android: return SystemInfo.deviceModel.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 ? Platforms.Quest : Platforms.Android;
                default: return Application.platform.ToString().ToLowerInvariant();
            }
        }
    }
}
