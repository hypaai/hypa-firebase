using System;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaCore
{
    /// <summary>
    /// Client for Hypa's REST developer API (POST {base}/v2/developer/{endpoint}, header x-api-key).
    /// All endpoints need a key. Responses may be JSON or server-sent events; <see cref="PostAsync"/> returns the
    /// final JSON payload either way.
    ///
    /// STATUS: transport is shared with the live-verified MCP client. Request/response field names for the REST
    /// endpoints have NOT been confirmed with a keyed live call yet, except <see cref="ChatAsync"/>; <see cref="MtAsync"/> and
    /// <see cref="DictionaryAsync"/> are typed from the API notes. Confirm each with one live call (README > API)
    /// before building features on them. Prefer <see cref="HypaMcpClient"/> where a tool exists: typed schemas,
    /// verified, and audio as a link.
    /// </summary>
    public sealed class HypaRestClient
    {
        public const string BasePath = "/v2/developer/";

        public static class Endpoints
        {
            public const string Mt = "mt", Tts = "tts", Asr = "asr", Chat = "chat", Dictionary = "dictionary", Ocr = "ocr";
        }

        readonly HypaConfig _cfg;
        public HypaRestClient(HypaConfig cfg = null) { _cfg = cfg ?? HypaConfig.Current; }

        /// <summary>Generic call for any endpoint. Returns the last JSON payload of the response.</summary>
        public async Task<JToken> PostAsync(string endpoint, JObject body, CancellationToken ct)
        {
            var trace = HypaTrace.Enabled ? HypaTrace.Begin("rest " + endpoint, HypaTrace.Describe(body)) : null;
            try
            {
                if (!_cfg.HasProxy) throw new HypaException(HypaErrorKind.MissingConfiguration, $"REST '{endpoint}' needs the authenticated Hypa proxy.");
                var raw = await HypaHttp.PostAsync(_cfg, BasePath + endpoint, body.ToString(Formatting.None), ct).ConfigureAwait(false);
                var payloads = HypaHttp.JsonPayloads(raw);
                if (payloads.Count == 0) throw new HypaException(HypaErrorKind.InvalidResponse, $"{endpoint}: empty response.");
                var last = payloads[payloads.Count - 1];
                JToken result;
                try { result = JToken.Parse(last); }
                catch (JsonException e)
                {
                    // an error event carries plain text ("Your request could not be processed... model was not found"): say that
                    var text = last.Length > 300 ? last.Substring(0, 300) + "…" : last;
                    throw new HypaException(HypaErrorKind.InvalidResponse, $"{endpoint}: {text}", 0, null, e);
                }
                HypaTrace.End(trace, "ok", trace == null ? null : result is JObject o ? HypaTrace.Describe(o) : HypaTrace.Clip(result.ToString(Formatting.None), 120));
                return result;
            }
            catch (OperationCanceledException) { HypaTrace.End(trace, "cancelled"); throw; }
            catch (Exception e) { HypaTrace.End(trace, e is HypaException h ? h.Kind.ToString() : e.GetType().Name, e.Message); throw; }
        }

        /// <summary>
        /// Machine translation. Empty source = auto-detect. Non-streaming; the final event carries
        /// choices[0].message.content. UNVERIFIED field names (source_lang/target_lang) - confirm live.
        /// </summary>
        public async Task<string> MtAsync(string text, string targetLang, string sourceLang, CancellationToken ct)
        {
            var body = new JObject
            {
                ["text"] = text, ["target_lang"] = targetLang, ["source_lang"] = sourceLang ?? "",
                ["stream"] = false, ["provider"] = "auto",
            };
            var res = await PostAsync(Endpoints.Mt, body, ct).ConfigureAwait(false);
            var content = (string)res.SelectToken("choices[0].message.content");
            if (string.IsNullOrEmpty(content)) throw new HypaException(HypaErrorKind.InvalidResponse, "mt: no choices[0].message.content.");
            return content;
        }

        /// <summary>Sends a G01 chat request and returns the answer text.</summary>
        public async Task<string> ChatAsync(string model, JArray messages, string language, int maxTokens, CancellationToken ct)
            => (await ChatWithRouteAsync(model, messages, language, maxTokens, ct).ConfigureAwait(false)).text;

        /// <summary>A blank model or "auto" uses the proxy's fallback order. An explicit model is tried once.</summary>
        public async Task<ChatResponse> ChatWithRouteAsync(string model, JArray messages, string language, int maxTokens, CancellationToken ct)
        {
            var history = new JArray(messages);
            var body = new JObject { ["messages"] = history, ["language"] = language, ["maxTokens"] = maxTokens };
            if (history.First is JObject first && (string)first["role"] == "system")
            {
                body["system"] = (string)first["content"];
                first.Remove();
            }
            if (!string.IsNullOrWhiteSpace(model)) body["model"] = model;
            var raw = await HypaHttp.PostProxyAsync(_cfg, ProxyApi.Chat, body.ToString(Formatting.None), ct).ConfigureAwait(false);
            var reply = JObject.Parse(raw).ToObject<ChatResponse>();
            if (string.IsNullOrWhiteSpace(reply?.text))
                throw new HypaException(HypaErrorKind.InvalidResponse, "chat: the proxy returned no text.");
            return reply;
        }

        /// <summary>Dictionary lookup. UNVERIFIED request shape - confirm live.</summary>
        public async Task<HypaDictionaryEntry> DictionaryAsync(string word, string language, CancellationToken ct)
        {
            var body = new JObject { ["word"] = word, ["language"] = language ?? "English", ["fun_facts"] = false, ["cultural_context"] = false };
            var res = await PostAsync(Endpoints.Dictionary, body, ct).ConfigureAwait(false);
            return new HypaDictionaryEntry { Raw = (res["result"] as JObject) ?? (res as JObject) };
        }
    }
}
