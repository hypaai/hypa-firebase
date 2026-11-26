using System.Collections.Generic;
using System.Linq;
using HypaCore.Hybrid;
using NUnit.Framework;

namespace HypaCore.Tests
{
    public class HybridRouterTests
    {
        static RouteAsk Answer(string lang, int tokens = 800, bool tools = false)
            => new RouteAsk { Language = lang, LanguageName = lang, PromptTokens = tokens, NeedsTools = tools };

        static RouteContext Ctx(bool online = true, string status = "available", AiMode mode = AiMode.Hybrid)
            => new RouteContext { Online = online, OnDeviceStatus = status, Mode = mode };

        static RouteDecision D(RouteAsk ask, RouteContext ctx) => HybridRouter.Decide(ask, ctx);

        [Test] public void English_RunsOnDevice_WhenTheModelIsReady_AndThePromptFits()
        {
            var d = D(Answer("en"), Ctx());
            Assert.AreEqual(Route.OnDevice, d.Route);
            Assert.AreEqual("Gemini Nano", d.Model);
            StringAssert.Contains("model on this device", d.Reason);
        }

        [Test] public void NigerianLanguagesAndKiswahili_GoToHypa_EvenWithAModelOnTheDevice()
        {
            foreach (var lang in new[] { "yo", "ha", "ig", "pcm", "sw" })
            {
                var d = D(new RouteAsk { Language = lang, LanguageName = "Yorùbá", PromptTokens = 10 }, Ctx());
                Assert.AreEqual(Route.Hypa, d.Route, lang);
                Assert.AreEqual("Yorùbá", d.Model);
            }
        }

        [Test] public void TheCloudAnswers_WhenTheDeviceCant()
        {
            Assert.AreEqual(Route.CloudGemini, D(Answer("en"), Ctx(status: "unavailable")).Route);
            Assert.AreEqual(Route.CloudGemini, D(Answer("de"), Ctx()).Route, "not an on-device language");
            Assert.AreEqual(Route.CloudGemini, D(Answer("en", tokens: 5001), Ctx()).Route, "too long for the device");
            Assert.AreEqual(Route.OnDevice, D(Answer("en", tokens: 5000), Ctx()).Route, "exactly the budget fits");
            Assert.AreEqual(Route.CloudGemini, D(Answer("en", tools: true), Ctx()).Route, "only the cloud calls tools");
            var noPreference = Ctx(); noPreference.PreferOnDevice = false;
            Assert.AreEqual(Route.CloudGemini, D(Answer("en"), noPreference).Route);
        }

        [Test] public void AModelStillDownloading_SendsTheQuestionToTheCloud_AndSaysWhy()
        {
            var d = D(Answer("fr"), Ctx(status: "downloading"));
            Assert.AreEqual(Route.CloudGemini, d.Route);
            Assert.AreEqual(HybridRouter.Downloading, d.Reason);
        }

        [Test] public void AModelNotYetDownloaded_SaysSo_InsteadOfSayingThereIsNone()
        {
            var answer = D(Answer("en"), Ctx(status: "downloadable"));
            Assert.AreEqual(Route.CloudGemini, answer.Route);
            StringAssert.Contains("not downloaded yet", answer.Reason);
            StringAssert.Contains("no model on this device", D(Answer("en"), Ctx(status: "unavailable")).Reason);

            var ctx = Ctx(status: "downloadable");
            ctx.OnDeviceLangs = RouteContext.Set("en,fr");
            var translate = D(new RouteAsk { Kind = RouteKind.Translate, SourceLanguage = "en", Language = "fr", PromptTokens = 10 }, ctx);
            StringAssert.Contains("not downloaded yet", translate.Reason);
        }

        [Test] public void Offline_UsesTheDevice_ElseTheSavedAnswers()
        {
            var d = D(Answer("en"), Ctx(online: false));
            Assert.AreEqual(Route.Offline, d.Route);
            Assert.AreEqual("Offline · on device", d.Chip());
            Assert.AreEqual(HybridRouter.OnDevice, D(Answer("yo"), Ctx(online: false)).Model, "offline, any language goes to the device");
            Assert.AreEqual(HybridRouter.SavedAnswers, D(Answer("en"), Ctx(online: false, status: "unavailable")).Model);
            Assert.AreEqual(HybridRouter.SavedAnswers, D(Answer("en"), Ctx(online: false, mode: AiMode.CloudOnly)).Model);
        }

