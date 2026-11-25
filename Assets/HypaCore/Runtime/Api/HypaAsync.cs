using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HypaCore
{
    /// <summary>WebGL-safe async helpers using Unity 6 Awaitable so single-threaded WebGL never hangs or overflows the stack.</summary>
    public static class HypaAsync
    {
        public static async Task Delay(int milliseconds, CancellationToken ct = default)
        {
            if (milliseconds <= 0) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            await Awaitable.WaitForSecondsAsync(milliseconds / 1000f, ct);
#else
            await Task.Delay(milliseconds, ct);
#endif
        }

        public static async Task NextFrame(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
#if UNITY_WEBGL && !UNITY_EDITOR
            await Awaitable.NextFrameAsync(ct);
#else
            await Task.Yield();
#endif
        }
    }
}
