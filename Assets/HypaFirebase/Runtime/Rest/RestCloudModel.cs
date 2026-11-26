using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    public sealed class RestCloudModel : ICloudModel
    {
        public const string BaseUrl = "https://firebasevertexai.googleapis.com/v1beta";

        readonly FirebaseRestConfig _config;
        readonly IHypaAuth _auth;
        readonly Func<string> _model;
        readonly HttpClient _http;

        public RestCloudModel(FirebaseRestConfig config, IHypaAuth auth, Func<string> model = null, HttpMessageHandler handler = null)
        {
            _config = config;
            _auth = auth;
            _model = model ?? (() => RemoteConfigKeys.Default(RemoteConfigKeys.CloudModel));
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(60);
        }

        public string Url(bool stream)
        {
            var model = _model();
            if (string.IsNullOrEmpty(model)) model = RemoteConfigKeys.Default(RemoteConfigKeys.CloudModel);
            return $"{BaseUrl}/projects/{_config.ProjectId}/models/{model}:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent");
        }

        public Task<string> GenerateAsync(string prompt, CancellationToken ct) => GenerateAsync(prompt, null, ct);

        public async Task<string> GenerateAsync(string prompt, IReadOnlyList<CloudTool> tools, CancellationToken ct)
        {
            var contents = new JArray { UserTurn(prompt) };
            var declarations = tools == null || tools.Count == 0 ? null : CloudToolCalls.Declarations(tools);
            for (var round = 0; ; round++)
            {
                var body = new JObject { ["contents"] = contents };
                var last = declarations == null || round >= CloudToolCalls.MaxRounds;
                if (!last) body["tools"] = new JArray { new JObject { ["functionDeclarations"] = declarations } };
                JObject reply;
                using (var req = await RequestAsync(Url(false), body).ConfigureAwait(false))
                using (var res = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "Gemini refused the request: " + RestAuthSession.ErrorCode(text));
                    reply = JObject.Parse(text);
                }
                var content = reply["candidates"]?[0]?["content"] as JObject;
                var parts = content?["parts"] as JArray ?? new JArray();
                var calls = parts.Select(p => p["functionCall"]).OfType<JObject>().ToList();
                if (last || calls.Count == 0)
                {
                    var answer = Text(parts);
                    if (string.IsNullOrWhiteSpace(answer)) throw new HypaFirebaseException(200, "Gemini sent no answer.");
                    return answer;
                }
                contents.Add(content);
                var answers = new JArray();
                foreach (var call in calls) answers.Add(await CloudToolCalls.RunAsync(tools, call, ct).ConfigureAwait(false));
                contents.Add(new JObject { ["role"] = "function", ["parts"] = answers });
            }
        }

        public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct)
        {
            using (var req = await RequestAsync(Url(true), new JObject { ["contents"] = new JArray { UserTurn(prompt) } }).ConfigureAwait(false))
            using (var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!res.IsSuccessStatusCode)
                {
                    var error = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HypaFirebaseException((int)res.StatusCode, "Gemini refused the request: " + RestAuthSession.ErrorCode(error));
                }
                using (ct.Register(res.Dispose))
                using (var reader = new StreamReader(await res.Content.ReadAsStreamAsync().ConfigureAwait(false)))
                {
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                        var chunk = JObject.Parse(line.Substring(5).Trim());
                        var text = Text(chunk["candidates"]?[0]?["content"]?["parts"] as JArray);
                        if (!string.IsNullOrEmpty(text)) yield return text;
                    }
                }
            }
        }

        static JObject UserTurn(string prompt) => new JObject { ["role"] = "user", ["parts"] = new JArray { new JObject { ["text"] = prompt ?? "" } } };

        static string Text(JArray parts)
            => parts == null ? "" : string.Concat(parts.Where(p => p["thought"]?.Value<bool>() != true).Select(p => (string)p["text"] ?? ""));

        async Task<HttpRequestMessage> RequestAsync(string url, JObject body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("x-goog-api-key", _config.ApiKey);
            req.Headers.TryAddWithoutValidation(ProxyApi.AppCheckHeader, await _auth.AppCheckTokenAsync().ConfigureAwait(false));
            req.Headers.TryAddWithoutValidation("Authorization", "Firebase " + await _auth.IdTokenAsync().ConfigureAwait(false));
            return req;
        }
    }
}