        [Test] public void Settings_CloudOnly_And_OnDeviceOnly()
        {
            Assert.AreEqual(Route.CloudGemini, D(Answer("en"), Ctx(mode: AiMode.CloudOnly)).Route);
            Assert.AreEqual(Route.Hypa, D(Answer("yo"), Ctx(mode: AiMode.CloudOnly)).Route);
            Assert.AreEqual(Route.OnDevice, D(Answer("yo"), Ctx(mode: AiMode.OnDeviceOnly)).Route);
            Assert.AreEqual(Route.OnDevice, D(Answer("en", tokens: 90000, tools: true), Ctx(mode: AiMode.OnDeviceOnly)).Route);
            var none = D(Answer("en"), Ctx(mode: AiMode.OnDeviceOnly, status: "unavailable"));
            Assert.AreEqual(Route.Offline, none.Route);
            Assert.AreEqual(HybridRouter.SavedAnswers, none.Model);
            Assert.AreEqual(HybridRouter.Downloading, D(Answer("en"), Ctx(mode: AiMode.OnDeviceOnly, status: "downloading")).Reason);
        }

        [Test] public void EveryCombination_KeepsTheRules()
        {
            var langs = new[] { "en", "fr", "es", "yo", "ha", "ig", "pcm", "sw", "de" };
            var statuses = new[] { "available", "downloadable", "downloading", "unavailable" };
            var count = 0;
            foreach (var online in new[] { true, false })
            foreach (var lang in langs)
            foreach (var status in statuses)
            foreach (var tokens in new[] { 100, 5000, 5001, 40000 })
            foreach (var tools in new[] { false, true })
            foreach (var mode in new[] { AiMode.Hybrid, AiMode.CloudOnly, AiMode.OnDeviceOnly })
            foreach (var prefer in new[] { true, false })
            {
                var ctx = Ctx(online, status, mode); ctx.PreferOnDevice = prefer;
                var d = D(Answer(lang, tokens, tools), ctx);
                var what = $"{(online ? "online" : "offline")} {lang} {status} {tokens} tools={tools} {mode} prefer={prefer}: {d}";
                count++;
                Assert.IsFalse(string.IsNullOrEmpty(d.Reason), what);
                Assert.AreEqual(0, d.LatencyMs, what);
                var usesDevice = d.Route == Route.OnDevice || d.Model == HybridRouter.OnDevice;
                var usesCloud = d.Route == Route.CloudGemini || d.Route == Route.Hypa;
                if (usesDevice) Assert.AreEqual("available", status, "the device only when its model is ready: " + what);
                if (!online) Assert.IsFalse(usesCloud, "offline never waits on the network: " + what);
                if (mode == AiMode.CloudOnly) Assert.IsFalse(usesDevice, "cloud only: " + what);
                if (mode == AiMode.OnDeviceOnly) Assert.IsFalse(usesCloud, "on device only: " + what);
                if (online && mode == AiMode.Hybrid && new[] { "yo", "ha", "ig", "pcm", "sw" }.Contains(lang))
                    Assert.AreEqual(Route.Hypa, d.Route, what);
                if (mode == AiMode.Hybrid && d.Route == Route.OnDevice)
                    Assert.IsTrue(prefer && tokens <= 5000 && !tools && new[] { "en", "fr", "es" }.Contains(lang), what);
                if (online && mode == AiMode.Hybrid && status == "available" && prefer && tokens <= 5000 && !tools && new[] { "en", "fr", "es" }.Contains(lang))
                    Assert.AreEqual(Route.OnDevice, d.Route, what);
            }
            Assert.AreEqual(2 * 9 * 4 * 4 * 2 * 3 * 2, count);
        }

        [Test] public void Translation_OnDeviceForItsLanguages_HypaForTheRest()
        {
            RouteAsk T(string from, string to) => new RouteAsk { Kind = RouteKind.Translate, SourceLanguage = from, Language = to, LanguageName = to };
            Assert.AreEqual(Route.OnDevice, D(T("en", "fr"), Ctx()).Route);
            Assert.AreEqual(Route.Hypa, D(T("en", "yo"), Ctx()).Route);
            Assert.AreEqual(Route.Hypa, D(T("yo", "en"), Ctx()).Route);
            Assert.AreEqual(Route.Hypa, D(T(null, "fr"), Ctx()).Route, "auto-detect needs Hypa");
            Assert.AreEqual(Route.Hypa, D(T("en", "de"), Ctx()).Route);
            Assert.AreEqual(Route.Hypa, D(T("en", "fr"), Ctx(status: "unavailable")).Route);
            Assert.AreEqual(Route.Hypa, D(T("en", "fr"), Ctx(mode: AiMode.CloudOnly)).Route);
            Assert.AreEqual(HybridRouter.OnDevice, D(T("en", "es"), Ctx(online: false)).Model);
            Assert.AreEqual(Route.Offline, D(T("en", "yo"), Ctx(online: false)).Route);
            Assert.IsNull(D(T("en", "yo"), Ctx(online: false)).Model);
            Assert.AreEqual(Route.Offline, D(T("en", "yo"), Ctx(mode: AiMode.OnDeviceOnly)).Route);
        }

