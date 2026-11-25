using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HypaCore.Hybrid;
using UnityEngine;

namespace HypaFirebase.Native
{
    /// <summary>
    /// Development iOS builds (tools/build.sh ios-sim, or a Development Build): asks the on-device model one question and one
    /// stream at start-up and logs a report to the Xcode console, so the device test does not wait for G05's router.
    /// Release builds never run it.
    /// </summary>
    public static class AppleModelCheck
    {
        public const string Question = "In one sentence, what is a story?";
        public const string StreamQuestion = "Count from one to five in Spanish.";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnStart()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (Debug.isDebugBuild) _ = LogAsync();
#endif
        }

        static async Task LogAsync()
        {
            try { Debug.Log(await RunAsync(HybridServices.Current.OnDevice)); }
            catch (Exception e) { Debug.LogWarning($"HYPA ON-DEVICE CHECK\nRESULT: the check failed ({e.GetType().Name})."); }
        }

        /// <summary>The report: status, then an answer and a stream when the model is available.</summary>
        public static async Task<string> RunAsync(IOnDeviceModel model)
        {
            var report = new StringBuilder("HYPA ON-DEVICE CHECK\n");
            var status = await model.StatusAsync();
            report.Append("status: ").Append(status).Append(" (").Append(model.GetType().Name).Append(")\n");
            if (status != "available")
            {
                var why = model is AppleFoundationModel ? AppleFoundationModel.UnavailableReason : null;
                if (why != null) report.Append(why).Append('\n');
                return report.Append("RESULT: this device uses the cloud.").ToString();
            }
            try
            {
                var watch = Stopwatch.StartNew();
                var answer = await model.GenerateAsync(Question, CancellationToken.None);
                report.Append($"[PASS] answer in {watch.Elapsed.TotalSeconds:0.0} s: {answer.Trim()}\n");
                var pieces = 0;
                var text = new StringBuilder();
                await foreach (var chunk in model.StreamAsync(StreamQuestion, CancellationToken.None)) { pieces++; text.Append(chunk); }
                report.Append($"[PASS] stream in {pieces} pieces: {text.ToString().Trim().Replace("\n", " ")}\n");
                return report.Append("RESULT: answered on this device.").ToString();
            }
            catch (OnDeviceModelException e)
            {
                return report.Append($"[FAIL] {e.Code}\nRESULT: the router would use the cloud.").ToString();
            }
        }
    }
}
