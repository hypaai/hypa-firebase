using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HypaFirebase.Rest
{
    public sealed class RestRemoteFlags : IRemoteFlags
    {
        public const string FidKey = "hypa.installations.fid", RefreshKey = "hypa.installations.refresh", ValuesKey = "hypa.remoteConfig.values";
        public const string InstallationsUrl = "https://firebaseinstallations.googleapis.com/v1";
        public const string RemoteConfigUrl = "https://firebaseremoteconfig.googleapis.com/v1";
        const string SdkVersion = "1.0.0";

        readonly FirebaseRestConfig _config;
        readonly IKeyValueStore _local;
        readonly HttpClient _http;
        readonly Func<DateTime> _now;
        volatile Dictionary<string, string> _values;
        string _token;
        DateTime _tokenExpires;

        public RestRemoteFlags(FirebaseRestConfig config, IKeyValueStore local, HttpMessageHandler handler = null, Func<DateTime> now = null)
        {
            _config = config;
            _local = local;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
            _now = now ?? (() => DateTime.UtcNow);
            _values = Read(_local.Get(ValuesKey));
        }

        public string Get(string key, string fallback)
            => _values.TryGetValue(key, out var v) ? v : (RemoteConfigKeys.Default(key) ?? fallback);

        public async Task FetchAsync()
        {
            JObject reply;
            try
            {
                var token = await TokenAsync().ConfigureAwait(false);
                var body = new JObject
                {
                    ["sdk_version"] = SdkVersion,
                    ["app_instance_id"] = _local.Get(FidKey),
                    ["app_instance_id_token"] = token,
                    ["app_id"] = _config.AppId,
                    ["language_code"] = CultureInfo.CurrentCulture.Name,
                };
                reply = await PostAsync($"{RemoteConfigUrl}/projects/{_config.ProjectId}/namespaces/firebase:fetch?key={_config.ApiKey}", body, null).ConfigureAwait(false);
            }
            catch (HttpRequestException) { return; }
            catch (TaskCanceledException) { return; }

            var state = (string)reply["state"];
            if (state == "NO_CHANGE" || state == "INSTANCE_STATE_UNSPECIFIED") return;
            var values = new Dictionary<string, string>();
            if (reply["entries"] is JObject entries)
                foreach (var entry in entries.Properties()) values[entry.Name] = (string)entry.Value;
            _values = values;
            _local.Set(ValuesKey, JsonConvert.SerializeObject(values));
        }

        async Task<string> TokenAsync()
        {
            if (_token != null && _now() < _tokenExpires) return _token;
            var fid = _local.Get(FidKey);
            var refresh = _local.Get(RefreshKey);
            if (string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(refresh))
            {
                var created = await PostAsync($"{InstallationsUrl}/projects/{_config.ProjectId}/installations", new JObject
                {
                    ["fid"] = NewFid(), ["authVersion"] = "FIS_v2", ["appId"] = _config.AppId, ["sdkVersion"] = SdkVersion,
                }, null).ConfigureAwait(false);
                _local.Set(FidKey, (string)created["fid"]);
                _local.Set(RefreshKey, (string)created["refreshToken"]);
                return Keep(created["authToken"] as JObject);
            }
            try
            {
                var token = await PostAsync($"{InstallationsUrl}/projects/{_config.ProjectId}/installations/{fid}/authTokens:generate",
                    new JObject { ["installation"] = new JObject { ["sdkVersion"] = SdkVersion, ["appId"] = _config.AppId } }, "FIS_v2 " + refresh).ConfigureAwait(false);
                return Keep(token);
            }
            catch (HypaFirebaseException e) when (e.StatusCode == 401 || e.StatusCode == 404)
            {
                _local.Remove(FidKey);
                _local.Remove(RefreshKey);
                return await TokenAsync().ConfigureAwait(false);
            }
        }

        string Keep(JObject token)
        {
            _token = (string)token?["token"];
            var seconds = double.Parse(((string)token?["expiresIn"] ?? "3600s").TrimEnd('s'), CultureInfo.InvariantCulture);
            _tokenExpires = _now() + TimeSpan.FromSeconds(Math.Max(0, seconds - 60));
            return _token;
        }

        async Task<JObject> PostAsync(string url, JObject body, string authorization)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.TryAddWithoutValidation("x-goog-api-key", _config.ApiKey);
                if (authorization != null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var res = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw new HypaFirebaseException((int)res.StatusCode, "Firebase refused the request: " + RestAuthSession.ErrorCode(text));
                    return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                }
            }
        }

        public static string NewFid()
        {
            var bytes = new byte[17];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            bytes[0] = (byte)(0x70 | (bytes[0] & 0x0F));
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Substring(0, 22);
        }

        static Dictionary<string, string> Read(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, string>();
            try { return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(); }
            catch (JsonException) { return new Dictionary<string, string>(); }
        }
    }
}