        [Test] public void Thresholds_ComeFromRemoteConfig()
        {
            var flags = new FakeRemoteFlags()
                .Set(RemoteConfigKeys.RouteOnDeviceLangs, "en, DE")
                .Set(RemoteConfigKeys.RouteHypaLangs, "yo")
                .Set(RemoteConfigKeys.RouteMaxOnDeviceTokens, "200")
                .Set(RemoteConfigKeys.RoutePreferOnDevice, "true");
            var ctx = RouteContext.FromFlags(flags, true, "available", AiMode.Hybrid);
            Assert.AreEqual(Route.OnDevice, D(Answer("de", 150), ctx).Route);
            Assert.AreEqual(Route.CloudGemini, D(Answer("de", 250), ctx).Route);
            Assert.AreEqual(Route.CloudGemini, D(Answer("fr", 10), ctx).Route);
            Assert.AreEqual(Route.CloudGemini, D(Answer("ha", 10), ctx).Route, "ha is no longer a Hypa language");

            flags.Set(RemoteConfigKeys.RoutePreferOnDevice, "FALSE").Set(RemoteConfigKeys.RouteMaxOnDeviceTokens, "nonsense");
            ctx = RouteContext.FromFlags(flags, true, "available", AiMode.Hybrid);
            Assert.IsFalse(ctx.PreferOnDevice);
            Assert.AreEqual(5000, ctx.MaxOnDeviceTokens, "a bad value keeps the default");

            var defaults = RouteContext.FromFlags(new FakeRemoteFlags(), true, null, AiMode.Hybrid);
            CollectionAssert.AreEquivalent(new[] { "en", "fr", "es" }, defaults.OnDeviceLangs);
            Assert.AreEqual("unavailable", defaults.OnDeviceStatus);
        }

        [Test] public void Tokens_AreAboutFourCharactersEach()
        {
            Assert.AreEqual(0, HybridRouter.Tokens(""));
            Assert.AreEqual(1, HybridRouter.Tokens("abc"));
            Assert.AreEqual(250, HybridRouter.Tokens(new string('a', 1000)));
        }

        [Test] public void CloudTools_AreCalledByTheFake()
        {
            var cloud = new FakeCloudModel { CallTool = "translate", CallArgs = new Dictionary<string, string> { ["text"] = "mother", ["to"] = "Yoruba" }, ReplyAfterTool = r => $"It is {r}." };
            var tool = new CloudTool { Name = "translate", Run = (a, ct) => System.Threading.Tasks.Task.FromResult(a["text"] == "mother" ? "ìyá" : "?") };
            var reply = cloud.GenerateAsync("what is mother in Yoruba?", new[] { tool }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual("It is ìyá.", reply);
            CollectionAssert.AreEqual(new[] { "ìyá" }, cloud.ToolResults);
        }

        [Test] public void NetworkErrors_AreToldApartFromRefusals()
        {
            Assert.IsTrue(NetworkStatus.IsNetworkError(new System.Net.Http.HttpRequestException("no route")));
            Assert.IsTrue(NetworkStatus.IsNetworkError(new HypaException(HypaErrorKind.Offline, "x")));
            Assert.IsTrue(NetworkStatus.IsNetworkError(new HypaProxyException(ProxyApi.ErrorUpstream, "could not be reached", 0)));
            Assert.IsTrue(NetworkStatus.IsNetworkError(new System.Exception("outer", new System.Net.Sockets.SocketException())));
            Assert.IsFalse(NetworkStatus.IsNetworkError(new HypaProxyException(ProxyApi.ErrorUpstream, "Hypa had a problem", 502)));
            Assert.IsFalse(NetworkStatus.IsNetworkError(new HypaProxyException(ProxyApi.ErrorQuota, "limit", 429)));
        }

        [Test] public void NetworkStatus_StaysDownForAWhileAfterAFailure()
        {
            var now = new System.DateTime(2026, 11, 24, 12, 0, 0, System.DateTimeKind.Utc);
            var device = NetworkStatus.DeviceOnline; var clock = NetworkStatus.Now;
            NetworkStatus.DeviceOnline = () => true; NetworkStatus.Now = () => now;
            try
            {
                NetworkStatus.MarkReachable();
                Assert.IsTrue(NetworkStatus.Online);
                NetworkStatus.MarkUnreachable();
                Assert.IsFalse(NetworkStatus.Online);
                now = now.AddSeconds(NetworkStatus.DownSeconds);
                Assert.IsTrue(NetworkStatus.Online);
            }
            finally { NetworkStatus.DeviceOnline = device; NetworkStatus.Now = clock; NetworkStatus.MarkReachable(); }
        }
    }
}
