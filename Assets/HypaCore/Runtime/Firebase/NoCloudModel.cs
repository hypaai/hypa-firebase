using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HypaCore.Hybrid
{
    public sealed class NoCloudModel : ICloudModel
    {
        static HypaException NotConnected() => new HypaException(HypaErrorKind.MissingConfiguration, "Cloud Gemini is not connected on this device.");

        public Task<string> GenerateAsync(string prompt, CancellationToken ct) => Task.FromException<string>(NotConnected());

        public Task<string> GenerateAsync(string prompt, IReadOnlyList<CloudTool> tools, CancellationToken ct) => Task.FromException<string>(NotConnected());

        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct) => throw NotConnected();
    }
}
