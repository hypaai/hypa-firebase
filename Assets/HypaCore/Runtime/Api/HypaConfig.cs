using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HypaCore
{
    /// <summary>Public proxy configuration only. Hypa credentials belong on the server.</summary>
    public sealed class HypaConfig
    {
        public const string DefaultBaseUrl = ""; // Set after the authenticated proxy is deployed.
        public const string FileName = "hypa.local.json";
        public const string EnvFileName = ".env";
        public string BaseUrl { get; }
        public bool HasProxy => !string.IsNullOrEmpty(BaseUrl);
        public bool DebugMode { get; }
        public string AuthEmulatorUrl { get; }

        public HypaConfig(string baseUrl = null, bool debugMode = false, string authEmulatorUrl = null)
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
            if (HasProxy && (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))))
                throw new ArgumentException("Proxy URL must use HTTPS, or HTTP on loopback for emulators.", nameof(baseUrl));
            DebugMode = debugMode;
            AuthEmulatorUrl = authEmulatorUrl;
        }

        static HypaConfig _current;
        public static HypaConfig Current => _current ?? (_current = Load());
        static string ConfigDir => Application.isEditor
            ? Directory.GetParent(Application.dataPath).FullName : Application.persistentDataPath;
        public static string LocalFilePath => Path.Combine(ConfigDir, FileName);
        public static string EnvFilePath => Path.Combine(ConfigDir, EnvFileName);

        public static HypaConfig Load()
        {
            var resource = Resources.Load<TextAsset>("hypa_client_config");
            var config = resource != null ? JObject.Parse(resource.text) : new JObject();
            string url = (string)config["proxyBaseUrl"];
            string debug = null;
            string authEmulatorUrl = null;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (string.IsNullOrWhiteSpace(url) && Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var page)
                && (page.Scheme == "https" || (page.Scheme == "http" && page.IsLoopback)))
                url = page.GetLeftPart(UriPartial.Authority);
#endif
            if (Application.isEditor)
            {
                url = Environment.GetEnvironmentVariable("HYPA_BASE_URL") ?? url;
                debug = Environment.GetEnvironmentVariable("DEBUG");
                authEmulatorUrl = Environment.GetEnvironmentVariable("HYPA_FIREBASE_AUTH_EMULATOR_URL");
                if (File.Exists(EnvFilePath))
                {
                    var env = ParseEnv(File.ReadAllText(EnvFilePath));
                    if (string.IsNullOrEmpty(url)) env.TryGetValue("HYPA_BASE_URL", out url);
                    if (string.IsNullOrEmpty(debug)) env.TryGetValue("DEBUG", out debug);
                    if (string.IsNullOrEmpty(authEmulatorUrl)) env.TryGetValue("HYPA_FIREBASE_AUTH_EMULATOR_URL", out authEmulatorUrl);
                }
            }
            return new HypaConfig(url, IsTrue(debug), authEmulatorUrl);
        }

        /// <summary>true / 1 / yes / on, any case.</summary>
        public static bool IsTrue(string v)
        {
            v = v?.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "on";
        }

        /// <summary>Minimal dotenv parser: KEY=VALUE lines, # comments, optional "export ", optional quotes.</summary>
        public static Dictionary<string, string> ParseEnv(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("export ")) line = line.Substring(7).TrimStart();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0]) v = v.Substring(1, v.Length - 2);
                else { int hash = v.IndexOf(" #", StringComparison.Ordinal); if (hash >= 0) v = v.Substring(0, hash).TrimEnd(); }
                d[k] = v;
            }
            return d;
        }

        /// <summary>For tests and for switching to the proxy at runtime.</summary>
        public static void Override(HypaConfig config) => _current = config;
    }
}
