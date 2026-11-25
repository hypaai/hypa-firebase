using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HypaCore
{
    /// <summary>WebGL-safe async delay: uses Unity's main loop in single-threaded WebGL where Task.Delay can hang.</summary>
    public static class HypaAsync
    {
        public static async Task Delay(int milliseconds, CancellationToken ct = default)
        {
            if (milliseconds <= 0) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            var end = Time.realtimeSinceStartupAsDouble + milliseconds / 1000.0;
            while (Time.realtimeSinceStartupAsDouble < end)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
#else
            await Task.Delay(milliseconds, ct);
#endif
        }
    }
}
