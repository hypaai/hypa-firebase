#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace HypaCore
{
    /// <summary>
    /// The browser microphone in WebGL builds, where Unity's Microphone class does not work (Assets/Plugins/WebGL/HypaMicrophone.jslib).
    /// 16 kHz mono with echo cancellation, noise suppression and auto gain. Start it from a tap (browsers ask for the microphone
    /// and only start audio after a gesture), then <see cref="Read"/> new samples every frame.
    /// </summary>
    public static class WebMicrophone
    {
        public const int Rate = 16000;

        public enum State { Failed = -2, Denied = -1, Idle = 0, Starting = 1, Recording = 2 }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void HypaMic_Start();
        [DllImport("__Internal")] static extern void HypaMic_Stop();
        [DllImport("__Internal")] static extern int HypaMic_State();
        [DllImport("__Internal")] static extern int HypaMic_Read(float[] buffer, int max);

        public static void Start() => HypaMic_Start();
        public static void Stop() => HypaMic_Stop();
        public static State Current => (State)HypaMic_State();
        /// <summary>Copies samples recorded since the last read into <paramref name="buffer"/> and returns how many.</summary>
        public static int Read(float[] buffer) => HypaMic_Read(buffer, buffer.Length);
#else
        public static void Start() { }
        public static void Stop() { }
        public static State Current => State.Failed;
        public static int Read(float[] buffer) => 0;
#endif
    }
}
