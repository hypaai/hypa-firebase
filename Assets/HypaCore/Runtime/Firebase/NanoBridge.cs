using System;

namespace HypaCore.Hybrid
{
    /// <summary>
    /// What <see cref="AndroidNanoModel"/> needs from the phone's on-device model (Gemini Nano, through ML Kit's Prompt API).
    /// <see cref="AndroidNanoBridge"/> is the real one. Tests use a fake. Every method may be called from a worker thread and must
    /// not throw: a failure is reported as a status of "unavailable" or as a reason to the sink.
    /// </summary>
    public interface INanoBridge
    {
        /// <summary>"available", "downloadable", "downloading" or "unavailable". Blocks for up to a few seconds: never on the main thread.</summary>
        string Status();
        /// <summary>Starts a reply and returns at once. The sink hears the text, then exactly one of Done and Error.</summary>
        void Generate(string prompt, int maxTokens, float temperature, INanoTextSink sink);
        /// <summary>Starts the model download and returns at once.</summary>
        void Download(INanoDownloadSink sink);
        /// <summary>Stops the reply in progress, if any.</summary>
        void Cancel();
    }

    /// <summary>Receives a streamed reply, on any thread.</summary>
    public interface INanoTextSink
    {
        void Text(string chunk);
        void Done();
        /// <summary>A short reason: "busy", "background", "quota", "no-space", "needs-update", "unavailable", "too-large", "too-small", "cancelled" or "error".</summary>
        void Error(string reason);
    }

    /// <summary>Receives the model download, on any thread.</summary>
    public interface INanoDownloadSink
    {
        void Started(long totalBytes);
        void Progress(long downloadedBytes);
        void Done();
        void Error(string reason);
    }

    /// <summary>
    /// The on-device model could not answer. <see cref="Reason"/> is one of the short reasons above. The message never carries the
    /// prompt or the reply.
    /// </summary>
    public sealed class OnDeviceModelException : Exception
    {
        public string Reason { get; }

        public OnDeviceModelException(string reason) : base("The on-device model could not answer (" + reason + ").")
        {
            Reason = reason;
        }
    }

    /// <summary>An on-device model that has to download its model first. The settings screen offers it when the status is "downloadable".</summary>
    public interface IDownloadableOnDeviceModel : IOnDeviceModel
    {
        /// <summary>
        /// Downloads the model, reporting 0..1. Completes when it is ready and throws <see cref="OnDeviceModelException"/> if it fails.
        /// Cancelling stops the wait; the system may finish the download anyway.
        /// </summary>
        System.Threading.Tasks.Task DownloadAsync(IProgress<float> progress, System.Threading.CancellationToken ct);
    }
}
