using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using UnityEngine;

namespace HypaCore.Hybrid
{
    public static class NetworkStatus
    {
        public const double DownSeconds = 30;
        static DateTime _downUntil = DateTime.MinValue;

        public static Func<bool> DeviceOnline = () => Application.internetReachability != NetworkReachability.NotReachable;
        public static Func<DateTime> Now = () => DateTime.UtcNow;

        public static bool Online => DeviceOnline() && Now() >= _downUntil;

        public static void MarkUnreachable() => _downUntil = Now().AddSeconds(DownSeconds);
        public static void MarkReachable() => _downUntil = DateTime.MinValue;

        public static bool IsNetworkError(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is HttpRequestException || x is WebException || x is SocketException) return true;
                if (x is HypaException h && h.Kind == HypaErrorKind.Offline) return true;
                if (x is HypaProxyException p && p.Status == 0 && p.Code == ProxyApi.ErrorUpstream) return true;
            }
            return false;
        }
    }
}
